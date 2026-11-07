using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record NaturalToolRequest(string? Command = null, string? Reply = null, string? Prefix = null);

// Parse only the user's request. Neither model output nor page text becomes a command.
// All normalized requests still run through the existing native-only tool handlers.
public static class NaturalToolLanguage
{
    private const string Please = @"\s*(?:줘(?:요)?|주세요|줄래(?:요)?|주실래요|줄\s*수\s*(?:있어(?:요)?|있니)|주겠니)?";
    private const string Open = @"(?:열어|켜|띄워|실행해|실행시켜|시작해)";
    private const string Remember = @"(?:기억해\s*둬|기억해|기억으로\s*저장해)";
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    public static string Address(string text) => Regex.Replace(text.Trim(), @"^모아(?:야)?\s*[,，!]\s*|^모아(?:야)?\s+", "");
    private static string Sentence(string text) => text.Trim().TrimEnd('.', '!', '?', '。', '？', '！').TrimEnd();
    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && ((text[0], text[^1]) is ('"', '"') or ('\'', '\'') or ('“', '”') or ('‘', '’')))
            return text[1..^1].Trim();
        return text;
    }
    // Do not turn the actual search word "가을" into "가" by blindly removing 을.
    private static string Object(string text)
    {
        text = text.Trim();
        if (text.Length > 1 && text[^1] is '을' or '를')
        {
            char prior = text[^2];
            bool quoted = prior is '"' or '\'' or '”' or '’';
            bool hangul = prior is >= '가' and <= '힣';
            bool consonant = hangul && (prior - '가') % 28 != 0;
            if (quoted || hangul && (text[^1] == '을' ? consonant : !consonant)) text = text[..^1].TrimEnd();
        }
        return Unquote(text);
    }
    private static string Target(string raw, SkillContext context)
    {
        string text = raw.Trim();
        var labels = context.Browser?.Elements.Select(e => e.Label).ToArray() ?? [];
        if (labels.Contains(text)) return text;
        text = Object(text);
        if (labels.Contains(text)) return text;
        return Object(Regex.Replace(text, @"\s+(?:버튼|링크|메뉴)$", ""));
    }
    private static string Key(string text) => Regex.Replace(text.Trim(), @"^(?:내가|저의|내|제)\s+", "").Trim();
    private static NaturalToolRequest Command(string command) => new(Command: command);
    private static NaturalToolRequest Reply(string text) => new(Reply: text);

    public static NaturalToolRequest? Parse(string input, SkillContext context, SiteRegistry sites, MemoryStore memory)
    {
        string text = Sentence(Address(input));
        if (text.StartsWith('/')) return null;

        var externalSite = Match(text, @"^(?:기본|로그인된)\s*크롬(?:으로|에서)\s*(?<site>.+?)\s*(?:열어|연결해)" + Please + "$");
        if (externalSite.Success)
        {
            var choices = sites.Find(Object(externalSite.Groups["site"].Value));
            return choices.Length == 1 ? Command("/사이트 외부 " + choices[0].Name) : Reply("등록된 사이트 이름을 정확히 말해 줘.");
        }

        var siteInfo = Match(text, @"^(?<site>.+?)\s*(?:사이트\s*)?(?:주소|연결\s*주소)(?:를|을)?\s*(?:알려|보여)" + Please + "$");
        if (siteInfo.Success)
        {
            var choices = sites.Find(siteInfo.Groups["site"].Value.Trim());
            if (choices.Length == 0) choices = sites.Find(Regex.Replace(siteInfo.Groups["site"].Value.Trim(), @"\s*사이트$", ""));
            return choices.Length == 1 ? Reply(choices[0].Name + " 사이트예요.\n" + choices[0].Url + "\n‘그거 열어줘’라고 하면 열 수 있어요.")
                : Reply("등록된 사이트 이름을 정확히 말해 줘. ‘사이트 목록’으로 확인할 수 있어요.");
        }

        // Memory is parsed first: a quoted app instruction inside a memory is data.
        var remember = Match(text, @"^(?<body>.*?)\s*(?:꼭\s*|좀\s*)?" + Remember + Please + "$");
        var prefixMemory = Match(text, @"^" + Remember + Please + @"\s*[:：]\s*(?<body>.+)$");
        if (remember.Success || prefixMemory.Success)
        {
            string body = Sentence((prefixMemory.Success ? prefixMemory : remember).Groups["body"].Value);
            body = Regex.Replace(body, @"^(?:앞으로|다음에도)\s+", "");
            body = Regex.Replace(body, @"(?:[,，]\s*)?(?:이거|이걸|이것을|이것|그걸|그것을)\s*$", "").Trim();
            body = Sentence(body);
            if (body.Length == 0 || body is "그거" or "그것" or "이 내용" or "그 내용")
                return Reply("기억할 내용을 함께 말해 줘. 예를 들어 ‘내가 좋아하는 색은 파랑이야. 기억해줘’라고 하면 돼요.");
            body = Unquote(Regex.Replace(body, @"(?:이?라고)$", "").Trim());
            string key, value;
            var pair = Match(body, @"^(?<key>[^=]{1,60}?)\s*=\s*(?<value>.+)$");
            if (!pair.Success) pair = Match(body, @"^(?<key>(?:(?:내가|제가)\s+)?(?:(?:좋아하는|싫어하는|사용하는|자주\s*쓰는|쓰는|사는)\s+)?[^=]+?)(?:은|는)\s+(?<value>.+)$");
            if (pair.Success && pair.Groups["key"].Value.Trim() is not ("나" or "저"))
            {
                key = Key(pair.Groups["key"].Value);
                value = Unquote(Regex.Replace(pair.Groups["value"].Value.Trim(), @"(?:이라고|라고|이에요|예요|입니다|이야)$", ""));
            }
            else
            {
                // Keep an unstructured fact verbatim and give it a stable key; no guessed fact extraction.
                key = "메모 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..10];
                value = body;
            }
            if (key.Length is < 1 or > 40 || value.Length is < 1 or > 400)
                return Reply("기억할 내용을 조금 짧게 말해 줘. 이름은 40자, 내용은 400자까지 저장할 수 있어요.");
            return Command("/기억 저장 " + key + " = " + value);
        }
        if (Match(text, @"(?:기억|저장)(?:하지\s*마(?:세요)?|하지\s*말아|하면\s*안\s*돼)" + Please + "$").Success)
            return Reply("이 내용은 별도 기억에 저장하지 않았어요.");
        if (Match(text, @"^(?:(?:내가\s*)?(?:저장한|저장된|기억해\s*둔)\s*)?기억(?:들|\s*목록|\s*내용)?(?:을|를)?\s*(?:좀\s*)?(?:보여|알려)" + Please + "$").Success)
            return Command("/기억 목록");
        var recall = Match(text, @"^(?:저장한\s*)?(?<key>.+?)\s*(?:기억\s*(?:찾아|알려)|기억나(?:요|니)?|기억하고\s*있어(?:요)?|뭐였(?:지|어|죠)|뭐야)" + Please + "$");
        if (recall.Success)
        {
            string key = Key(recall.Groups["key"].Value);
            if (text.Contains("기억") || text.StartsWith("저장한 ") || memory.List().Any(m => key.Contains(m.Key, StringComparison.OrdinalIgnoreCase)))
                return Command("/기억 찾기 " + key);
        }

        var app = Match(text, @"^(?<app>계산기|메모장|(?:파일\s*|윈도우\s*)?탐색기)(?:\s*(?:앱|프로그램))?(?:을|를)?\s*(?:좀\s*)?" + Open + Please + "$");
        if (app.Success)
        {
            string name = app.Groups["app"].Value;
            return Command("/앱 " + (name.EndsWith("탐색기") ? "탐색기" : name));
        }

        var register = Match(text, @"^(?<url>https?://\S+)\s*(?:을|를)?\s+(?<name>.+?)(?:이라는|라는|란)?\s*(?:이름으로|사이트로)\s*(?:등록해|저장해)" + Please + "$");
        if (register.Success)
            return Command("사이트 등록 " + Unquote(register.Groups["name"].Value) + " " + register.Groups["url"].Value);

        bool active = context.BrowserConversation && context.Browser is { Page.IsClosed: false };
        var open = Match(text, @"^(?<site>.+?)\s*(?:좀\s*)?(?:연결(?:해)?|접속(?:해)?|열기|연결하기|조작(?:창)?(?:으로)?\s*(?:열어|해)|" + Open + ")" + Please + "$");
        if (open.Success && !text.Contains("에서 "))
        {
            string name = Object(open.Groups["site"].Value);
            var matches = sites.Find(name);
            if (matches.Length == 0) { name = Object(Regex.Replace(name, @"\s*사이트$", "")); matches = sites.Find(name); }
            if (matches.Length == 1) return Command("/웹 열기 " + matches[0].Name);
            if (matches.Length > 1) return Reply("어느 사이트를 열까요? 이름을 정확히 말해 줘: " + string.Join(", ", matches.Select(s => s.Name)));
            // A bare label belongs to the current page, not an arbitrary application launcher.
            if (!active)
                return Reply("앱은 계산기·메모장·탐색기를 열 수 있어요. 사이트라면 ‘주소를 이름으로 등록해줘’라고 먼저 알려 줘.");
        }

        string browserText = text;
        var scope = Match(text, @"^(?:이\s*사이트|그\s*사이트|지금\s*사이트|사이트|웹|브라우저|이\s*페이지|지금\s*페이지)(?:에서|는|를|을)?\s+(?<rest>.+)$");
        var here = Match(text, @"^(?:여기서|거기서|여기에서|거기에서)\s+(?<rest>.+)$");
        bool explicitBrowser = scope.Success || here.Success;
        if (explicitBrowser) browserText = (scope.Success ? scope : here).Groups["rest"].Value;
        bool navigation = active || explicitBrowser;
        if (navigation && Match(browserText, @"^(?:지금\s*)?(?:(?:화면|페이지|내용)(?:을|를)?)?\s*(?:좀\s*)?(?:보여|읽어)" + Please + "$").Success) return Command("/웹 화면");
        if (Match(browserText, @"^(?:이전\s*(?:페이지|화면)(?:로)?|뒤로)\s*(?:좀\s*)?(?:가|돌아가|이동해)" + Please + "$").Success)
            return navigation ? Command("/웹 뒤로") : Reply("어느 사이트에서 뒤로 갈까요? 먼저 ‘갈피 열어줘’처럼 사이트를 열어 줘.");
        if (navigation && Match(browserText, @"^(?:새로\s*고침(?:해)?|다시\s*불러와)" + Please + "$").Success) return Command("/웹 새로고침");
        if (navigation && Match(browserText, @"^(?:(?:이|그)\s*탭(?:을)?\s*)?닫아" + Please + "$").Success) return Command("/웹 닫기");

        var action = Match(browserText, @"^(?<target>.+?)\s*(?:좀\s*)?(?<action>검색해|찾아|눌러|클릭해|열어)" + Please + "$");
        if (action.Success)
        {
            string target = action.Groups["target"].Value;
            var named = Match(target, @"^(?<site>.+?)에서\s+(?<target>.+)$");
            string? site = null;
            if (named.Success)
            {
                var choices = sites.Find(Object(named.Groups["site"].Value));
                if (choices.Length > 1) return Reply("사이트 이름을 정확히 골라 줘: " + string.Join(", ", choices.Select(s => s.Name)));
                if (choices.Length == 1) { site = choices[0].Name; target = named.Groups["target"].Value; navigation = true; }
                else return null; // Not necessarily a website: existing file/weather handlers may own it.
            }
            bool search = action.Groups["action"].Value is "검색해" or "찾아";
            if (!explicitBrowser && site is null && Regex.IsMatch(target, @"(?:파일|폴더|메모장|계산기)(?:\s|에서|$)")) return null;
            if (navigation)
            {
                target = search ? Object(target) : Target(target, context);
                return site is not null ? Command(site + "에서 " + target + (search ? " 검색해줘" : " 눌러줘"))
                    : Command(search ? "/웹 검색 " + target : "사이트에서 " + target + " 눌러줘");
            }
            if (!search && !Match(target, @"^(?:계산기\s*)?(?:e\d+|\d|더하기|빼기|곱하기|나누기|같음|등호)$").Success)
                return Reply("어느 사이트의 항목을 누를까요? 먼저 ‘갈피 열어줘’처럼 사이트를 열고 메뉴 이름을 말해 줘.");
        }
        return null;
    }
}
