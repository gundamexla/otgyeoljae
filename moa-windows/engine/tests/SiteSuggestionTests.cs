using VoltiEngine;

internal static class SiteSuggestionTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "site-suggestion-phrases");
        Directory.CreateDirectory(directory);
        int launches = 0;
        var sites = new SiteRegistry(directory, _ => launches++);

        // Complete first-person statements should offer a service without executing it.
        (string Text, string Site)[] positives =
        [
            ("머리가 아파", "병원"),
            ("머리가 좀 아프네", "병원"),
            ("모아야, 오늘 머리가 아파요", "병원"),
            ("어제부터 배가 아파", "병원"),
            ("무릎이 아파", "병원"),
            ("오늘 입을 옷 좀 골라줘", "의상 착장"),
            ("내일 뭐 입으면 좋을까?", "의상 착장"),
            ("옷 추천해줄래?", "의상 착장"),
            ("오늘 코디 좀 도와줘", "의상 착장"),
            ("이번 달 돈을 너무 썼어", "금융"),
            ("요즘 지출이 너무 많아", "금융"),
            ("돈을 좀 아끼고 싶어", "금융"),
            ("이번 달 예산 정리 좀 도와줘", "금융"),
            ("이번 달 돈을 너무 많이 썼어", "금융"),
            ("내 머리가 아파", "병원")
        ];
        foreach (var item in positives)
        {
            var context = new SkillContext();
            var reply = SiteSuggestions.Parse(item.Text, context, sites);
            check(reply?.Command is null && reply?.Reply?.Contains("열어 드릴까요") == true &&
                context.PendingSiteOffer?.Name == item.Site && context.PendingSiteOffer.Url == sites.Find(item.Site).Single().Url &&
                context.Browser is null && launches == 0,
                "paraphrased need offers only its registered site: " + item.Text);
        }

        string[] negatives =
        [
            "나 안 아파", "머리가 아프지 않아", "어제 머리가 아팠어",
            "엄마가 머리가 아파", "친구가 머리 아프대",
            "‘머리가 아파’가 무슨 뜻이야?", "머리가 아프면 병원에 가야 해?",
            "머리가 아파. 기억해줘", "이번 달 돈을 너무 썼어. 기억해줘",
            "오늘 입을 옷 골라달라고 안 했어", "옷 추천은 필요 없어",
            "이번 달 돈을 많이 안 썼어", "작년에 돈을 너무 썼었어",
            "친구가 이번 달 돈을 너무 썼어", "‘오늘 입을 옷 좀 골라줘’를 영어로 번역해줘",
            "머리가 아프고 오늘 입을 옷도 골라줘"
        ];
        foreach (string input in negatives)
        {
            var context = new SkillContext();
            check(SiteSuggestions.Parse(input, context, sites) is null && context.PendingSiteOffer is null,
                "non-current or non-actionable wording does not offer a site: " + input);
        }

        NaturalToolRequest? Turn(string input, SkillContext context) =>
            ConversationReferences.Parse(input, context, sites) ?? SiteSuggestions.Parse(input, context, sites);

        foreach (var item in new[] { positives[0], positives[5], positives[9] })
        {
            var context = new SkillContext();
            Turn(item.Text, context);
            var accepted = Turn("응, 연결해줘", context);
            check(accepted?.Command == "/웹 열기 " + item.Site && context.PendingSiteOffer is null,
                "confirmation resolves the current paraphrased offer: " + item.Site);
            check(Turn("응", context) is null, "consumed offer cannot execute a second time: " + item.Site);
        }

        var pending = new SkillContext();
        Turn("머리가 아파", pending);
        var declined = Turn("아니요", pending);
        check(declined?.Reply?.Contains("열지 않을게요") == true && declined.Command is null &&
            pending.PendingSiteOffer is null && Turn("응", pending) is null,
            "declining a paraphrased need cancels the offer and later assent");

        Turn("오늘 입을 옷 좀 골라줘", pending);
        Turn("오늘은 산책하고 싶어", pending);
        check(Turn("응", pending) is null && pending.PendingSiteOffer is null,
            "an unrelated new topic expires a paraphrased site offer");

        Turn("이번 달 돈을 너무 썼어", pending);
        Turn("머리가 아파", pending);
        check(Turn("그거 열어줘", pending)?.Command == "/웹 열기 병원",
            "a new need replaces the previous offer before pronoun confirmation");

        Turn("머리가 아파", pending);
        check(Turn("응, 병원 말고 착장", pending)?.Command == "/웹 열기 의상 착장" && pending.PendingSiteOffer is null,
            "explicit changed target replaces the earlier offer");

        Turn("머리가 아파", pending);
        pending.ReferenceInterrupted = true;
        check(Turn("응", pending)?.Command is null && pending.PendingSiteOffer is null,
            "interrupted dialogue cannot confirm a paraphrased old offer");

        var changedSite = new SkillContext();
        Turn("머리가 아파", changedSite);
        sites.Save("병원", "https://example.org/replaced-clinic");
        check(Turn("응", changedSite)?.Command is null && launches == 0,
            "confirmation revalidates the suggested URL and never launches during parsing");
    }
}
