using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoltiEngine;

// Phase one: explicit, bounded commands. Model output is never executable.
public sealed class LocalSkills(EngineOptions options, MemoryStore memory, DesktopTools desktop, IPcInfoReader? pcReader = null)
{
    private readonly PcInfoSkills pcInfo = new(pcReader ?? new WindowsPcInfoReader());
    public bool PcInfoAvailable => pcInfo.Available;
    private readonly OnlineLookup lookup = new();
    private readonly SiteRegistry sites = new(options.DataPath);
    private readonly PurchaseScenario purchases = new(options.DataPath);
    private readonly FileWorkflows files = new(options, desktop);
    private BrowserWorkflows? browser;
    public async ValueTask DisposeAsync() { if (browser is not null) await browser.DisposeAsync(); }
    public const string SharedHelp = "‘(12+3)×4 계산해줘’, ‘서울 내일 날씨’, ‘오로라 검색해줘’처럼 물어봐. 계산은 PC에서 하고, 날씨는 Open-Meteo, 검색은 한국어 위키백과에서 찾아요.";
    public const string Help = "모아 로컬 기능\n" +
        SharedHelp + "\n" +
        "모아야, 계산기를 좀 켜줄래? · 메모장 열어줘 · 파일 탐색기 실행해줘\n" +
        "앞서 말한 앱·사이트는 ‘그거 열어줘’ · 앞선 내 말은 ‘방금 말한 내용 기억해줘’\n" +
        "금융 사이트 주소 알려줘 · 금융 열어줘 · 착장 열어줘 · 병원 연결해줘\n" +
        "갈피 열어줘 · 여기서 로봇을 찾아줘 · 자료 탐색 메뉴를 눌러줘 · 이전 페이지로 가줘\n" +
        "내가 좋아하는 색은 파랑이야. 기억해줘 · 내가 좋아하는 색이 뭐였지? · 저장한 기억 보여줘\n" +
        "사이트 목록 · https://주소 를 이름으로 등록해줘\n" +
        "내 PC 사양 · 내 RAM 용량 · 내 CPU · 내 GPU · 저장공간 알려줘 — 실제 PC 정보 읽기\n" +
        "/창 — 열린 창 목록\n/화면 창ID — 버튼·이름 읽기\n/앱 메모장 (또는 계산기, 탐색기)\n" +
        "메모장 화면 보여줘 · 메모장에 안녕 써줘 · 계산기 화면 보여줘\n/클릭 e번호 — 계산기 버튼\n/입력 e번호 내용 — 메모장 본문 뒤에 추가\n" +
        "2번 창 선택해줘 · 그 창에 다음 줄 써줘 · 메모장 내용 오늘메모.txt로 저장해줘\n회의 파일 찾아줘 · 그 파일 보여줘 — 파일 이름 검색·탐색기 위치 열기\n" +
        "/메모 내용 — 모아 메모 폴더에 새 파일 저장\n/파일 검색어 — 모아가 저장한 메모 파일 찾기\n/계산 (12+3)*4 — 사칙연산\n" +
        "/기억 저장 이름 = 내용 — 재시작 후에도 기억\n/기억 목록 · /기억 찾기 검색어 · /기억 삭제 이름\n" +
        "갈피 조작해줘 · 갈피에서 로봇 검색해줘 · 사이트에서 프로젝트 눌러줘\n/웹 화면 · /웹 클릭 w번호 · /웹 입력 w번호 검색어 · /웹 뒤로 · /웹 닫기\n" +
        "대화는 Markdown 노트에 자동 저장하고 재시작 뒤 이어가요. 최근 500회와 관련 대화를 참고해요. ‘대화 기록 지워줘’로 현재 대화를 비울 수 있어요. ‘기억해줘’로 부탁한 내용은 별도의 기억에 저장해요.";
    public async Task<string?> TryHandle(string text, bool native, CancellationToken token, SkillContext? context = null)
    {
        context ??= new();
        context.Dialogue = context.Dialogue.Apply(text);
        var wasWeather = context.LastWeather;
        context.LastWeather = false;
        return await Handle(text, native, token, context, wasWeather);
    }
    private async Task<string?> Handle(string text, bool native, CancellationToken token, SkillContext context, bool wasWeather)
    {
        text = NaturalToolLanguage.Address(text);
        token.ThrowIfCancellationRequested();
        if (Regex.IsMatch(Regex.Replace(text, @"[\s.!?]", ""), @"^(?:오늘|요즘|밖에|날씨가|날씨|너무|좀|많이|정말|진짜)*(?:춥네|춥다|추워|추워요|춥네요)$"))
        {
            context.PurchaseConsent = null; context.PendingSiteOffer = null;
            return "춥게 느껴지시는군요. 따뜻한 겉옷을 챙겨 입으세요. 옷이 필요하면 ‘옷을 사야겠어’라고 말해 주세요. 착장 사이트로 연결해 드릴게요.";
        }
        var natural = purchases.TryHandle(text, native, context, token) ?? ConversationReferences.Parse(text, context, sites) ?? SiteSuggestions.Parse(text, context, sites) ?? NaturalToolLanguage.Parse(text, context, sites, memory);
        bool naturalMemory = natural?.Command?.StartsWith("/기억 ") == true;
        context.BrowserConversation = false;
        if (natural is not null)
        {
            if (!native) throw new ApiFailure(403, "native_only", "앱 실행·사이트 조작·기억 저장은 Windows 모아에서 해 줘.");
            if (natural.Reply is not null) return natural.Reply;
            text = natural.Command!;
        }
        context.BrowserConversation = BrowserWorkflows.IsBrowserCommand(text);
        if (text is "대화 조건 초기화" or "말투 초기화") return "좋아, 이 대화의 말투·길이·제외 조건을 초기화했어요.";
        var windowReply = await desktop.TryWindowCommand(text, native, context, token);
        if (windowReply is not null) return windowReply;
        var fileReply = await files.TryHandle(text, native, context, token);
        if (fileReply is not null) return fileReply;
        var actionReply = await desktop.TryAction(text, native, context, token);
        if (actionReply is not null) return actionReply;
        var launch = Regex.Match(text, @"^(메모장|계산기|탐색기)\s*(?:좀\s*)?(?:열어|실행해|켜)\s*줘(?:요)?[.!?]*$");
        if (launch.Success)
        {
            if (!native) throw new ApiFailure(403, "native_only", "앱 실행은 Windows 모아에서 해 줘.");
            return await desktop.Launch(launch.Groups[1].Value, token);
        }
        if (Regex.IsMatch(text, @"^(?:그\s*창에\s*)?이어서\s*해\s*줘[.!?]*$"))
            return "어떤 내용을 이어 쓸지 말해 줘. 예: ‘그 창에 내일 할 일 써줘’.";
        var pcReply = await pcInfo.TryHandle(text, native, token, context);
        if (pcReply is not null) { context.PendingSites = []; return pcReply; }
        if (BrowserWorkflows.IsBrowserCommand(text))
        {
            browser ??= new BrowserWorkflows(options, sites);
            var browserReply = await browser.TryHandle(text, native, context, token);
            if (browserReply is not null) return (natural?.Prefix ?? "") + browserReply;
        }
        var siteReply = sites.TryHandle(text, native, token, context);
        if (siteReply is not null) return siteReply;
        text = text switch
        {
            "도구 도움말" or "기능 알려줘" or "도움말" => "/도구",
            "열린 창 보여줘" => "/창",
            "메모장 열어줘" => "/앱 메모장",
            "계산기 열어줘" => "/앱 계산기",
            "탐색기 열어줘" => "/앱 탐색기",
            _ => text
        };
        token.ThrowIfCancellationRequested();
        if (text == "/도구") return native ? Help : SharedHelp;
        if (Regex.IsMatch(text, @"(?:컴퓨터|노트북|PC|피씨|화면|마우스|키보드).*(?:조작|제어)|(?:뭘|무엇을|어떤\s*기능).*(?:할\s*수|가능)", RegexOptions.IgnoreCase))
            return ControlHelp(native);
        if (text == "/계산") return "계산할 식을 적어 줘. 예를 들면 ‘/계산 (12+3)*4’예요.";
        if (text.StartsWith("/계산 ")) return Calculate(text[4..]);
        var arithmetic = Regex.Match(text, @"^(?<expr>[0-9.,()+*/xX×÷−\s-]+?)\s*(?:은|는|이|가)?\s*(?:계산해\s*줘요?|계산해\s*주세요|얼마(?:야|예요)?|=)?[?!]*$");
        if (arithmetic.Success && arithmetic.Groups["expr"].Value.Any(char.IsDigit) && arithmetic.Groups["expr"].Value.IndexOfAny(['+', '-', '*', '/', '×', '÷', 'x', 'X', '−']) >= 0)
            return Calculate(arithmetic.Groups["expr"].Value);
        var online = await lookup.TryHandle(text, token, context, wasWeather);
        if (online is not null) return online;
        if (!text.StartsWith('/') && Regex.IsMatch(text, @"(?:저장해|닫아|종료해|삭제해)\s*(?:줘|주세요)[.!?]*$|(?:ctrl|alt|shift|enter|엔터).*(?:눌러|입력해)\s*줘", RegexOptions.IgnoreCase))
            return native ? "메모장 글은 ‘메모장 내용 오늘메모.txt로 저장해줘’로 새 파일에 저장할 수 있어요. 창 닫기·파일 삭제·단축키 실행은 아직 연결하지 않았어요." : ControlHelp(false);
        if (!text.StartsWith('/') && (Regex.IsMatch(text, @"클릭해\s*줘|타이핑해\s*줘") ||
            Regex.IsMatch(text, @"(?:파일|폴더|프로그램|앱|컴퓨터|PC|마우스|키보드|창|화면|메모장|버튼).*(?:눌러|입력해|붙여넣어|삭제해|종료해)\s*줘", RegexOptions.IgnoreCase)))
            return ControlHelp(native);
        if (!text.StartsWith('/')) return null;
        if (!native) throw new ApiFailure(403, "native_only", "PC 조작과 저장된 기억은 Windows 모아 채팅창에서만 사용할 수 있어요.");
        if (text == "/창") return await desktop.Windows(token);
        if (text.StartsWith("/화면 ")) return await desktop.Snapshot(text[4..].Trim(), token, context);
        if (text.StartsWith("/앱 ")) return await desktop.Launch(text[3..].Trim(), token);
        if (text == "/기억 목록")
        {
            var items = memory.List();
            return items.Length == 0 ? "저장한 기억이 없어요." : naturalMemory
                ? "기억해 둔 내용이에요.\n" + string.Join("\n", items.Take(20).Select(MemoryLabel))
                : $"기억 {items.Length}개 중 최근 20개까지 표시해요. /기억 찾기 검색어 로 찾을 수 있어요.\n" + string.Join("\n", items.Take(20).Select(m => $"{m.Key} = {m.Value}"));
        }
        if (text.StartsWith("/기억 저장 "))
        {
            var parts = text[7..].Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new ApiFailure(400, "invalid_memory", "예: /기억 저장 좋아하는 색 = 파랑");
            var saved = memory.Save(parts[0], parts[1]);
            return naturalMemory ? "기억해 뒀어요.\n" + MemoryLabel(saved) + "\n모아를 다시 켜도 기억할게요."
                : $"기억했어요: {saved.Key} = {saved.Value}\n엔진을 다시 켜도 유지돼요.";
        }
        if (text.StartsWith("/기억 삭제 ")) return memory.Delete(text[7..].Trim()) ? "저장된 기억을 삭제했어요. 이미 나눈 대화에서 이 내용도 지우려면 새 대화를 시작해 주세요." : "그 이름의 기억이 없어요.";
        if (text.StartsWith("/기억 찾기 "))
        {
            var matches = memory.Search(text[7..]);
            return matches.Length == 0 ? "관련된 기억이 없어요." : naturalMemory
                ? "이렇게 기억하고 있어요.\n" + string.Join("\n", matches.Select(MemoryLabel))
                : string.Join("\n", matches.Select(m => $"{m.Key} = {m.Value}"));
        }
        if (text.StartsWith("/메모 "))
        {
            string content = text[4..].Trim();
            if (content.Length == 0) throw new ApiFailure(400, "invalid_note", "저장할 메모 내용을 적어 주세요.");
            var folder = NoteFolder();
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt";
            var path = Path.Combine(folder, name);
            // New files only; no user supplied paths, no overwriting existing documents.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
            if (File.ReadAllText(path, Encoding.UTF8) != content) throw new ApiFailure(500, "note_verify", "메모 저장 결과를 확인하지 못했어요.");
            return $"메모를 저장하고 내용을 확인했어요.\n{path}\n/파일 검색어 로 파일 이름을 찾을 수 있어요.";
        }
        if (text == "/파일" || text.StartsWith("/파일 "))
        {
            var query = text.Length > 3 ? text[4..].Trim() : "";
            var files = Directory.EnumerateFiles(NoteFolder(), "*.txt", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase)).Take(20).Select(Path.GetFileName).ToArray();
            return "모아 메모 폴더의 파일 이름 검색 결과예요 (최대 20개).\n" + (files.Length == 0 ? "일치하는 파일이 없어요." : string.Join("\n", files));
        }
        return "아직 지원하지 않는 명령이에요. /도구 로 사용법을 확인해 주세요.";
    }
    private string ControlHelp(bool native) => !native ? "이 사이트에서는 채팅·계산·날씨·검색을 쓸 수 있어요. PC 정보 조회와 앱 실행은 Windows 모아에서 해 줘." :
        "모아 전용 크롬에서 사이트 메뉴·링크 열기와 검색도 할 수 있어요. ‘갈피 조작해줘’로 시작해 줘.\n" +
        (pcInfo.Available ? "PC 사양 조회와 등록 사이트 열기는 할 수 있어요. " : "등록 사이트를 열 수 있고, PC 사양 조회 도구는 지금 준비되지 않았어요. ") +
        (desktop.Available ? "열린 창 선택·전환, 메모장·계산기·탐색기 실행도 돼요.\n메모장 글 추가·새 파일 저장, 파일 이름 검색·탐색기 위치 열기, 계산기 버튼 누르기를 지원해요. " : "창 읽기·앱 실행·입력 도구는 지금 준비되지 않았어요.\n") +
        "기존 파일 삭제와 다른 앱의 자유로운 클릭은 아직 연결하지 않았어.";
    private static string MemoryLabel(MemoryItem item) => Regex.IsMatch(item.Key, @"^메모 [A-F0-9]{10}$") ? item.Value : item.Key + ": " + item.Value;
    private string NoteFolder()
    {
        var path = Path.Combine(options.DataPath, "notes");
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ApiFailure(400, "note_folder_link", "메모 폴더가 연결 경로여서 저장하지 않았어요.");
        return path;
    }
    public static string Calculate(string expression)
    {
        expression = expression.Trim().Replace('×', '*').Replace('x', '*').Replace('X', '*').Replace('÷', '/').Replace('−', '-');
        // Remove only correctly grouped thousands separators; malformed commas fail validation.
        expression = Regex.Replace(expression, @"(?<![\d.,])\d{1,3}(?:,\d{3})+(?:\.\d+)?(?![\d.,])", m => m.Value.Replace(",", ""));
        if (expression.Length is < 1 or > 100 || !Regex.IsMatch(expression, @"^[0-9.()+*/\s-]+$"))
            throw new ApiFailure(400, "invalid_calculation", "숫자와 + - * / ( ) 만 사용해 주세요. 예: /계산 (12+3)*4");
        try
        {
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            var value = Convert.ToDouble(table.Compute(expression, ""), CultureInfo.InvariantCulture);
            if (!double.IsFinite(value)) throw new ArithmeticException();
            return expression + " = " + value.ToString("G15", CultureInfo.InvariantCulture) + "\n로컬 사칙연산 결과예요.";
        }
        catch (Exception e) when (e is EvaluateException or SyntaxErrorException or ArithmeticException or FormatException or InvalidCastException)
        { throw new ApiFailure(400, "invalid_calculation", "계산식을 확인해 주세요. 0으로 나누거나 너무 큰 수는 계산할 수 없어요."); }
    }
}
