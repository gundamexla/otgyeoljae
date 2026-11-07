using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed class FileWorkflows(EngineOptions options, DesktopTools desktop)
{
    public string[]? SearchRoots { get; init; }
    public Func<string, CancellationToken, Task>? RevealFile { get; init; }
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", ".venv", "venv", ".gpu-venv", "__pycache__", "AppData", ".cache" };

    public string NoteFolder()
    {
        var folder = Path.Combine(options.DataPath, "notes");
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new ApiFailure(400, "note_folder_link", "메모 폴더가 연결 경로여서 저장하지 않았어요.");
        return folder;
    }
    public async Task<string?> TryHandle(string text, bool native, SkillContext context, CancellationToken token)
    {
        var save = Regex.Match(text, @"^(?:메모장(?:\s*내용)?|(?:그|이)\s*창(?:\s*내용)?|지금\s*내용)(?:을|를)?\s*(?:(?<name>.+?)(?:으로|로)\s*)?(?:파일로\s*)?저장해\s*줘(?:요)?[.!?]*$");
        var find = Regex.Match(text, @"^(?:(?:내\s*)?(?:PC|컴퓨터)에서\s*)?(?<query>.+?)\s*파일(?:을)?\s*찾아\s*줘(?:요)?[.!?]*$", RegexOptions.IgnoreCase);
        if (text.StartsWith("/찾기 ")) find = Regex.Match(text, @"^/찾기\s+(?<query>.+)$");
        var reveal = Regex.Match(text, @"^(?:(?:탐색기에서|탐색기로)\s*)?(?<which>그|이|저장한|첫\s*번째|두\s*번째|세\s*번째|\d+\s*번)\s*파일(?:을)?\s*(?:(?:탐색기에서|탐색기로)\s*)?(?:보여\s*줘|열어\s*줘|위치\s*알려\s*줘)[.!?]*$");
        if (!save.Success && !find.Success && !reveal.Success) return null;
        if (!native) throw new ApiFailure(403, "native_only", "파일 작업은 Windows 모아에서 해 줘.");
        if (save.Success)
        {
            var name = save.Groups["name"].Value.Trim().Trim('‘', '’', '"', '“', '”');
            if (name is "파일" or "새 파일") name = "";
            if (name.Length == 0) name = "모아-메모-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4] + ".txt";
            if (!Path.HasExtension(name)) name += ".txt";
            if (name.Length > 100 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl) ||
                name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith('.') ||
                !Path.GetExtension(name).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
                return "파일 이름만 적어 줘. 예: ‘메모장 내용 오늘메모.txt로 저장해줘’. 새 텍스트 파일로 저장해요.";
            var content = await desktop.ReadNotepad(context, token);
            var path = Path.Combine(NoteFolder(), name);
            token.ThrowIfCancellationRequested();
            try
            {
                // A completed local write/readback is kept together even if the caller cancels now.
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
            }
            catch (IOException) when (File.Exists(path)) { return $"‘{name}’ 파일이 이미 있어요. 다른 이름으로 저장해 줘."; }
            if (File.ReadAllText(path, Encoding.UTF8) != content) throw new ApiFailure(502, "file_verify_failed", "파일은 만들었지만 내용을 확인하지 못했어요. 다시 저장하기 전에 파일을 확인해 줘.");
            context.Files = [path];
            return $"메모장 내용을 새 파일 ‘{name}’로 저장하고 확인했어요.\n{path}\n원래 메모장 탭은 그대로예요. ‘그 파일 보여줘’로 위치를 열 수 있어요.";
        }
        if (find.Success) return Search(find.Groups["query"].Value.Trim().Trim('‘', '’', '"', '“', '”'), context, token);
        var key = Regex.Replace(reveal.Groups["which"].Value, @"\s+", "");
        int index = key switch { "첫번째" => 1, "두번째" => 2, "세번째" => 3, _ => int.TryParse(key.Replace("번", ""), out var i) ? i : context.Files.Length == 1 ? 1 : 0 };
        if (index < 1 || index > context.Files.Length) return "먼저 파일을 찾고 번호를 골라 줘. 예: ‘회의 파일 찾아줘’, ‘2번 파일 보여줘’.";
        var selected = context.Files[index - 1];
        if (!File.Exists(selected) || IsLinked(selected)) return "그 파일이 이동했거나 연결 경로로 바뀌었어요. 다시 찾아 줘.";
        token.ThrowIfCancellationRequested();
        if (RevealFile is not null) await RevealFile(selected, token);
        else
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            // Explorer parses /select itself. Quoting that flag together with a spaced
            // path opens Documents instead; keep the literal flag and path separate.
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(selected);
            using var process = Process.Start(start) ?? throw new ApiFailure(502, "explorer_launch_failed", "탐색기를 열지 못했어요.");
        }
        return $"‘{Path.GetFileName(selected)}’ 위치를 탐색기로 열도록 요청했어요.\n{selected}";
    }

    public string Search(string query, SkillContext context, CancellationToken token)
    {
        if (query.Length is < 1 or > 100 || query.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0)
            return "찾을 파일 이름의 일부를 적어 줘. 예: ‘회의 파일 찾아줘’.";
        var roots = SearchRoots ?? [NoteFolder(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")];
        var matches = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (var root in roots.Where(Directory.Exists)) queue.Enqueue((Path.GetFullPath(root), 0));
        int visited = 0; bool limited = false; var watch = Stopwatch.StartNew();
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (folder, depth) = queue.Dequeue();
            if (!seen.Add(folder) || IsLinked(folder)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 20000 || watch.Elapsed.TotalSeconds > 4 || matches.Count >= 20) { limited = true; break; }
                    var attr = File.GetAttributes(path);
                    if ((attr & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) != 0) continue;
                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        if (!SkipDirectories.Contains(Path.GetFileName(path)))
                        {
                            if (depth < 7) queue.Enqueue((path, depth + 1)); else limited = true;
                        }
                    }
                    else if (Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase) && !matches.Contains(path, StringComparer.OrdinalIgnoreCase)) matches.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { limited = true; }
            if (visited > 20000 || watch.Elapsed.TotalSeconds > 4 || matches.Count >= 20) break;
        }
        context.Files = matches.ToArray();
        return "바탕화면·문서·다운로드·모아 메모에서 파일 이름을 찾았어요.\n" +
            (matches.Count == 0 ? "일치하는 파일이 없어요." : string.Join("\n", matches.Select((p, i) => $"{i + 1}. {p}")) + "\n‘1번 파일 보여줘’로 탐색기에서 볼 수 있어요.") +
            (limited ? "\n검색 범위·시간 제한으로 일부 폴더는 생략했어요." : "");
    }
    private static bool IsLinked(string path)
    {
        try
        {
            for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
}
