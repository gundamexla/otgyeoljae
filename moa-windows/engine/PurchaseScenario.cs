using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record PurchaseConsent(string Kind, string RunId, int Version);
public sealed record PurchaseDemo
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Version { get; set; }
    public string Step { get; set; } = "budget";
    public int Balance { get; set; } = 130000;
    public int Reserve { get; set; } = 70000;
    public int Price { get; set; }
    public int Shipping { get; set; }
    public string Item { get; set; } = "";
    public bool Paid { get; set; }
}

// An explicitly labelled demo ledger. It never reads bank accounts, claims
// catalog clothes are user-owned, posts to shopping sites, or charges real money.
public sealed class PurchaseScenario
{
    private const string Label = "[면접 구매 시연 · 가상 데이터 / 실제 결제 없음]\n";
    private readonly string connectionString;
    private readonly object gate = new();
    public PurchaseScenario(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "purchase-demo.sqlite3"), Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS scenarios(conversation TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS demo_payments(id TEXT PRIMARY KEY,amount INTEGER NOT NULL,item TEXT NOT NULL,created TEXT NOT NULL); CREATE TABLE IF NOT EXISTS demo_preferences(key TEXT PRIMARY KEY,value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public PurchaseDemo? Read(string conversation)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM scenarios WHERE conversation=$key"; cmd.Parameters.AddWithValue("$key", conversation);
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<PurchaseDemo>(value) : null;
    }
    public int PaymentCount()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM demo_payments";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    private string Preference()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM demo_preferences WHERE key='pants'";
        return cmd.ExecuteScalar() as string ?? "아직 적용한 구매 후 기준이 없어요.";
    }
    private void Save(string conversation, PurchaseDemo state, CancellationToken token, bool payment = false, string? preference = null)
    {
        token.ThrowIfCancellationRequested();
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        state.Version++;
        cmd.CommandText = "INSERT INTO scenarios(conversation,payload) VALUES($key,$data) ON CONFLICT(conversation) DO UPDATE SET payload=excluded.payload";
        cmd.Parameters.AddWithValue("$key", conversation); cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(state)); cmd.ExecuteNonQuery();
        if (payment)
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO demo_payments(id,amount,item,created) VALUES($id,$amount,$item,$created)";
            cmd.Parameters.AddWithValue("$id", state.Id); cmd.Parameters.AddWithValue("$amount", state.Price + state.Shipping);
            cmd.Parameters.AddWithValue("$item", state.Item); cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
        if (preference is not null)
        {
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO demo_preferences(key,value) VALUES('pants',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$value", preference); cmd.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); tx.Commit();
    }
    private static string Compact(string input) => Regex.Replace(NaturalToolLanguage.Address(input), @"[\s,.!?。？！]", "");
    private static NaturalToolRequest Reply(string value) => new(Reply: Label + value);
    private static bool Yes(string text) => text is "응" or "네" or "좋아" or "좋아요" or "승인" or "승인할게" or "승인해줘" or "모의결제승인" or "이구성으로진행해줘" or "적용해줘";
    private static bool No(string text) => text is "아니" or "아니요" or "취소" or "취소해줘" or "아니지금은됐어" or "지금은됐어";
    private static string Budget(PurchaseDemo s) => $"잔액 {s.Balance:N0}원 / 남겨둘 생활비 {s.Reserve:N0}원 / 사용 가능 {s.Balance - s.Reserve:N0}원";
    private static string Receipt(PurchaseDemo s) => $"모의 결제 완료 · {s.Item}\n상품 {s.Price:N0}원 + 배송비 {s.Shipping:N0}원 = {s.Price + s.Shipping:N0}원\n{Budget(s)}\n보유 검정 재킷은 그대로 활용해요. 구매 기록은 모아 전용 시연 장부에 저장했어요. 금융 사이트나 실제 계좌에 반영한 것은 아니에요.\n‘일주일 후’로 구매 후 피드백을 이어갈 수 있어요.";

    public NaturalToolRequest? TryHandle(string input, bool native, SkillContext context, CancellationToken token)
    {
        string text = Compact(input);
        var consent = context.PurchaseConsent; context.PurchaseConsent = null;
        bool purchaseNeed = Regex.IsMatch(text, @"^(?:나|난|나는|저|저는)?(?:새)?옷(?:을)?(?:좀)?(?:사고싶어(?:요)?|사야(?:할|될)(?:것|거|꺼)같아(?:요)?|사야겠어(?:요)?|사려고해(?:요)?)$");
        if (purchaseNeed)
        {
            if (!native) throw new ApiFailure(403, "native_only", "사이트 연결은 Windows 모아에서 해 줘.");
            token.ThrowIfCancellationRequested();
            context.PendingSiteOffer = null; context.PendingReferenceTargets = [];
            bool cold = context.RecentDialogue.TakeLast(2).Any(t => Regex.IsMatch(Compact(t.User), @"^(?:오늘|요즘|밖에|날씨가|날씨|너무|좀|많이|정말|진짜)*(?:춥네|춥다|추워|추워요|춥네요)$"));
            return new(Command: "/웹 열기 의상 착장", Prefix: (cold ? "앞서 춥다고 하셨으니 따뜻한 겉옷을 살펴볼 수 있도록 " : "옷을 살펴보고 비교할 수 있도록 ") + "착장 사이트를 연결할게요.\n실제 예산·보유 옷은 아직 조회하지 않았어요. 가상 예산부터 체험하려면 ‘시연 시작’이라고 해 주세요.\n");
        }
        bool start = text is "시연시작" or "면접시연시작" or "면접구매시연시작" or "결재함시연시작";
        bool interview = Regex.IsMatch(text, @"^(?:다음주)?면접(?:에|용)?(?:입을)?(?:옷|재킷)(?:이필요해.*사도될까|하나사줘|사줘|좀골라줘|골라줘)$") || text == "다음주면접인데옷좀골라줘";
        bool command = start || interview || text is "결재함보여줘" or "결재카드보여줘" or "모의구매기록보여줘" or "구매기준보여줘" or "내옷부터볼래" or "보유옷확인" or "가진옷부터볼래" or "착장비교" or "착장비교해줘" or "물세탁가능한걸로볼래" or "기존재킷도괜찮네바지만바꾸면되겠다" or "바지만바꾸면되겠다" or "재킷살래" or "새재킷살래" or "일주일후" or "관리하기는편했는데오래앉아있으니까허리가불편했어" or "허리가불편했어";
        bool confirm = consent is not null && (Yes(text) || No(text));
        if (!command && !confirm && text is not ("모의결제승인" or "승인할게" or "승인해줘" or "적용해줘")) return null;
        bool explicitControl = start || interview || text is "결재함보여줘" or "결재카드보여줘" or "모의구매기록보여줘" or "구매기준보여줘" or "모의결제승인";
        if (!explicitControl && !confirm && Read(context.ConversationId) is null) return null;
        if (!native) throw new ApiFailure(403, "native_only", "결재함 시연은 Windows 모아에서 해 줘.");
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(context.ConversationId, "N", out _)) return Reply("새 대화에서 시연을 시작해 주세요.");
        context.PendingSiteOffer = null; context.PendingReferenceTargets = [];
        lock (gate)
        {
            var state = Read(context.ConversationId);
            if (context.ReferenceInterrupted && confirm && Yes(text))
                return Reply("앞선 요청이 완료되지 않았어요. ‘결재함 보여줘’로 현재 상태를 다시 확인한 뒤 승인해 주세요.");
            if (confirm && No(text))
            {
                if (state is not null && consent!.Kind == "pay") { state.Step = "cancelled"; Save(context.ConversationId, state, token); }
                return Reply("취소했어요. 모의 결제나 구매 기준 변경을 실행하지 않았어요.");
            }
            if (interview)
            {
                context.PurchaseConsent = new("start", "", 0);
                return Reply("‘아이디어.’ 채팅의 면접 구매 이야기를 시연할까요? 예시 잔액 130,000원·생활비 70,000원·보유 검정 재킷을 사용해요. 실제 계좌나 보유 옷 조회 결과가 아니에요.\n‘응’ 또는 ‘시연 시작’으로 시작해 주세요.");
            }
            if (start || confirm && consent!.Kind == "start")
            {
                if (state is null || state.Paid) { state = new(); Save(context.ConversationId, state, token); }
                context.ReferenceInterrupted = false;
                context.PurchaseConsent = new("owned", state.Id, state.Version);
                return Reply(Budget(state) + "\n새 재킷 89,000원은 사용 가능 금액보다 29,000원 많아 생활비를 쓰게 돼요.\n시연 보유 옷에 검정 재킷이 있어요. ‘내 옷부터 볼래’로 먼저 살펴볼까요?\n이전 피드백(시연 예시): 드라이클리닝 맡기기 번거로워서 잘 안 입게 돼.\n적용한 구매 기준: " + Preference());
            }
            if (text == "구매기준보여줘") return Reply(Preference());
            if (state is null) return Reply("진행 중인 면접 구매 시연이 없어요. ‘시연 시작’으로 시작해 주세요.");
            if (text is "모의구매기록보여줘") return Reply(state.Paid ? Receipt(state) : "아직 모의 구매 기록이 없어요. 승인 전에는 잔액을 바꾸지 않아요.\n" + Budget(state));
            if (confirm && consent!.Kind == "pay")
            {
                if (state.Paid) return Reply(Receipt(state) + "\n같은 구매를 다시 결제하지 않았어요.");
                if (consent.RunId != state.Id || consent.Version != state.Version || state.Step != "review") return Reply("결재 카드가 바뀌었어요. ‘결재함 보여줘’로 다시 확인해 주세요.");
                int amount = checked(state.Price + state.Shipping);
                if (amount <= 0 || amount > state.Balance - state.Reserve) return Reply("남겨둘 생활비를 침범해 모의 결제하지 않았어요.");
                state.Balance -= amount; state.Paid = true; state.Step = "paid";
                Save(context.ConversationId, state, token, payment: true);
                var receipt = JsonSerializer.Serialize(new { version = 1, id = state.Id, item = state.Item, price = state.Price, shipping = state.Shipping, total = amount, balance = state.Balance, reserve = state.Reserve, demo = true });
                return new(Command: "/웹 결제내역 " + Uri.EscapeDataString(receipt), Prefix: Label + Receipt(state) + "\n금융 화면에서 이 모의 영수증을 확인해요.\n");
            }
            if (confirm && consent!.Kind == "feedback")
            {
                if (!state.Paid || consent.RunId != state.Id || consent.Version != state.Version) return Reply("피드백 적용 요청이 바뀌었어요. 내용을 다시 말해 주세요.");
                Save(context.ConversationId, state, token, preference: "바지 구매 → 허리 치수·착용 관련 정보 확인 필요");
                return Reply("다음 구매의 확인 기준을 저장했어요.\n" + Preference() + "\n사진으로 착용감을 확정하지 않고, 다음 후보에서 확인할 항목으로 사용해요.");
            }
            if (text == "일주일후")
            {
                if (!state.Paid) return Reply("모의 구매를 마친 뒤 구매 후 피드백으로 이어갈 수 있어요.");
                state.Step = "feedback"; Save(context.ConversationId, state, token);
                return Reply("일주일 후 — 시연용 시간 이동\n면접 때 입었던 바지는 어땠어요? 실제 일주일이 지난 것은 아니에요.");
            }
            if (text is "허리가불편했어" or "관리하기는편했는데오래앉아있으니까허리가불편했어")
            {
                if (!state.Paid || state.Step != "feedback") return null;
                context.PurchaseConsent = new("feedback", state.Id, state.Version);
                return Reply("다음 바지 구매 때 허리 치수·착용 관련 정보를 먼저 확인하도록 설정할까요? ‘적용해줘’라고 하면 저장해요.");
            }
            if (text is "승인할게" or "승인해줘" or "모의결제승인" or "적용해줘")
                return Reply(state.Paid ? Receipt(state) + "\n이미 완료한 구매는 중복 결제하지 않았어요." : "지금 승인할 카드가 없어요. ‘결재함 보여줘’로 확인해 주세요.");
            if (text is "내옷부터볼래" or "보유옷확인" or "가진옷부터볼래" || confirm && consent!.Kind == "owned")
            {
                context.PurchaseConsent = new("compare", state.Id, state.Version);
                return Reply("시연 보유 옷: OWN-BLACK-JACKET · 검정 재킷 1개\n자료실의 모든 옷을 소유한 것으로 간주하지 않아요.\n새 재킷 구매 89,000원 / 가진 재킷 활용 0원.\n‘착장 비교’로 사진 착장 사이트를 열거나, ‘기존 재킷도 괜찮네. 바지만 바꾸면 되겠다’로 이어가세요.");
            }
            if (text is "착장비교" or "착장비교해줘" || confirm && consent!.Kind == "compare")
                return new(Command: "/웹 열기 의상 착장", Prefix: Label + "비교할 두 안: 새 재킷 / 시연 보유 검정 재킷. 사이트에서 사진과 의상을 직접 골라 비교해 주세요. 사진 전송·이미지 생성은 자동으로 실행하지 않았어요.\n");
            if (text is "기존재킷도괜찮네바지만바꾸면되겠다" or "바지만바꾸면되겠다")
                return Reply("기존 검정 재킷을 활용하고 바지 두 후보를 비교해요.\nDEMO-PANTS-WASH · 물세탁 가능 · 39,000원 + 배송 3,000원\nDEMO-PANTS-DRY · 드라이클리닝 · 45,000원 + 배송 3,000원\n세탁법·가격 출처: 시연 후보 카드(실제 상품 정보 아님). 이전 시연 피드백의 관리 번거로움을 반영했어요.\n‘물세탁 가능한 걸로 볼래’로 결재 카드를 만들 수 있어요.\n적용한 기준: " + Preference());
            if (text is "물세탁가능한걸로볼래" or "재킷살래" or "새재킷살래")
            {
                if (state.Paid) return Reply(Receipt(state) + "\n다른 구매는 ‘시연 시작’으로 별도 시연을 시작해 주세요.");
                bool pants = text == "물세탁가능한걸로볼래";
                state.Item = pants ? "물세탁 바지 DEMO-PANTS-WASH" : "새 재킷 DEMO-JACKET";
                state.Price = pants ? 39000 : 89000; state.Shipping = pants ? 3000 : 0; state.Step = "review";
                Save(context.ConversationId, state, token);
            }
            if (text is "결재함보여줘" or "결재카드보여줘" || state.Step == "review")
            {
                context.ReferenceInterrupted = false;
                if (state.Paid) return Reply(Receipt(state));
                if (state.Step != "review") return Reply(Budget(state) + "\n승인 대기 카드가 없어요. ‘내 옷부터 볼래’ 또는 ‘물세탁 가능한 걸로 볼래’로 이어가세요.");
                int total = state.Price + state.Shipping;
                if (total > state.Balance - state.Reserve) return Reply(Budget(state) + $"\n{state.Item} 총 {total:N0}원은 생활비를 침범해 승인할 수 없어요. ‘물세탁 가능한 걸로 볼래’로 대안을 고를 수 있어요.");
                context.PurchaseConsent = new("pay", state.Id, state.Version);
                return Reply($"결재 카드 · {state.Item}\n보유 검정 재킷 활용\n상품 {state.Price:N0}원 + 배송비 {state.Shipping:N0}원 = 총지출 {total:N0}원\n구매 후 잔액 {state.Balance - total:N0}원 / 남겨둘 생활비 {state.Reserve:N0}원 / 추가 사용 가능 {state.Balance - total - state.Reserve:N0}원\n모아 전용 가상 장부에만 기록해요. ‘모의 결제 승인’으로 실행하거나 ‘아니 지금은 됐어’로 취소해 주세요.");
            }
            return null;
        }
    }
}
