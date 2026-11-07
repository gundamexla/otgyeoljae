using VoltiEngine;

internal static class PurchaseScenarioTests
{
    public static void Run(string root, Action<bool,string> check)
    {
        string directory = Path.Combine(root, "purchase-scenario"); Directory.CreateDirectory(directory);
        var scenario = new PurchaseScenario(directory);
        var context = new SkillContext { ConversationId = Guid.NewGuid().ToString("N") };
        NaturalToolRequest? Turn(string text) => scenario.TryHandle(text, true, context, default);
        check(Turn("허리가 불편했어") is null && Turn("적용해줘") is null, "ordinary conversation outside a purchase demo is not hijacked");
        check(Turn("면접 옷 사줘")?.Reply?.Contains("실제 계좌나 보유 옷 조회 결과가 아니에요") == true && scenario.Read(context.ConversationId) is null, "interview request offers labelled simulation before creating any budget");
        check(Turn("응")?.Reply?.Contains("사용 가능 60,000원") == true, "demo starts with explicit reserved living expenses");
        check(Turn("내 옷부터 볼래")?.Reply?.Contains("OWN-BLACK-JACKET") == true, "owned demo jacket is distinguished from catalog inventory");
        check(Turn("착장 비교")?.Command == "/웹 열기 의상 착장", "demo comparison uses existing fitting connection");
        check(Turn("기존 재킷도 괜찮네. 바지만 바꾸면 되겠다")?.Reply?.Contains("실제 상품 정보 아님") == true, "candidate price and care evidence is labelled as demo data");
        check(Turn("새 재킷 살래")?.Reply?.Contains("승인할 수 없어요") == true && context.PurchaseConsent is null, "reserve budget prevents approval of the expensive jacket");
        Turn("모의 결제 승인");
        check(scenario.PaymentCount() == 0 && scenario.Read(context.ConversationId)!.Balance == 130000, "blocked approval does not change the ledger");
        check(Turn("물세탁 가능한 걸로 볼래")?.Reply?.Contains("총지출 42,000원") == true, "quote includes delivery cost before approval");
        Turn("아니 지금은 됐어"); Turn("모의 결제 승인");
        check(scenario.PaymentCount() == 0, "cancelled quote cannot later be paid by stale assent");
        Turn("물세탁 가능한 걸로 볼래"); context.ReferenceInterrupted = true;
        check(Turn("모의 결제 승인")?.Reply?.Contains("다시 확인") == true && scenario.PaymentCount() == 0, "failed intervening turn invalidates purchase approval");
        Turn("결재함 보여줘"); Turn("오늘은 산책하고 싶어");
        check(Turn("모의 결제 승인")?.Reply?.Contains("승인할 카드가 없어요") == true, "topic change expires payment consent");
        Turn("결재함 보여줘");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool cancelled = false;
            try { scenario.TryHandle("모의 결제 승인", true, context, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && scenario.PaymentCount() == 0, "pre-cancelled confirmation never spends demo funds");
        }
        var restarted = new PurchaseScenario(directory);
        context = new SkillContext { ConversationId = context.ConversationId };
        check(restarted.TryHandle("모의 결제 승인", true, context, default)?.Reply?.Contains("승인할 카드가 없어요") == true, "restart restores quote but requires fresh review before payment");
        Turn("결재함 보여줘");
        var paid = Turn("모의 결제 승인");
        check(paid?.Prefix?.Contains("잔액 88,000원") == true && paid.Command?.StartsWith("/웹 결제내역 ") == true && scenario.Read(context.ConversationId)!.Balance == 88000, "approved demo payment atomically persists balance and opens finance receipt");
        Turn("모의 결제 승인");
        check(scenario.PaymentCount() == 1, "duplicate confirmation cannot repeat the payment");
        check(new PurchaseScenario(directory).Read(context.ConversationId)!.Paid, "receipt survives a new store instance");
        Turn("일주일 후"); Turn("관리하기는 편했는데, 오래 앉아 있으니까 허리가 불편했어");
        check(Turn("구매 기준 보여줘")?.Reply?.Contains("아직 적용한") == true, "feedback is not stored before explicit application");
        Turn("허리가 불편했어");
        check(Turn("적용해줘")?.Reply?.Contains("확인 기준을 저장") == true, "explicit feedback approval stores a future purchase criterion");
        check(new PurchaseScenario(directory).TryHandle("구매 기준 보여줘", true, context, default)?.Reply?.Contains("허리 치수") == true, "purchase criterion survives restart");
        check(Turn("시연 시작")?.Reply?.Contains("허리 치수") == true, "next demo uses the saved purchase criterion");
        var other = new SkillContext { ConversationId = Guid.NewGuid().ToString("N") };
        check(scenario.TryHandle("모의 구매 기록 보여줘", true, other, default)?.Reply?.Contains("진행 중인") == true, "another conversation does not inherit this purchase ledger");
        bool denied = false;
        try { scenario.TryHandle("시연 시작", false, other, default); } catch (ApiFailure e) { denied = e.Code == "native_only"; }
        check(denied && scenario.Read(other.ConversationId) is null, "web callers cannot create a native purchase scenario");

        var sites = new SiteRegistry(directory, _ => {});
        foreach (var pair in new[] { ("3D 스튜디오", "oba-fitting-studio"), ("옷장", "oba-wardrobe"), ("매장 재고", "thread-stock-boutique"), ("아이디어", "oba-run-archive") })
            check(sites.Find(pair.Item1).Single().Url.Contains(pair.Item2), "remaining site and alias connected: " + pair.Item1);
        foreach (string text in new[] { "옷장 보고 싶어", "매장 재고 확인하고 싶어", "3D로 입어보고 싶어", "아이디어 시나리오 보고 싶어" })
        {
            var c = new SkillContext();
            check(SiteSuggestions.Parse(text, c, sites)?.Command is null && c.PendingSiteOffer is not null, "remaining-site need offers without opening: " + text);
        }
        var memory = new MemoryStore(directory);
        check(NaturalToolLanguage.Parse("착장 사이트 연결", new(), sites, memory)?.Command == "/웹 열기 의상 착장", "user-reported noun-ending connection request invokes the real browser tool");
        foreach (string phrase in new[] { "옷 사고싶어", "옷을 사야할꺼 같아", "옷을 사야 할 것 같아", "옷을 사야겠어" })
        {
            var c = new SkillContext { ConversationId = Guid.NewGuid().ToString("N") };
            var action = scenario.TryHandle(phrase, true, c, default);
            check(action?.Command == "/웹 열기 의상 착장" && scenario.Read(c.ConversationId) is null, "ordinary clothing intent opens the actual site without inventing a demo budget: " + phrase);
        }
        var coldContext = new SkillContext { ConversationId = Guid.NewGuid().ToString("N"), RecentDialogue = [new("날씨가 춥네", "따뜻하게 입으세요.")] };
        check(scenario.TryHandle("옷을 사야겠어", true, coldContext, default)?.Prefix?.Contains("따뜻한 겉옷") == true, "clothing connection retains recent cold-weather context");
        check(NaturalToolLanguage.Parse("기본 크롬으로 옷장 열어줘", new(), sites, memory)?.Command == "/사이트 외부 의상 자료실", "private site can be requested in the user's existing Chrome profile");
    }
}
