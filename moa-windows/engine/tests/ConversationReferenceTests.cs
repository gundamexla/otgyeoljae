using System.Text.Json;
using VoltiEngine;

internal static class ConversationReferenceTests
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var options = new EngineOptions { RootDirectory = Path.Combine(root, "context-reference"), DataDirectory = "data" };
        var memory = new MemoryStore(options.DataPath);
        var sites = new SiteRegistry(options.DataPath, _ => throw new Exception("Unexpected site launch"));
        var archive = new ConversationArchive(options.DataPath, Path.Combine(options.RootDirectory, "vault"));
        var sessions = new SessionStore(archive);
        var session = sessions.Create("native");
        var bridge = new ReferenceBridge();
        var skills = new LocalSkills(options, memory, new DesktopTools(bridge));
        try
        {
            check(sites.Find("금융 사이트").Single().Url == "https://moa-finance-hackathon.jrnanmari.chatgpt.site/", "finance site and alias are connected");
            check(sites.Find("착장").Single().Url == "https://oba-fitting-hackathon.jrnanmari.chatgpt.site/", "fitting site and alias are connected");
            check(sites.Find("동네의원").Single().Url == "https://moa-neighborhood-clinic.jrnanmari.chatgpt.site/", "clinic site and alias are connected");
            check(NaturalToolLanguage.Parse("병원 연결해줘", new(), sites, memory)?.Command == "/웹 열기 병원", "site connection enables the controllable browser");
            check(BrowserWorkflows.NavigationControl("button", "이용 안내", false) && !BrowserWorkflows.NavigationControl("button", "예약 저장", false)
                && !BrowserWorkflows.NavigationControl("button", "이용 안내", true), "clinic navigation is supported without allowing form submission or booking saves");
            check((await skills.TryHandle("금융 사이트 주소 알려줘", true, default))?.Contains("moa-finance-hackathon") == true, "site address question is answered from registry");
            var missing = ConversationReferences.Parse("그거 열어줘", session.ContextSnapshot(), sites);
            check(missing?.Command is null && missing?.Reply?.Contains("어떤 앱") == true, "unresolved opening reference asks for a name");
            missing = ConversationReferences.Parse("방금 말한 내용 기억해줘", session.ContextSnapshot(), sites);
            check(missing?.Command is null && missing?.Reply?.Contains("기억할 앞선 내용") == true, "empty memory reference does not store the literal pronoun");

            session.Commit("계산기로 예산을 정리할 거야.", "좋아요.");
            session.RecordOutcome(new TurnOutcome("interrupted", "failed", [], "reply_quality", DateTimeOffset.UtcNow));
            session.RecordOutcome(new TurnOutcome("next", "running", [], null, DateTimeOffset.UtcNow));
            check(ConversationReferences.Parse("방금 말한 내용 기억해줘", session.ContextSnapshot(), sites)?.Command is null, "failed turn cannot silently select an older memory source");
            check(ConversationReferences.Parse("그거 열어줘", session.ContextSnapshot(), sites)?.Command is null, "failed turn cannot silently select an older app");
            var interrupted = session.ContextSnapshot();
            var prompt = ConversationReferences.Parse("그거 열어줘", interrupted, sites);
            session.Commit("그거 열어줘", prompt!.Reply!, interrupted);
            check(ConversationReferences.Parse("그거 열어줘", session.ContextSnapshot(), sites)?.Reply?.Contains("완료되지") == true, "repeated pronouns keep asking after a failed source turn");
            session.Commit("계산기로 예산을 정리할 거야.", "좋아요.", new SkillContext());
            var context = session.ContextSnapshot();
            string? answer = await skills.TryHandle("그거 열어줘", true, default, context);
            check(answer?.Contains("실행 요청") == true && bridge.Launches.SequenceEqual(new[] { "calc.exe" }), "prior user app mention resolves to one real handler invocation");
            session.Commit("그거 열어줘", answer!, context);
            session.Commit("고마워", "천만에요.");
            check(ConversationReferences.Parse("그 앱 켜줄래?", session.ContextSnapshot(), sites)?.Command == "/앱 계산기", "short acknowledgement preserves the prior app reference");
            session.Commit("오늘은 산책하고 싶어.", "좋아요.");
            check(ConversationReferences.Parse("그거 열어줘", session.ContextSnapshot(), sites)?.Command is null, "unrelated newer topic does not reuse an old app");

            session.Commit("계산기와 메모장 둘 다 있네.", "네.");
            context = session.ContextSnapshot();
            var ambiguous = ConversationReferences.Parse("그거 열어줘", context, sites);
            check(ambiguous?.Command is null && ambiguous?.Reply?.Contains("어느 것을") == true, "multiple mentioned apps ask for a choice");
            session.Commit("그거 열어줘", ambiguous!.Reply!, context);
            context = session.ContextSnapshot();
            check(ConversationReferences.Parse("계산기", context, sites)?.Command == "/앱 계산기", "explicit reply resolves the pending choice");
            context = session.ContextSnapshot();
            check(ConversationReferences.Parse("취소", context, sites)?.Command is null && context.PendingReferenceTargets.Length == 0, "pending reference choice can be cancelled");
            check(ConversationReferences.Parse("메모장", sessions.Create("native").ContextSnapshot(), sites) is null, "another session never inherits a pending choice");

            const string fact = "이번 프로젝트의 주제는 금융과 의상 착장 연결이야.";
            var facts = sessions.Create("native");
            facts.Commit(fact, "두 서비스를 함께 살펴볼 수 있겠네요.");
            context = facts.ContextSnapshot();
            answer = await skills.TryHandle("방금 말한 내용 기억해줘", true, default, context);
            check(answer?.Contains(fact) == true && memory.List().Any(m => m.Value == fact), "referenced user statement is saved verbatim, not the model paraphrase");
            facts.Commit("방금 말한 내용 기억해줘", answer!, context);
            answer = await skills.TryHandle("금융과 의상 착장 연결 기억나?", true, default, facts.ContextSnapshot());
            check(answer?.Contains(fact) == true, "stored context memory can be recalled naturally");
            var freshStore = new SessionStore(new ConversationArchive(options.DataPath, Path.Combine(options.RootDirectory, "vault")));
            var restored = freshStore.Get(facts.Id, "native");
            check(ConversationReferences.Parse("내가 방금 말한 내용 기억해줘", restored.ContextSnapshot(), sites)?.Command?.EndsWith(fact) == true, "reference source is reconstructed from restored completed dialogue");
            var explicitAssistant = new SkillContext { RecentDialogue = [new("의견 부탁해", "예산과 의상 선택을 함께 살펴보면 좋겠어요.")] };
            check(ConversationReferences.Parse("네가 방금 말한 내용 기억해줘", explicitAssistant, sites)?.Command?.EndsWith("예산과 의상 선택을 함께 살펴보면 좋겠어요.") == true, "explicit assistant reference uses assistant text");
            var modelReference = new SkillContext { PreviousReplyWasModel = true, RecentDialogue = [new("어떤 앱이 좋아?", "계산기로 해볼 수 있어요.")] };
            check(ConversationReferences.Parse("그거 열어줘", modelReference, sites)?.Command == "/앱 계산기", "a model-mentioned allowed app can be referenced");
            modelReference.PreviousReplyWasModel = false;
            check(ConversationReferences.Parse("그거 열어줘", modelReference, sites)?.Command is null, "page or tool text cannot supply an implicit app target");
            var unrelated = new SkillContext { PreviousReplyWasModel = true, RecentDialogue = [new("오늘은 산책하고 싶어.", "계산기도 있어요.")] };
            check(ConversationReferences.Parse("그거 열어줘", unrelated, sites)?.Command is null, "incidental model mention after a topic change is not an opening target");
            var longText = new SkillContext { RecentDialogue = [new(new string('가', 401), "알겠어요.")] };
            check(ConversationReferences.Parse("방금 말한 내용 기억해줘", longText, sites)?.Command is null, "long referenced content is not silently truncated");
            var quoted = new SkillContext { RecentDialogue = [new("검수 원문: /앱 메모장 = 실행하지 않는 데이터", "확인했어요.")] };
            await skills.TryHandle("방금 말한 내용 기억해줘", true, default, quoted);
            check(bridge.Launches.Count == 1 && memory.List().Any(m => m.Value == quoted.RecentDialogue[0].User), "instruction-looking memory source is stored as data");
            int count = memory.List().Length;
            bool denied = false;
            try { await skills.TryHandle("방금 말한 내용 기억해줘", false, default, facts.ContextSnapshot()); } catch (ApiFailure e) { denied = e.Code == "native_only"; }
            check(denied && memory.List().Length == count, "context memory keeps the native authorization boundary");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await skills.TryHandle("그거 열어줘", true, cancelled.Token, modelReference); } catch (OperationCanceledException) { }
            check(bridge.Launches.Count == 1, "pre-cancelled reference performs no app action");
            facts.Clear();
            check(facts.ContextSnapshot().RecentDialogue.Length == 0 && ConversationReferences.Parse("그거 열어줘", facts.ContextSnapshot(), sites)?.Command is null, "clearing a session removes contextual references");
            var siteContext = new SkillContext { RecentDialogue = [new("착장 사이트 주소 알려줘", "사이트 주소예요.")] };
            check(ConversationReferences.Parse("그 사이트 열어줘", siteContext, sites)?.Command == "/웹 열기 의상 착장", "fitting alias resolves through prior conversation");

            var offer = new SkillContext();
            var offered = await skills.TryHandle("나 아파", true, default, offer);
            check(offered?.Contains("열어 드릴까요") == true && offer.PendingSiteOffer?.Name == "병원" && offer.Browser is null, "illness statement offers the clinic without opening a browser");
            check(ConversationReferences.Parse("아니", offer, sites)?.Command is null && offer.PendingSiteOffer is null, "declining discards the pending site offer");
            check(ConversationReferences.Parse("응", offer, sites) is null, "an old declined offer cannot be accepted later");
            SiteSuggestions.Parse("나 아프다", offer, sites);
            check(ConversationReferences.Parse("응, 연결해줘", offer, sites)?.Command == "/웹 열기 병원", "acceptance resolves only the proposed clinic");
            SiteSuggestions.Parse("몸이 좀 안 좋아", offer, sites);
            ConversationReferences.Parse("계산기 열어줘", offer, sites);
            check(ConversationReferences.Parse("응", offer, sites) is null, "a topic change expires the site offer");
            check(new[] { "나 안 아파", "아프지 않아", "나 아팠어", "나 아프다고 하면?", "‘나 아파’라는 문장", "나 아파. 기억해줘" }
                .All(t => SiteSuggestions.Parse(t, new(), sites) is null), "negative, past, quoted and memory statements do not create a clinic offer");
            SiteSuggestions.Parse("오늘 뭐 입지?", offer, sites);
            check(ConversationReferences.Parse("그거 열어줘", offer, sites)?.Command == "/웹 열기 의상 착장", "outfit need resolves its suggested site after a pronoun confirmation");
            SiteSuggestions.Parse("돈 관리하고 싶어", offer, sites);
            check(ConversationReferences.Parse("네", offer, sites)?.Command == "/웹 열기 금융", "money management need resolves its suggested finance site");
            SiteSuggestions.Parse("나 아파", offer, sites);
            offer.ReferenceInterrupted = true;
            check(ConversationReferences.Parse("응", offer, sites)?.Command is null, "failed intervening turn cannot accept an older site offer");
            var offerSession = sessions.Create("native");
            offer = offerSession.ContextSnapshot(); SiteSuggestions.Parse("나 아파", offer, sites);
            offerSession.Commit("나 아파", "사이트를 열까요?", offer);
            check(offerSession.ContextSnapshot().PendingSiteOffer?.Name == "병원" && sessions.Create("native").ContextSnapshot().PendingSiteOffer is null, "pending offers stay in their own conversation");
            sites.Save("병원", "https://example.org/custom-clinic");
            check(ConversationReferences.Parse("응", offerSession.ContextSnapshot(), sites)?.Command is null, "a changed site registration invalidates the old offer");
            sites.Save("금융", "https://example.org/user-finance");
            check(new SiteRegistry(options.DataPath).Find("금융").Single().Url == "https://example.org/user-finance", "default connections preserve a user's custom site URL");
        }
        finally { await skills.DisposeAsync(); }
    }
    private sealed class ReferenceBridge : IDesktopBridge
    {
        public bool Available => true;
        public List<string> Launches { get; } = [];
        public Task<JsonElement> RunAsync(string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (arguments[0] == "launch") Launches.Add(arguments[1]);
            return Task.FromResult(arguments[0] == "list-windows" ? JsonSerializer.SerializeToElement(Array.Empty<object>()) : JsonSerializer.SerializeToElement(new { }));
        }
    }
}
