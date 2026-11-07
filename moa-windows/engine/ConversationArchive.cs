using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VoltiEngine;

public sealed record StoredConversation(string Id, string Owner, string Path, ConversationRules Rules, string[] Requests);

// Obsidian Markdown is the transcript source. SQLite contains only ownership,
// location, preferences and replay IDs; it does not contain the conversation text.
public sealed class ConversationArchive
{
    private const string Start = "<!-- volti-log -->";
    private const string End = "<!-- /volti-log -->";
    private readonly string connectionString;
    public string DirectoryPath { get; }
    public ConversationArchive(string dataDirectory, string? vaultDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DirectoryPath = Path.GetFullPath(vaultDirectory ?? Path.Combine(dataDirectory, "conversations"));
        Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "conversation-index.sqlite3"), Pooling = false }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, owner TEXT NOT NULL, path TEXT NOT NULL, rules TEXT NOT NULL, requests TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public StoredConversation? Find(string id, string owner)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT path,rules,requests FROM conversations WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(id, owner, reader.GetString(0), JsonSerializer.Deserialize<ConversationRules>(reader.GetString(1), JsonDefaults.Options) ?? new(),
            JsonSerializer.Deserialize<string[]>(reader.GetString(2), JsonDefaults.Options) ?? []);
    }
    public void Create(string id, string owner)
    {
        var now = DateTimeOffset.Now;
        string path = Path.Combine(DirectoryPath, $"{now:yyyy-MM-dd_HHmmss}_모아대화_{id[..8]}.md");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            writer.Write($"---\ntype: moa-conversation\nproject: 모아\ncategory: 개발·AI협업\nsource: 모아 로컬 엔진의 완료된 대화\ncreated: {now:yyyy-MM-dd}\nchecked: {now:yyyy-MM-dd}\nstatus: 로컬 대화 기록\nclient: {(owner == "native" ? "Windows" : "연결 사이트")}\n---\n# 모아와 나눈 대화\n\n대화 원문은 이 노트에 이어 저장합니다. 자동 기록 구간 밖에는 자유롭게 메모할 수 있어요.\n\n{Start}\n{End}\n");
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO conversations(id,owner,path,rules,requests) VALUES($id,$owner,$path,$rules,'[]')";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(new ConversationRules(), JsonDefaults.Options)); command.ExecuteNonQuery();
    }
    public void SaveState(string id, string owner, ConversationRules rules, string[] requests)
    {
        using var db = Open(); using var command = db.CreateCommand();
        // UPDATE cannot resurrect a revoked/deleted conversation.
        command.CommandText = "UPDATE conversations SET rules=$rules,requests=$requests WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(rules, JsonDefaults.Options));
        command.Parameters.AddWithValue("$requests", JsonSerializer.Serialize(requests, JsonDefaults.Options));
        if (command.ExecuteNonQuery() != 1) throw new ApiFailure(404, "session_not_found", "대화 기록이 해제됐어요. 새 대화를 열어 줘.");
    }
    public ChatMessage[] Read(string id, string owner) => Parse(ReadDocument(Required(id, owner).Path)).TakeLast(1000).ToArray();
    private StoredConversation Required(string id, string owner) => Find(id, owner) ?? throw new ApiFailure(404, "session_not_found", "대화 기록을 찾지 못했어요.");
    private static string ReadDocument(string path)
    {
        if (!File.Exists(path)) throw new ApiFailure(409, "conversation_note_missing", "Obsidian 대화 노트가 이동했거나 없어졌어요. 원래 위치로 돌리거나 새 대화를 시작해 줘.");
        if (new FileInfo(path).Length > 20_000_000) throw new ApiFailure(409, "conversation_note_large", "대화 노트가 커졌어요. 새 대화에서 이어가 줘.");
        return File.ReadAllText(path, Encoding.UTF8);
    }
    private static (int Start, int End) Bounds(string document)
    {
        var starts = Regex.Matches(document, @"(?m)^<!-- volti-log -->\r?$");
        var ends = Regex.Matches(document, @"(?m)^<!-- /volti-log -->\r?$");
        if (starts.Count != 1 || ends.Count != 1 || starts[0].Index >= ends[0].Index)
            throw new ApiFailure(409, "conversation_note_edited", "대화 노트의 자동 기록 구간이 바뀌었어요. 기존 내용을 덮어쓰지 않았어요.");
        return (starts[0].Index + starts[0].Length, ends[0].Index);
    }
    public static ChatMessage[] Parse(string document)
    {
        var bounds = Bounds(document);
        var lines = document[bounds.Start..bounds.End].Replace("\r\n", "\n").Split('\n');
        var messages = new List<ChatMessage>();
        for (int i = 0; i < lines.Length; i++)
        {
            var marker = Regex.Match(lines[i], @"^<!-- volti-message:(user|assistant) -->$");
            if (!marker.Success) continue;
            if (++i >= lines.Length || !lines[i].StartsWith("> [!")) throw new ApiFailure(409, "conversation_note_edited", "대화 노트의 메시지 형식이 바뀌었어요. 원문을 확인해 줘.");
            var content = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].StartsWith('>'))
            { i++; content.Add(lines[i].StartsWith("> ") ? lines[i][2..] : lines[i][1..]); }
            if (marker.Groups[1].Value != (messages.Count % 2 == 0 ? "user" : "assistant"))
                throw new ApiFailure(409, "conversation_note_edited", "대화 노트의 질문·답변 순서를 확인해 줘.");
            messages.Add(new(marker.Groups[1].Value, string.Join("\n", content)));
        }
        if (messages.Count % 2 != 0) throw new ApiFailure(409, "conversation_note_edited", "대화 노트에 짝이 없는 메시지가 있어요. 원문을 보존했어요.");
        return messages.ToArray();
    }
    public ChatMessage[] Append(string id, string owner, string requestId, string input, string answer)
    {
        var note = Required(id, owner); var current = ReadDocument(note.Path); var bounds = Bounds(current);
        _ = Parse(current);
        var marker = "<!-- volti-turn:" + requestId + " -->";
        if (current.Split('\n').Any(line => line.TrimEnd('\r') == marker)) return Parse(current).TakeLast(1000).ToArray();
        string Quote(string role, string title, string value) => $"<!-- volti-message:{role} -->\n> [!{(role == "user" ? "note" : "tip")}] {title}\n" +
            string.Join("\n", value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => "> " + line)) + "\n\n";
        var turn = $"\n{marker}\n### {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n\n" + Quote("user", "나", input) + Quote("assistant", "모아", answer);
        var updated = current.Insert(bounds.End, turn);
        ReplaceUnchanged(note.Path, current, updated);
        return Parse(updated).TakeLast(1000).ToArray();
    }
    private static void ReplaceUnchanged(string path, string expected, string updated)
    {
        var temporary = path + ".volti-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            // Preserve user edits made since the initial read rather than silently overwriting them.
            if (File.ReadAllText(path, Encoding.UTF8) != expected) throw new ApiFailure(409, "conversation_note_conflict", "Obsidian에서 대화 노트를 수정 중이에요. 편집 내용을 보존하고 저장을 멈췄어요.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Remove(string id, string owner)
    {
        var note = Find(id, owner);
        if (note is null) return;
        if (File.Exists(note.Path))
        {
            var current = ReadDocument(note.Path); var bounds = Bounds(current);
            ReplaceUnchanged(note.Path, current, current[..bounds.Start] + "\n\n대화 기록을 지웠어요.\n\n" + current[bounds.End..]);
        }
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner); command.ExecuteNonQuery();
    }
    public string[] Owned(string owner)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM conversations WHERE owner=$owner"; command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader(); var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0)); return ids.ToArray();
    }
}
