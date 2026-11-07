using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoltiEngine;

// Only completed turns from this session supply references. Resolve names to the
// existing app allowlist/site registry; dialogue never supplies executable code.
public static class ConversationReferences
{
    private const string End = @"(?:줘요?|주세요|줄래요?|줄수있어(?:요)?)?";
    private static string Compact(string text) => Regex.Replace(NaturalToolLanguage.Address(text), @"\s+", "").TrimEnd('.', '!', '?', '。', '？');
    private static bool OpenReference(string text) => Regex.IsMatch(Compact(text), @"^(?:그럼|그러면|응[,，]?|좋아[,，]?)?(?:그거|그걸|그것을?|이거|이걸|그(?:앱|사이트)(?:을|를)?|방금(?:말한)?(?:앱|사이트)(?:을|를)?)좀?(?:열어|켜|띄워|실행해)" + End + "$");
    private static bool MemoryReference(string text) => Regex.IsMatch(Compact(text), @"^(?:(?:(?:방금|아까|직전|조금전|내가|제가|네가|너가|모아가))*(?:말한내용|말한말|말한것|얘기한내용|이야기한내용|한말)|그거|그걸|그것|그내용|이내용|방금내용)(?:을)?(?:좀|꼭)?(?:기억해둬|기억해|기억으로저장해)" + End + "$");
    private static bool Acknowledgement(string text) => Compact(text) is "응" or "네" or "그래" or "좋아" or "알겠어" or "알았어" or "고마워" or "고마워요" or "감사해";
    private static NaturalToolRequest Reply(string text) => new(Reply: text);
    private static NaturalToolRequest Open(ReferenceTarget target, SiteRegistry sites)
    {
        if (target.Kind == "app" && target.Name is "계산기" or "메모장" or "탐색기") return new(Command: "/앱 " + target.Name);
        var current = sites.Find(target.Name);
        if (target.Kind == "site" && current.Length == 1 && current[0].Url == target.Url) return new(Command: "/웹 열기 " + current[0].Name);
        return Reply("사이트 등록이 바뀌었어요. 열고 싶은 사이트 이름을 다시 말해 줘.");
    }
    private static bool Mentions(string source, string name)
    {
        string words = string.Join(@"\s*", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        string suffix = @"(?:을|를|은|는|이|가|에서|에|랑|와|과|하고|으로|로|도|야|이야)?";
        if (Regex.IsMatch(source, words + suffix + @"\s*(?:말고|아니고|제외|열지|켜지|실행하지)")) return false;
        return Regex.IsMatch(source, @"(?<![가-힣A-Za-z0-9])" + words + suffix + @"(?=$|[^가-힣A-Za-z0-9])", RegexOptions.IgnoreCase);
    }
    private static ReferenceTarget[] Candidates(string text, SiteRegistry sites)
    {
        var targets = new List<ReferenceTarget>();
        foreach (string app in new[] { "계산기", "메모장", "탐색기" }) if (Mentions(text, app)) targets.Add(new("app", app));
        foreach (var site in sites.List())
            if (SiteRegistry.Aliases(site).Any(name => Mentions(text, name))) targets.Add(new("site", site.Name, site.Url));
        return targets.Distinct().ToArray();
    }
    private static DialogueTurn? Antecedent(SkillContext context) => context.RecentDialogue.Reverse()
        .FirstOrDefault(t => !Acknowledgement(t.User) && !OpenReference(t.User) && !MemoryReference(t.User));

    public static NaturalToolRequest? Parse(string input, SkillContext context, SiteRegistry sites)
    {
        string compact = Compact(input);
        if (input.TrimStart().StartsWith('/')) { context.PendingReferenceTargets = []; context.PendingSiteOffer = null; context.ReferenceInterrupted = false; return null; }
        var pending = context.PendingReferenceTargets;
        var offer = context.PendingSiteOffer;
        context.PendingReferenceTargets = [];
        context.PendingSiteOffer = null;
        var offerReply = offer is null ? null : SiteOfferReplies.Parse(input, offer, sites);
        if (context.ReferenceInterrupted && (OpenReference(input) || MemoryReference(input) || pending.Length > 0 || offerReply?.Command is not null))
            return Reply("앞선 요청이 완료되지 않아 대상을 확실히 이어받을 수 없어요. 앱·사이트 이름이나 기억할 내용을 다시 말해 줘.");
        context.ReferenceInterrupted = false;
        if (offerReply is not null) return offerReply;
        if (offer is not null && OpenReference(input)) return Open(offer, sites);
        if (offer is null && SiteOfferReplies.ExplicitAssent(input))
            return Reply("이어받을 사이트 제안이 없어요. ‘병원 열어줘’처럼 열고 싶은 이름을 말해 줘.");
        if (pending.Length > 0)
        {
            if (compact is "취소" or "취소해줘" or "아니" or "됐어" or "그만") return Reply("알겠어요. 열지 않을게요.");
            var choices = pending.Where(t => compact == Compact(t.Name) || compact == Compact(t.Name) + "로" || compact == Compact(t.Name) + "으로" ||
                t.Kind == "site" && sites.Find(input.Trim()).Any(s => s.Name == t.Name)).ToArray();
            if (choices.Length == 1) return Open(choices[0], sites);
        }
        if (MemoryReference(input))
        {
            bool assistant = compact.Contains("네가") || compact.Contains("너가") || compact.Contains("모아가");
            var turn = assistant ? context.RecentDialogue.LastOrDefault() : Antecedent(context);
            string? body = assistant ? turn?.Assistant : turn?.User;
            if (string.IsNullOrWhiteSpace(body)) return Reply("기억할 앞선 내용을 찾지 못했어요. 저장할 문장을 먼저 말해 줘.");
            if (body.Length > 400) return Reply("앞선 내용이 400자보다 길어요. 기억할 부분을 짧게 골라 말해 줘.");
            // Preserve the referenced text exactly, including quotes, numbers and punctuation.
            string key = "메모 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..10];
            return new(Command: "/기억 저장 " + key + " = " + body);
        }
        if (!OpenReference(input)) return null;
        var prior = Antecedent(context);
        var candidates = prior is null ? [] : Candidates(prior.User, sites);
        // Tool output may contain page-supplied text. Only a known model reply may
        // provide an assistant-only name, and still only from the allowlisted targets.
        if (candidates.Length == 0 && context.PreviousReplyWasModel && prior == context.RecentDialogue.LastOrDefault() &&
            Regex.IsMatch(prior?.User ?? "", @"앱|프로그램|사이트|도구") &&
            !Regex.IsMatch(prior?.User ?? "", @"열지|켜지|실행하지|말고|아니고"))
            candidates = Candidates(prior?.Assistant ?? "", sites);
        if (compact.Contains("사이트")) candidates = candidates.Where(t => t.Kind == "site").ToArray();
        else if (compact.Contains("앱")) candidates = candidates.Where(t => t.Kind == "app").ToArray();
        if (candidates.Length == 1) return Open(candidates[0], sites);
        if (candidates.Length == 0) return Reply("어떤 앱이나 사이트를 뜻하는지 찾지 못했어요. 이름을 한 번만 말해 줘.");
        context.PendingReferenceTargets = candidates;
        return Reply("앞서 여러 대상을 말했어요. 어느 것을 열까요? " + string.Join(" / ", candidates.Select(t => t.Name)));
    }
}
