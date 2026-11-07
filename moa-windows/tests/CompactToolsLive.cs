using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Moa;

// Real compact Form -> installed engine -> desktop bridge/Chrome/SQLite.
// Run in the user's Explorer desktop, with a private engine.json beside this executable.
class CompactToolsLive
{
    static string root, phase;
    static int exitCode;
    static bool finished;
    static readonly List<string> checks = new List<string>();
    static readonly List<string> prompts = new List<string>();
    static readonly string memoryKey = "작은창검수코드";
    static readonly string memoryValue = "해달-초록-7429";
    static readonly string contextFact = "이번 프로젝트의 주제는 금융과 의상 착장 연결이야.";
    static TextBox input;
    static RichTextBox transcript;
    static Button send;
    static MoaStateController state;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add("PASS " + label); Save(); }
    static void Save() { File.WriteAllLines(Path.Combine(root, phase + "-results.txt"), checks.ToArray()); }
    static Button FindSend(Control parent)
    {
        foreach (Control c in parent.Controls) { var b = c as Button; if (b != null && b.AccessibleName == "메시지 보내기") return b; var child = FindSend(c); if (child != null) return child; }
        return null;
    }
    static async Task<string> Ask(string text, string expected = null, bool error = false, bool model = false)
    {
        prompts.Add(text); File.WriteAllLines(Path.Combine(root, phase + "-prompts.txt"), prompts.ToArray());
        input.Text = text; send.PerformClick();
        bool observedModel = false;
        for (int i = 0; i < (model ? 2400 : 900) && (state.Busy || input.ReadOnly); i++)
        {
            await Task.Delay(100);
            if (model && i % 5 == 0) { string runtime = await Status(); observedModel |= runtime == "loading" || runtime == "ready"; }
        }
        if (state.Busy || input.ReadOnly) throw new Exception("UI timed out: " + text);
        if (error)
        {
            Check(state.Outcome == Outcome.Error, "blocked browser action is displayed as an error");
            return state.Notice;
        }
        if (state.Outcome != Outcome.Completed) throw new Exception(text + " -> " + state.Notice);
        string answer = state.Answer;
        Check(!String.IsNullOrWhiteSpace(answer) && transcript.Text.Contains(answer), "compact UI displays completed result: " + text.Split(' ')[0]);
        if (expected != null && !answer.Contains(expected)) throw new Exception("Expected result missing for " + text + ": " + answer.Substring(0, Math.Min(350, answer.Length)));
        if (model) Check(observedModel, "ordinary dialogue runs the real local CPU model");
        return answer;
    }
    static async Task<string> Status()
    {
        return await Task.Run(() => {
            var req = (HttpWebRequest)WebRequest.Create(MoaLocalRuntime.BaseUrl + "/v1/status"); req.Proxy = null; req.Timeout = 3000;
            using (var response = req.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
                return Convert.ToString(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd())["state"]);
        });
    }
    [STAThread] static int Main(string[] args)
    {
        root = AppDomain.CurrentDomain.BaseDirectory; phase = args[1];
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        state = new MoaStateController { LocalMode = true };
        using (var fixture = new FixtureSite())
        using (var pet = new MoaPetForm(args[0], state))
        using (var chat = new MoaCompactChat(pet))
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            chat.Location = new Point(Math.Max(area.Left, area.Right - chat.Width - 20), area.Top + 40);
            chat.TopMost = false; chat.Text = "모아 · 기능 검수용 작은 창";
            chat.FormClosed += delegate(object sender, FormClosedEventArgs e) { if (!finished) File.WriteAllText(Path.Combine(root, phase + "-early-close.txt"), e.CloseReason.ToString()); };
            chat.Shown += async delegate {
                try
                {
                    input = (TextBox)chat.Controls.Find("CompactInput", true)[0];
                    transcript = (RichTextBox)chat.Controls.Find("CompactTranscript", true)[0]; send = FindSend(chat);
                    for (int i = 0; i < 300 && input.ReadOnly; i++) await Task.Delay(100);
                    Check(!input.ReadOnly && send != null, "compact chat finishes history load");
                    bool natural = phase.StartsWith("natural-");
                    bool contextual = phase.StartsWith("context-");
                    bool clinic = phase.StartsWith("clinic-");
                    bool variations = phase.StartsWith("variations-");
                    bool offerReplies = phase.StartsWith("offer-replies-");
                    bool scenario = phase.StartsWith("scenario-");
                    if (scenario)
                    {
                        if (phase.EndsWith("restore"))
                        {
                            Check(transcript.Text.Contains("모의 결제 완료"), "fresh UI restores the completed purchase conversation");
                            await Ask("모의 구매 기록 보여줘", "잔액 88,000원");
                            await Ask("구매 기준 보여줘", "허리 치수");
                            await Ask("모의 결제 승인", "중복 결제하지 않았어요");
                            await Ask("시연 시작", "허리 치수");
                            Check(true, "receipt and feedback survive restart and inform the next demo");
                        }
                        else
                        {
                            await Ask("날씨가 춥네", "따뜻한 겉옷");
                            await Ask("옷을 사야겠어", "따뜻한 겉옷");
                            Check(transcript.Text.Contains("Fitting Lab"), "cold-weather clothing request opens the actual fitting site");
                            await Ask("옷을 사야할꺼 같아", "Fitting Lab");
                            await Ask("착장 사이트 연결", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("옷 사고싶어", "Fitting Lab");
                            await Ask("시연 시작", "사용 가능 60,000원");
                            await Ask("응", "OWN-BLACK-JACKET");
                            await Ask("응", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            Check(true, "reported purchase and connection phrases execute the idea scenario instead of model-only replies");
                            await Ask("사이트 목록", "재고 관리");
                            await Ask("3D 스튜디오 열어줘", "OBA Studio");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("옷장 열어줘", "로그인 또는 접근 권한");
                            await Ask("재고 관리 열어줘", "THREAD");
                            Check(true, "wardrobe access is reported and the now-public inventory site opens");
                            await Ask("아이디어 열어줘", "OBA.RUN");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("면접 옷 사줘", "실제 계좌나 보유 옷 조회 결과가 아니에요");
                            await Ask("응", "사용 가능 60,000원");
                            await Ask("새 재킷 살래", "승인할 수 없어요");
                            await Ask("내 옷부터 볼래", "OWN-BLACK-JACKET");
                            await Ask("착장 비교", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("기존 재킷도 괜찮네. 바지만 바꾸면 되겠다", "DEMO-PANTS-WASH");
                            await Ask("물세탁 가능한 걸로 볼래", "총지출 42,000원");
                            await Ask("아니 지금은 됐어", "실행하지 않았어요");
                            await Ask("모의 구매 기록 보여줘", "아직 모의 구매 기록이 없어요");
                            await Ask("물세탁 가능한 걸로 볼래", "추가 사용 가능 18,000원");
                            await Ask("모의 결제 승인", "잔액 88,000원");
                            await Ask("모의 결제 승인", "중복 결제하지 않았어요");
                            await Ask("금융 열어줘", "가상금융");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("일주일 후", "시연용 시간 이동");
                            await Ask("관리하기는 편했는데, 오래 앉아 있으니까 허리가 불편했어", "먼저 확인하도록 설정할까요");
                            await Ask("적용해줘", "확인 기준을 저장했어요");
                            await Ask("구매 기준 보여줘", "허리 치수");
                            Check(true, "idea scenario completes reserved budget, owned jacket, approval, mock payment and feedback");
                        }
                    }
                    else if (offerReplies)
                    {
                        if (phase.EndsWith("restore"))
                        {
                            Check(transcript.Text.Contains("병원 말고 착장"), "fresh UI restores acceptance, refusal and replacement dialogue");
                            await Ask("응 열어줘", "사이트 제안이 없어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("아니 착장으로 바꿔줘", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            Check(true, "replacement works after restart without reviving an old proposal");
                        }
                        else
                        {
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("아니 지금은 됐어", "열지 않을게요");
                            Check(!Directory.Exists(Path.Combine(root, "data", "browser-profile")), "natural refusal never starts Chrome");
                            await Ask("응 열어줘", "사이트 제안이 없어요");
                            Check(!Directory.Exists(Path.Combine(root, "data", "browser-profile")), "later assent cannot revive a refused proposal");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("응 열어줘", "동네의원");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("병원 말고 착장", "Fitting Lab");
                            await Ask("사이트 화면 보여줘", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("병원 대신 금융으로 연결해줘", "가상금융");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("그거 말고 피팅", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("응 병원 말고 착장", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("아니요 지금은 괜찮아요", "열지 않을게요");
                            await Ask("머리가 아파", "문진 사이트");
                            await Ask("기능 알려줘", "모아");
                            await Ask("응 열어줘", "사이트 제안이 없어요");
                            // A pending offer is not silently restored as executable consent.
                            await Ask("머리가 아파", "문진 사이트");
                        }
                    }
                    else if (variations)
                    {
                        if (phase.EndsWith("restore"))
                        {
                            Check(transcript.Text.Contains("이번 달 돈을 너무 썼어"), "fresh UI restores varied-phrase dialogue");
                            await Ask("머리가 좀 아픈데요", "문진 사이트");
                            await Ask("응", "동네의원");
                            await Ask("사이트 닫아줘", "닫았어요");
                            Check(true, "varied phrase offer and acceptance work after engine restart");
                        }
                        else
                        {
                            string[] needs = { "머리가 아파", "오늘 입을 옷 좀 골라줘", "이번 달 돈을 너무 썼어" };
                            string[] names = { "문진 사이트", "의상 착장", "가상금융" };
                            for (int n = 0; n < needs.Length; n++)
                            {
                                await Ask(needs[n], names[n]);
                                Check(!Directory.Exists(Path.Combine(root, "data", "browser-profile")), "proposal alone never starts Chrome: " + needs[n]);
                                await Ask("아니", "열지 않을게요");
                            }
                            await Ask(needs[0], names[0]);
                            await Ask("응", "동네의원");
                            await Ask("사이트 화면 보여줘", "동네의원");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask(needs[1], names[1]);
                            await Ask("그거 열어줘", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask(needs[2], names[2]);
                            await Ask("응, 연결해줘", "가상금융");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("모아야, 어제부터 배가 아파요", names[0]);
                            await Ask("아니요", "열지 않을게요");
                            await Ask("내 머리가 아파", names[0]);
                            await Ask("아니", "열지 않을게요");
                            await Ask("이번 달 돈을 너무 많이 썼어", names[2]);
                            await Ask("아니", "열지 않을게요");
                            await Ask("내일 뭐 입으면 좋을까?", names[1]);
                            await Ask("취소", "열지 않을게요");
                            await Ask("요즘 지출이 너무 많아요", names[2]);
                            await Ask("기능 알려줘", "모아");
                            await Ask("그거 열어줘", "어떤 앱");
                            Check(true, "a different topic discards the prior finance suggestion");
                        }
                    }
                    else if (clinic)
                    {
                        if (phase.EndsWith("restore"))
                        {
                            Check(transcript.Text.Contains("병원 사이트 주소 알려줘"), "fresh UI restores the clinic conversation");
                            await Ask("그거 열어줘", "동네의원");
                            Check(true, "clinic reference survives UI and engine restart");
                            await Ask("사이트 닫아줘", "닫았어요");
                        }
                        else
                        {
                            await Ask("사이트 목록", "병원");
                            await Ask("병원 연결해줘", "동네의원");
                            await Ask("사이트 화면 보여줘", "증상 이야기하기");
                            await Ask("이용 안내 메뉴를 눌러줘", "모아를 편하게 이용하세요");
                            await Ask("사이트 화면 보여줘", "기록은 내 선택으로");
                            Check(true, "clinic opens and guide navigation works in real Chrome");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("동네의원 열어줘", "동네의원");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("나 아파", "열어 드릴까요");
                            await Ask("아니", "열지 않을게요");
                            Check(true, "declining the clinic suggestion completes without opening");
                            await Ask("나 아프다", "실제 의료진 연결이나 진단·예약이 아닌");
                            await Ask("응", "동네의원");
                            Check(true, "affirmative reply opens the suggested clinic");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("오늘 뭐 입지?", "의상 착장");
                            await Ask("응, 연결해줘", "Fitting Lab");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("돈 관리하고 싶어", "가상금융");
                            await Ask("그거 열어줘", "가상금융");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("병원 사이트 주소 알려줘", "moa-neighborhood-clinic");
                        }
                    }
                    else if (contextual)
                    {
                        if (phase.EndsWith("restore"))
                        {
                            Check(transcript.Text.Contains(contextFact), "fresh UI restores the actual contextual conversation");
                            await Ask("그거 열어줘", "가상금융");
                            Check(true, "pronoun opening resolves from conversation restored in a fresh engine");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("금융과 의상 착장 연결 기억나?", contextFact);
                            Check(true, "exact referenced memory survives UI and engine restart");
                        }
                        else
                        {
                            await Ask("그거 열어줘", "어떤 앱");
                            await Ask("방금 말한 내용 기억해줘", "기억할 앞선 내용");
                            await Ask(contextFact, null, false, true);
                            await Ask("방금 말한 내용 기억해줘", contextFact);
                            await Ask("금융과 의상 착장 연결 기억나?", contextFact);
                            Check(true, "memory contains the user's exact earlier statement");
                            await Ask("계산기로 예산을 정리할 거야. 한 문장으로만 답해줘.", null, false, true);
                            await Ask("그거 열어줘", "실행 요청");
                            string windows = await Ask("열린 창 보여줘");
                            var window = Regex.Match(windows, @"[^\r\n]*(?:계산기|Calculator)[^\r\n]*\(w-(\d+)\)", RegexOptions.IgnoreCase);
                            Check(window.Success && IsWindowVisible(new IntPtr(long.Parse(window.Groups[1].Value))), "contextual app request opens a visible Calculator window");
                            await Ask("금융과 의상 착장 사이트 주소 알려줘", "사이트 이름");
                            await Ask("그거 열어줘", "어느 것을");
                            await Ask("금융", "가상금융");
                            Check(true, "ambiguous pronoun waits for the user's explicit target choice");
                            await Ask("금융 사이트 주소 알려줘", "moa-finance-hackathon");
                            await Ask("그거 열어줘", "가상금융");
                            await Ask("사이트 화면 보여줘", "주식 투자");
                            await Ask("주식 투자 메뉴를 눌러줘", "주식");
                            Check(true, "connected finance site opens and navigates from the compact chat");
                            await Ask("사이트 닫아줘", "닫았어요");
                            await Ask("착장 사이트 주소 알려줘", "oba-fitting-hackathon");
                            await Ask("그 사이트 열어줘", "Fitting Lab");
                            await Ask("사이트 화면 보여줘", "의상");
                            await Ask("여기서 재킷 검색해줘", "검색칸에 입력했어요");
                            Check(true, "connected fitting site opens and accepts a catalog search");
                            await Ask("사이트 닫아줘", "닫았어요");
                            // Leave an unambiguous target as the final user topic for restart resolution.
                            await Ask("금융 사이트 주소 알려줘", "moa-finance-hackathon");
                        }
                    }
                    else if (phase.EndsWith("restore"))
                    {
                        Check(transcript.Text.Contains(memoryValue), "fresh compact process restores prior conversation");
                        await Ask(natural ? "내 " + memoryKey + "가 뭐였지?" : "/기억 찾기 " + memoryKey, memoryValue);
                        Check(true, "memory survives both engine and compact UI restart");
                        await Ask("사이트 목록", "모아검수");
                        Check(true, "site registry survives engine restart");
                    }
                    else
                    {
                        await Ask(natural ? "모아야, 기능 알려줘" : "/도구", "모아");
                        await Ask(natural ? "모아야, 계산기를 좀 켜줄래?" : "계산기 열어줘", "실행 요청");
                        string windows = await Ask(natural ? "열린 창 보여줘" : "/창");
                        var matches = Regex.Matches(windows, @"[^\r\n]*(?:계산기|Calculator)[^\r\n]*\(w-(\d+)\)", RegexOptions.IgnoreCase);
                        Check(matches.Count > 0, "calculator appears in observed window inventory");
                        long handle = long.Parse(matches[0].Groups[1].Value);
                        Check(IsWindowVisible(new IntPtr(handle)), "launched calculator has a visible Windows window");
                        await Ask(natural ? "내 " + memoryKey + "는 " + memoryValue + "이야. 기억해 줘." : "/기억 저장 " + memoryKey + " = " + memoryValue, memoryValue);
                        await Ask(natural ? "내 " + memoryKey + "가 뭐였지?" : "/기억 찾기 " + memoryKey, memoryValue);
                        if (natural)
                        {
                            await Ask("내 " + memoryKey + "는 주황이야. 기억하지 마.", "저장하지 않았어요");
                            await Ask("내 " + memoryKey + "가 뭐였지?", memoryValue);
                            await Ask("나는 비 오는 날 산책하는 걸 좋아해. 기억해 둬.", "나는 비 오는 날 산책하는 걸 좋아해");
                            await Ask("비 오는 날 기억나?", "나는 비 오는 날 산책하는 걸 좋아해");
                            await Ask("저장한 기억 보여줘", memoryValue);
                            await Ask("기억해줘", "기억할 내용");
                        }
                        await Ask(natural ? fixture.Url + " 를 모아검수라는 이름으로 등록해줘" : "사이트 등록 모아검수 " + fixture.Url, "등록");
                        await Ask(natural ? "모아검수 사이트 좀 열어줄래?" : "모아검수 조작해줘", "Moa Browser Check");
                        string home = await Ask(natural ? "지금 페이지 보여줄래?" : "사이트 화면 보여줘", "자료 탐색");
                        await Ask(natural ? "자료 탐색 메뉴를 눌러줄래?" : "사이트에서 자료 탐색 눌러줘", "Library");
                        Check(fixture.LibraryVisits > 0, "real Chrome navigated to clicked library link");
                        await Ask(natural ? "여기서 '해달'을 찾아줘" : "모아검수에서 해달 검색해줘", "검색 결과: 해달");
                        for (int i = 0; i < 30 && fixture.LastSearch != "해달"; i++) await Task.Delay(100);
                        Check(fixture.LastSearch == "해달", "real search input event reaches fixture server");
                        await Ask(natural ? "지금 화면 좀 보여줘" : "사이트 화면 보여줘", "자세히 보기");
                        await Ask(natural ? "자세히 보기 링크를 눌러줘" : "사이트에서 자세히 보기 눌러줘", "Details");
                        Check(fixture.DetailVisits > 0, "real Chrome opens selected detail page");
                        await Ask(natural ? "이전 페이지로 돌아가 줘" : "/웹 뒤로", "Library");
                        await Ask(natural ? "새로고침해 줘" : "/웹 새로고침", "Library");
                        await Ask(natural ? "삭제 버튼을 눌러줘" : "/웹 클릭 삭제", null, true);
                        Check(fixture.DeleteVisits == 0, "blocked action never reaches fixture endpoint");
                        await Ask(natural ? "지금 사이트 화면 보여줘" : "/웹 화면", "Library");
                        await Ask(natural ? "이 탭 닫아줘" : "/웹 닫기", "닫았어요");
                        Check(true, "browser controls recover after a blocked action");
                        // A public site supplied by the original engine's own Galpi integration.
                        await Ask(natural ? "모아야 갈피를 열어줄래?" : "갈피 조작해줘", "모아 전용 크롬");
                        await Ask("사이트 화면 보여줘", "갈피");
                        await Ask(natural ? "거기서 로봇을 좀 검색해줄래?" : "갈피에서 로봇 검색해줘", "검색칸에 입력했어요");
                        Check(true, "public Galpi site search is connected from compact chat");
                        await Ask(natural ? "브라우저 닫아주세요" : "/웹 닫기", "닫았어요");
                        if (natural) await Ask("모아야, 알 수 없는 앱을 열어줘", "계산기·메모장·탐색기");
                    }
                    Check(await Status() == "idle", "tool workflow leaves the CPU model idle");
                    if (natural || contextual || clinic || variations || offerReplies || scenario) Check(prompts.All(p => !p.StartsWith("/")), "all live requests use natural language without slash commands");
                }
                catch (Exception ex) { exitCode = 1; checks.Add("FAIL " + ex.Message); Save(); }
                finally { finished = true; chat.Close(); }
            };
            Application.Run(chat);
        }
        if (!finished) { exitCode = 1; checks.Add("FAIL verification window closed before all requests finished"); Save(); }
        File.WriteAllText(Path.Combine(root, phase + "-exit.txt"), exitCode.ToString());
        return exitCode;
    }
    sealed class FixtureSite : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        volatile bool stopped;
        public volatile int LibraryVisits, DetailVisits, DeleteVisits;
        public volatile string LastSearch = "";
        public readonly string Url;
        public FixtureSite() { listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"; Task.Run(() => Serve()); }
        void Serve()
        {
            while (!stopped) try
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string request = reader.ReadLine(); if (request == null) continue;
                    string line; while (!String.IsNullOrEmpty(line = reader.ReadLine())) { }
                    var uri = new Uri(Url.TrimEnd('/') + request.Split(' ')[1]);
                    string body;
                    if (uri.AbsolutePath == "/library")
                    {
                        LibraryVisits++;
                        body = "<title>Library</title><main><h1>Library</h1><div id='search-area' hidden><input type='search' aria-label='검색' oninput=\"document.getElementById('library-results').textContent='검색 결과: '+this.value;fetch('/observed?value='+encodeURIComponent(this.value))\"></div><div id='library-results'>검색 결과</div><a href='/details'>자세히 보기</a><button onclick=\"location.href='/deleted'\">삭제</button></main><script>setTimeout(()=>document.getElementById('search-area').hidden=false,1400)</script>";
                    }
                    else if (uri.AbsolutePath == "/details") { DetailVisits++; body = "<title>Details</title><main>Details · 모아 실제 사이트 클릭 검수</main>"; }
                    else if (uri.AbsolutePath == "/deleted") { DeleteVisits++; body = "unexpected"; }
                    else if (uri.AbsolutePath == "/observed") { LastSearch = Uri.UnescapeDataString(uri.Query.Replace("?value=", "")); body = "ok"; }
                    else body = "<title>Moa Browser Check</title><main><h1>Moa Browser Check</h1><a href='/library'>자료 탐색</a></main>";
                    var bytes = Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'>" + body);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                }
            } catch { if (stopped) return; }
        }
        public void Dispose() { stopped = true; listener.Stop(); }
    }
}
