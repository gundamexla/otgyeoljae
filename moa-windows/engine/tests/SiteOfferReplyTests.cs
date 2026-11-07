using VoltiEngine;

internal static class SiteOfferReplyTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "site-offer-replies");
        Directory.CreateDirectory(directory);
        var sites = new SiteRegistry(directory, _ => throw new Exception("Parsing must not launch a site"));
        NaturalToolRequest? Turn(string text, SkillContext context) =>
            ConversationReferences.Parse(text, context, sites) ?? SiteSuggestions.Parse(text, context, sites);
        SkillContext ClinicOffer()
        {
            var context = new SkillContext();
            Turn("머리가 아파", context);
            return context;
        }

        foreach (string text in new[] { "응 열어줘", "네 연결해주세요", "좋아요 열어주세요" })
        {
            var context = ClinicOffer();
            var reply = Turn(text, context);
            check(reply?.Command == "/웹 열기 병원" && context.PendingSiteOffer is null,
                "natural assent opens only the pending clinic: " + text);
        }

        foreach (string text in new[] { "아니 지금은 됐어", "아니요 지금은 괜찮아요", "지금은 됐어", "응 지금은 열지마" })
        {
            var context = ClinicOffer();
            var reply = Turn(text, context);
            check(reply?.Command is null && reply?.Reply?.Contains("열지 않을게요") == true && context.PendingSiteOffer is null &&
                Turn("응 열어줘", context)?.Command is null,
                "natural refusal cancels the offer and later assent: " + text);
        }

        (string Text, string Site)[] replacements =
        [
            ("병원 말고 착장", "의상 착장"),
            ("응 병원 말고 착장", "의상 착장"),
            ("병원 대신 금융으로 연결해줘", "금융"),
            ("그거 말고 피팅", "의상 착장"),
            ("아니 착장으로 바꿔줘", "의상 착장"),
            ("착장으로 바꿔줘", "의상 착장")
        ];
        foreach (var replacement in replacements)
        {
            var context = ClinicOffer();
            var reply = Turn(replacement.Text, context);
            check(reply?.Command == "/웹 열기 " + replacement.Site && context.PendingSiteOffer is null,
                "explicit replacement opens the named registered site: " + replacement.Text);
        }

        string[] nonActions =
        [
            "금융 말고 착장",                         // Excluded site is not the current offer.
            "병원 말고 착장과 금융",                  // More than one destination.
            "병원 말고 착장 열지마",                  // Negative replacement.
            "병원 말고 착장으로 바꾸지 마",
            "‘병원 말고 착장’이 무슨 뜻이야?",        // Quoted language question.
            "병원 말고 착장이라고 말하면?",           // Hypothetical phrase.
            "병원 말고 착장. 기억해줘",               // Another tool owns memory requests.
            "병원 말고 https://example.org/",         // No arbitrary URL supplied by dialogue.
            "병원 말고 장",                          // Partial names are not exact aliases.
            "병원 말고 새로만든미등록사이트"
        ];
        foreach (string text in nonActions)
            check(Turn(text, ClinicOffer())?.Command is null, "ambiguous or non-actionable reply does not open a site: " + text);

        check(Turn("기억해줘: 병원 말고 착장", ClinicOffer()) is null,
            "prefix memory request is delegated to the memory handler");
        check(Turn("고양이 말고 강아지가 좋아", ClinicOffer()) is null,
            "ordinary preference changes continue as normal conversation");
        sites.Save("네이버", "https://example.org/naver-fixture");
        var namedOffer = new SkillContext { PendingSiteOffer = new("site", "네이버", "https://example.org/naver-fixture") };
        check(Turn("네이버 말고 착장", namedOffer)?.Command == "/웹 열기 의상 착장",
            "site names beginning with assent syllables remain intact");

        check(Turn("착장", new SkillContext())?.Command is null, "a bare site name in a fresh conversation does not launch");
        check(Turn("응 열어줘", new SkillContext())?.Command is null, "assent without an offer has no implicit destination");
        check(Turn("병원 말고 착장", new SkillContext())?.Command is null, "replacement without an offer does not reuse another conversation");

        var changedTopic = ClinicOffer();
        Turn("오늘은 산책하고 싶어", changedTopic);
        check(Turn("응 열어줘", changedTopic)?.Command is null, "topic change expires natural assent to an earlier offer");

        var consumed = ClinicOffer();
        Turn("병원 말고 착장", consumed);
        check(Turn("응 열어줘", consumed)?.Command is null, "a replacement consumes its offer and cannot execute twice");

        foreach (string stage in new[] { "failed", "cancelled" })
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), "native");
            var context = ClinicOffer();
            session.Commit("머리가 아파", "병원 사이트를 열까요?", context);
            session.RecordOutcome(new TurnOutcome("intervening", stage, [], null, DateTimeOffset.UtcNow));
            check(Turn("병원 말고 착장", session.ContextSnapshot())?.Command is null,
                "an interrupted offer cannot be changed into an execution: " + stage);
        }

        var changedAddress = ClinicOffer();
        sites.Save("병원", "https://example.org/changed-clinic");
        check(Turn("응 열어줘", changedAddress)?.Command is null,
            "natural assent revalidates the originally suggested site address");
    }
}
