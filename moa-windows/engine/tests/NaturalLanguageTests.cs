using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;
using VoltiEngine;

internal static class NaturalLanguageTests
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var options = new EngineOptions { RootDirectory = Path.Combine(root, "natural-language"), DataDirectory = "data" };
        var memory = new MemoryStore(options.DataPath);
        var sites = new SiteRegistry(options.DataPath, _ => throw new Exception("Parser must not launch a site"));
        var page = DispatchProxy.Create<IPage, PageProbe>();
        var browsing = new SkillContext { BrowserConversation = true, Browser = new(page, "https://galpi-ai-studio.vercel.app/", DateTimeOffset.UtcNow, []) };
        var cases = new (string Text, string Expected, bool Browsing)[]
        {
            ("모아야, 계산기를 좀 켜줄래?", "/앱 계산기", false),
            ("메모장 앱을 열어 주세요.", "/앱 메모장", false),
            ("파일 탐색기 실행해줄 수 있어?", "/앱 탐색기", false),
            ("계산기 띄워줘요!", "/앱 계산기", false),
            ("내가 좋아하는 색은 파랑이야. 꼭 기억해줘", "/기억 저장 좋아하는 색 = 파랑", false),
            ("제 별명은 솔방울이에요. 기억해 주세요", "/기억 저장 별명 = 솔방울", false),
            ("좋아하는 음식은 사과예요. 기억해줘", "/기억 저장 좋아하는 음식 = 사과", false),
            ("내 취미는 책을 읽는 거야. 기억해줘", "/기억 저장 취미 = 책을 읽는 거야", false),
            ("기억해줘: 작업 이름 = 모아", "/기억 저장 작업 이름 = 모아", false),
            ("앞으로 검수번호는 7429라고 기억해 둬", "/기억 저장 검수번호 = 7429", false),
            ("저장한 기억을 보여줄래?", "/기억 목록", false),
            ("비 오는 날 기억나?", "/기억 찾기 비 오는 날", false),
            ("모아야 갈피를 열어줄래?", "/웹 열기 갈피", false),
            ("갈피 사이트 좀 열어주세요", "/웹 열기 갈피", false),
            ("갈피에서 로봇을 검색해줄래?", "갈피에서 로봇 검색해줘", false),
            ("https://example.org/ 를 예시라는 이름으로 등록해줘", "사이트 등록 예시 https://example.org/", false),
            ("여기서 '해달'을 찾아줘", "/웹 검색 해달", true),
            ("거기서 로봇을 좀 검색해줄래?", "/웹 검색 로봇", true),
            ("가을 검색해줘", "/웹 검색 가을", true),
            ("자료 탐색 메뉴를 눌러줄래?", "사이트에서 자료 탐색 눌러줘", true),
            ("자세히 보기 링크를 열어줘", "사이트에서 자세히 보기 눌러줘", true),
            ("지금 페이지 보여줄래?", "/웹 화면", true),
            ("화면 좀 읽어줘", "/웹 화면", true),
            ("이전 페이지로 돌아가 줘", "/웹 뒤로", true),
            ("새로고침해 주세요", "/웹 새로고침", true),
            ("이 탭 닫아줘", "/웹 닫기", true),
            ("브라우저 닫아주세요", "/웹 닫기", true)
        };
        foreach (var item in cases)
        {
            var result = NaturalToolLanguage.Parse(item.Text, item.Browsing ? browsing : new(), sites, memory);
            check(result?.Command == item.Expected, "natural intent: " + item.Text + (result?.Command == item.Expected ? "" : " -> " + (result?.Command ?? result?.Reply ?? "null")));
        }
        foreach (string text in new[] { "계산기를 켜지 마", "계산기 열어줘라는 문장을 설명해줘", "\"계산기 켜줘\"", "나는 파랑을 좋아해", "/앱 계산기" })
            check(NaturalToolLanguage.Parse(text, new(), sites, memory)?.Command is null, "no accidental action: " + text);
        check(NaturalToolLanguage.Parse("기억해줘", new(), sites, memory)?.Reply?.Contains("기억할 내용") == true, "missing memory content asks for the fact without saving");
        check(NaturalToolLanguage.Parse("계산기 켜줘 기억해줘", new(), sites, memory)?.Command?.StartsWith("/기억 저장 ") == true, "remembered instruction remains data, not an app command");
        check(NaturalToolLanguage.Parse("로봇 검색해줘", new(), sites, memory) is null, "unscoped search outside browser conversation keeps existing lookup routing");
        check(NaturalToolLanguage.Parse("회의 파일 찾아줘", browsing, sites, memory) is null, "explicit file search is not redirected into a browser");
        check(NaturalToolLanguage.Parse("알 수 없는 앱을 열어줘", new(), sites, memory)?.Command is null, "unknown app never becomes an executable path");
        check(NaturalToolLanguage.Parse("알 수 없는 앱을 열어줘", new() { BrowserConversation = true }, sites, memory)?.Reply?.Contains("계산기·메모장·탐색기") == true, "closed browser does not capture the next app request");
        sites.Save("연습 A", "https://example.org/a"); sites.Save("연습 B", "https://example.org/b");
        var ambiguous = NaturalToolLanguage.Parse("연습 열어줘", new(), sites, memory);
        check(ambiguous?.Command is null && ambiguous?.Reply?.Contains("어느 사이트") == true, "ambiguous site asks for a name before opening");

        var bridge = new DesktopProbe();
        var skills = new LocalSkills(options, memory, new DesktopTools(bridge));
        try
        {
            var answer = await skills.TryHandle("모아야, 계산기를 좀 켜줄래?", true, default);
            check(answer?.Contains("실행 요청") == true && bridge.Calls.Count(c => c[0] == "launch" && c[1] == "calc.exe") == 1, "natural app request reaches existing launch handler exactly once");
            await skills.TryHandle("내가 좋아하는 색은 파랑이야. 기억해줘", true, default);
            check(memory.List().Single(m => m.Key == "좋아하는 색").Value == "파랑", "natural memory request writes the real SQLite store");
            await skills.TryHandle("내가 좋아하는 색은 주황이야. 기억하지 마", true, default);
            check(memory.List().Single(m => m.Key == "좋아하는 색").Value == "파랑", "negated memory request does not overwrite a stored fact");
            answer = await skills.TryHandle("내가 좋아하는 색이 뭐였지?", true, default);
            check(answer?.Contains("파랑") == true, "natural recall reads the stored fact without model inference");
            await skills.TryHandle("나는 비 오는 날 산책하는 걸 좋아해. 기억해 둬", true, default);
            check(memory.List().Any(m => m.Value == "나는 비 오는 날 산책하는 걸 좋아해"), "unstructured explicit memory preserves the original statement");
            var restored = new MemoryStore(options.DataPath);
            check(restored.Search("좋아하는 색").Any(m => m.Value == "파랑"), "natural memory persists across a fresh store instance");
            int saved = memory.List().Length, launches = bridge.Calls.Count(c => c[0] == "launch");
            foreach (string text in new[] { "계산기 켜줄래", "내 별명은 해달이야. 기억해줘", "갈피 열어줄래" })
            {
                bool denied = false;
                try { await skills.TryHandle(text, false, default); } catch (ApiFailure e) { denied = e.Code == "native_only"; }
                check(denied, "natural request retains native-only boundary: " + text);
            }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await skills.TryHandle("내 별명은 취소야. 기억해줘", true, cancellation.Token); } catch (OperationCanceledException) { }
            check(memory.List().Length == saved && bridge.Calls.Count(c => c[0] == "launch") == launches, "denied and pre-cancelled requests leave storage and launches unchanged");
            await skills.TryHandle("/기억 저장 기존명령 = 그대로", true, default);
            check(memory.List().Any(m => m.Key == "기존명령" && m.Value == "그대로"), "existing slash memory command remains compatible");
            var otherTopic = browsing with { };
            await skills.TryHandle("내가 좋아하는 색이 뭐였지?", true, default, otherTopic);
            check(!otherTopic.BrowserConversation, "switching to memory clears implicit browser follow-up focus");
        }
        finally { await skills.DisposeAsync(); }
    }

    private sealed class DesktopProbe : IDesktopBridge
    {
        public bool Available => true;
        public List<string[]> Calls { get; } = [];
        public Task<JsonElement> RunAsync(string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(arguments);
            return Task.FromResult(arguments[0] == "list-windows" ? JsonSerializer.SerializeToElement(Array.Empty<object>()) : JsonSerializer.SerializeToElement(new { }));
        }
    }
}

public class PageProbe : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_IsClosed" ? false : throw new NotSupportedException(method?.Name);
}
