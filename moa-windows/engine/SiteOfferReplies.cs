using System.Text.RegularExpressions;

namespace VoltiEngine;

// Only an outstanding site proposal gives these short replies an action target.
public static class SiteOfferReplies
{
    private const string End = @"(?:줘(?:요)?|주세요|줄래(?:요)?|주실래요|줄수있어(?:요)?)?";
    private const string Yes = @"(?:응|네|예|그래|좋아(?:요)?|좋습니다)";
    private static string Compact(string text) => Regex.Replace(NaturalToolLanguage.Address(text), @"\s+", "").TrimEnd('.', '!', '?', '。', '？', '！');
    private static Match Match(string text, string pattern) => Regex.Match(text, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static NaturalToolRequest Reply(string text) => new(Reply: text);
    public static bool ExplicitAssent(string input) => Match(Compact(input), @"(?:" + Yes + @"[,，]?)?(?:그럼)?(?:좀)?(?:연결해|열어)" + End).Success;
    private static SavedSite[] ExactSites(string text, SiteRegistry sites)
    {
        SavedSite[] Exact(string name) => sites.Find(name).Where(s => SiteRegistry.Aliases(s).Any(a => Compact(a).Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var exact = Exact(text);
        return exact.Length > 0 ? exact : Exact(Regex.Replace(text, @"(?:으로|로|을|를|은|는)$", ""));
    }
    public static NaturalToolRequest? Parse(string input, ReferenceTarget offer, SiteRegistry sites)
    {
        string text = Compact(input);
        // Explicit memory requests and quoted sentences belong to their own handlers.
        if (text.IndexOfAny(['\'', '"', '‘', '’', '“', '”']) >= 0 ||
            Match(text, @".*(?:기억해둬|기억해|기억으로저장해)" + End).Success ||
            Match(text, @"(?:기억해둬|기억해|기억으로저장해)" + End + @"[:：].+").Success) return null;
        bool decline = text is "아니" or "아니요" || Match(text,
            @"(?:(?:아니요?|" + Yes + @")[,，]?)?(?:(?:지금|오늘)은?|이번엔)?(?:됐어(?:요)?|괜찮아(?:요)?|필요없어(?:요)?|그만|취소(?:해" + End + @")?|열지마(?:요|세요)?|연결하지마(?:요|세요)?|안할래(?:요)?|나중에(?:할게(?:요)?|하자)?)").Success;
        if (decline) return Reply("알겠어요. 사이트를 열지 않을게요.");

        bool accept = Match(text, Yes + @"|(?:" + Yes + @"[,，]?)?(?:그럼)?(?:좀)?(?:연결해|열어)" + End).Success;
        string change = Regex.Replace(text, @"^(?:아니요?|" + Yes + @")[,，]?", "");
        var original = Match(text, @"(?<old>.+?)(?:말고|대신)(?<target>.+)");
        if (original.Success && ExactSites(original.Groups["old"].Value, sites).Length > 0) change = text;
        var replacement = Match(change, @"(?<old>.+?)(?:말고|대신)(?<target>.+)");
        var explicitChange = Match(change, @"(?<target>.+?)(?:으로|로)(?:좀)?(?:바꿔|변경해)" + End);
        if (!accept && !replacement.Success && !explicitChange.Success) return null;

        var current = sites.Find(offer.Name);
        if (offer.Kind != "site" || current.Length != 1 || current[0].Url != offer.Url)
            return Reply("사이트 등록이 바뀌었어요. 열고 싶은 사이트 이름을 다시 말해 줘.");
        if (accept) return new(Command: "/웹 열기 " + current[0].Name);
        if (replacement.Success)
        {
            string prior = replacement.Groups["old"].Value;
            var named = ExactSites(prior, sites);
            if (prior is not ("그거" or "그것" or "그사이트" or "이거" or "거기") && named.Length == 0)
                return null; // Ordinary topic changes such as "cats rather than dogs" are not site instructions.
            if (prior is not ("그거" or "그것" or "그사이트" or "이거" or "거기") &&
                (named.Length != 1 || named[0].Name != offer.Name))
                return Reply("지금 제안한 곳은 ‘" + offer.Name + "’이에요. 변경할 사이트 이름을 정확히 말해 줘.");
        }
        string target = (replacement.Success ? replacement : explicitChange).Groups["target"].Value;
        target = Regex.Replace(target, @"(?:좀)?(?:연결해|열어|바꿔|변경해)" + End + "$", "");
        var choices = ExactSites(target, sites);
        if (choices.Length != 1)
            return Reply("변경할 사이트를 하나로 확인하지 못했어요. 등록된 사이트 이름을 정확히 말해 줘.");
        return new(Command: "/웹 열기 " + choices[0].Name);
    }
}
