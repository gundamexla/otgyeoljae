using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VoltiEngine;

public sealed record SavedSite(string Name, string Url);

public sealed class SiteRegistry
{
    private readonly string connectionString;
    private readonly Action<string> launch;
    public SiteRegistry(string directory, Action<string>? launch = null)
    {
        this.launch = launch ?? LaunchChrome;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "sites.sqlite3"), Pooling = false }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS sites (name TEXT PRIMARY KEY, url TEXT NOT NULL)";
        command.ExecuteNonQuery();
        // User-requested companion sites, verified from the owning project chats.
        // Existing Moa registrations take precedence; Volti data is never imported.
        foreach (var site in new[] {
            new SavedSite("갈피", "https://galpi-ai-studio.vercel.app/"),
            new SavedSite("금융", "https://moa-finance-hackathon.jrnanmari.chatgpt.site/"),
            new SavedSite("의상 착장", "https://oba-fitting-hackathon.jrnanmari.chatgpt.site/"),
            new SavedSite("병원", "https://moa-neighborhood-clinic.jrnanmari.chatgpt.site/"),
            new SavedSite("3D 착장", "https://oba-fitting-studio.jrnanmari.chatgpt.site/"),
            new SavedSite("의상 자료실", "https://oba-wardrobe.jrnanmari.chatgpt.site/"),
            new SavedSite("재고 관리", "https://thread-stock-boutique-1107.jrnanmari.chatgpt.site/"),
            new SavedSite("프로젝트 자료실", "https://oba-run-archive.jrnanmari.chatgpt.site/") })
        {
            var existing = List();
            if (existing.Length < 100 && !existing.Any(s => Key(s.Name) == Key(site.Name))) Save(site.Name, site.Url);
        }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }
    private static string Key(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", "");
    public SavedSite[] List()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name,url FROM sites ORDER BY name";
        using var reader = command.ExecuteReader();
        var sites = new List<SavedSite>();
        while (reader.Read()) sites.Add(new(reader.GetString(0), reader.GetString(1)));
        return sites.ToArray();
    }
    public static string ValidateUrl(string url)
    {
        if (url.Length > 1500 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Host.Length == 0)
            throw new ApiFailure(400, "invalid_site", "사이트 주소는 http:// 또는 https://로 시작하는 주소로 적어 줘.");
        return uri.AbsoluteUri;
    }
    public SavedSite Save(string name, string url)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new ApiFailure(400, "invalid_site", "사이트 이름을 1~60자로 적어 줘.");
        var sites = List();
        name = sites.FirstOrDefault(s => Key(s.Name) == Key(name))?.Name ?? name;
        if (sites.Length >= 100 && !sites.Any(s => s.Name == name))
            throw new ApiFailure(400, "site_limit", "등록 가능한 사이트는 100개예요.");
        var site = new SavedSite(name, ValidateUrl(url));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO sites(name,url) VALUES($name,$url) ON CONFLICT(name) DO UPDATE SET url=excluded.url";
        command.Parameters.AddWithValue("$name", site.Name);
        command.Parameters.AddWithValue("$url", site.Url);
        command.ExecuteNonQuery();
        return site;
    }
    public SavedSite[] Find(string name)
    {
        var key = Key(name);
        var sites = List();
        var exact = sites.Where(s => Key(s.Name) == key).ToArray();
        if (exact.Length > 0) return exact;
        var aliases = sites.Where(s => Aliases(s).Any(a => Key(a) == key)).ToArray();
        return aliases.Length > 0 ? aliases : sites.Where(s => Key(s.Name).Contains(key)).ToArray();
    }
    public static string[] Aliases(SavedSite site) => Key(site.Name) switch
    {
        "금융" => [site.Name, "금융 사이트", "가상금융", "모아 금융", "금융 시뮬레이터"],
        "의상착장" => [site.Name, "착장", "착장 사이트", "의상", "시착", "시착 사이트", "피팅", "피팅 사이트", "OBA Fitting Lab"],
        "병원" => [site.Name, "병원 사이트", "모아 병원", "동네의원", "모아 동네의원", "가상 병원", "의원", "클리닉"],
        "3d착장" => [site.Name, "3D 스튜디오", "착장 스튜디오", "OBA Studio"],
        "의상자료실" => [site.Name, "옷장", "의상 자료", "OBA 옷장", "옷 자료실"],
        "재고관리" => [site.Name, "재고", "매장 재고", "의상 재고", "THREAD", "포스기", "가상 매장"],
        "프로젝트자료실" => [site.Name, "아이디어", "해커톤 자료실", "제작 자료실", "OBA.RUN 자료실"],
        _ => [site.Name]
    };
    public string? TryHandle(string text, bool native, CancellationToken token, SkillContext? context = null)
    {
        context ??= new();
        if (text.StartsWith("/사이트 외부 "))
        {
            if (!native) throw new ApiFailure(403, "native_only", "사이트 연결은 Windows 모아에서 해 줘.");
            token.ThrowIfCancellationRequested();
            var external = Find(text[8..].Trim());
            if (external.Length != 1) return "등록된 사이트 이름을 하나로 말해 줘.";
            launch(ValidateUrl(external[0].Url));
            return external[0].Name + ", 기본 프로필의 크롬에 열기 요청을 보냈어요. 필요한 로그인은 직접 진행해 주세요.\n" + external[0].Url;
        }
        var selection = text.Trim().TrimEnd('.', '!', '?');
        var selected = context.PendingSites.Where(name => Key(name) == Key(selection)).ToArray();
        if (selected.Length == 0)
        {
            var withoutParticle = Regex.Replace(selection, @"(?:으로|로|을|를)$", "").Trim().Trim('"', '\'', '‘', '’', '“', '”');
            selected = context.PendingSites.Where(name => Key(name) == Key(withoutParticle)).ToArray();
        }
        context.PendingSites = [];
        var register = Regex.Match(text, @"^/?사이트\s*등록\s+(.+?)\s+(https?://\S+)$");
        var open = Regex.Match(text, @"^(.*?)\s*(?:사이트\s*)?(?:연결해\s*줘요?|열어\s*줘요?|열어\s*주세요|연결해\s*주세요)[.!?]*$");
        bool command = selection is "/사이트" or "/사이트 목록" or "사이트 목록" or "내 사이트" or "내 사이트 목록" or "내 사이트 목록 보여줘" or "사이트 목록 보여줘" or "등록된 사이트 보여줘" or "등록된 사이트" or "사이트 뭐 있어" || text.StartsWith("/사이트 ");
        // Generic app-opening requests continue to the existing desktop command handler.
        SavedSite[] RequestedSites(string name)
        {
            name = name.Trim().Trim('"', '\'', '‘', '’', '“', '”');
            if (name.Length == 0) return [];
            var exact = Find(name);
            if (exact.Length > 0) return exact;
            name = Regex.Replace(name, @"(?:으로|로|을|를)$", "").Trim().Trim('"', '\'', '‘', '’', '“', '”');
            return name.Length == 0 ? [] : Find(name);
        }
        bool siteOpen = open.Success && (RequestedSites(open.Groups[1].Value).Length > 0 || text.Contains("사이트") || text.Contains("연결"));
        if (!register.Success && !command && !siteOpen && selected.Length == 0) return null;
        if (!native) throw new ApiFailure(403, "native_only", "내 사이트 목록과 크롬 열기는 Windows 모아에서 사용할 수 있어요.");
        token.ThrowIfCancellationRequested();
        if (register.Success)
        {
            var saved = Save(register.Groups[1].Value, register.Groups[2].Value);
            return $"{saved.Name} 등록했어. ‘{saved.Name} 연결해줘’라고 말하면 크롬으로 열어요.\n{saved.Url}";
        }
        if (command)
        {
            var all = List();
            context.PendingSites = all.Select(s => s.Name).ToArray();
            return "등록된 사이트예요. 열고 싶은 이름을 골라 줘.\n" + string.Join("\n", all.Select(s => $"{s.Name} · {s.Url}")) +
                "\n추가하려면 ‘사이트 등록 이름 https://주소’라고 적어 줘.";
        }
        var candidates = RequestedSites(selected.Length == 1 ? selected[0] : open.Groups[1].Value);
        if (candidates.Length != 1)
        {
            context.PendingSites = (candidates.Length > 0 ? candidates : List()).Select(s => s.Name).ToArray();
            return "어느 사이트를 열까요? 아래 이름을 골라 줘.\n" + string.Join("\n", context.PendingSites);
        }
        var target = candidates[0];
        var url = ValidateUrl(target.Url);
        token.ThrowIfCancellationRequested();
        launch(url);
        return $"{target.Name}, 크롬에 열기 요청을 보냈어요.\n{url}";
    }
    private static void LaunchChrome(string url)
    {
        string? chrome = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
            .Where(p => p.Length > 0).Select(p => Path.Combine(p, "Google", "Chrome", "Application", "chrome.exe")).FirstOrDefault(File.Exists);
        if (chrome is null) throw new ApiFailure(503, "chrome_missing", "크롬 실행 파일을 찾지 못했어요. Google Chrome 설치를 확인해 줘.");
        try
        {
            var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
            start.ArgumentList.Add("--new-tab"); start.ArgumentList.Add(url);
            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new ApiFailure(502, "chrome_failed", "크롬을 시작하지 못했어요. 잠시 뒤 다시 열어 줘."); }
    }
}
