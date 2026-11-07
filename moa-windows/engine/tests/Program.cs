using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VoltiEngine;

if (args.Length == 2 && args[0] == "--fake-server")
{
    await using var fakeApp = EngineHost.Build(EngineOptions.Load(args[1]), new FakeRuntime());
    await fakeApp.RunAsync(); return;
}

var results = new List<string>();
var root = Path.Combine(SharedResources.MoaRoot, "tests", "engine-artifacts", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
void Check(bool value, string name) { results.Add((value ? "PASS " : "FAIL ") + name); Console.WriteLine(results[^1]); if (!value) throw new Exception(name); }
int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
EngineOptions Options(string directory) => new() { RootDirectory = directory, Port = FreePort(), BackendPort = FreePort(), DataDirectory = "data", ConversationVaultDirectory = Path.Combine(directory, "vault"), SystemPrompt = "너는 해달 캐릭터 로컬 도우미 모아야. 한국어로 짧고 자연스럽게 답해. 네 이름은 모아야.", PresencePenalty = 0, RequestTimeoutSeconds = 180 };
void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
try
{
    var options = Options(root); options.Validate();
    await NaturalLanguageTests.Run(root, Check);
    await ConversationReferenceTests.Run(root, Check);
    SiteSuggestionTests.Run(root, Check);
    SiteOfferReplyTests.Run(root, Check);
    PurchaseScenarioTests.Run(root, Check);
    Check(options.ModelPath == SharedResources.Model && options.RuntimePath == SharedResources.Runtime, "model and runtime use original files");
    var siteFolder = Path.Combine(root, "site-default-check"); Directory.CreateDirectory(siteFolder);
    var sites = new SiteRegistry(siteFolder, _ => throw new Exception("Unexpected site launch"));
    Check(sites.Find("갈피").Single().Url == "https://galpi-ai-studio.vercel.app/", "advertised Galpi command has a default site");
    sites.Save("갈피", "https://example.org/custom-galpi");
    var reopenedSites = new SiteRegistry(siteFolder, _ => throw new Exception("Unexpected site launch"));
    Check(reopenedSites.Find("갈피").Single().Url == "https://example.org/custom-galpi", "existing Moa site override survives initialization");
    var weather = WeatherClockRange.Parse("모아야, 서울 오후 3시 날씨 알려줘", false);
    Check(weather.Matched && weather.Error is null && weather.Region == "서울" && weather.Range?.StartHour == 15, "Moa address is excluded from the weather location");
    Reject(() => { var o = Options(root); o.ModelFile = Path.Combine(root, "copy.gguf"); o.Validate(); }, "unapproved model path rejected");
    Reject(() => { var o = Options(root); o.RuntimeFile = "runtime/fake.exe"; o.Validate(); }, "unapproved runtime rejected");
    Reject(() => { var o = Options(root); o.DataDirectory = "../../../../volti-pet/desktop/engine-data"; o.Validate(); }, "data path escape rejected");
    Reject(() => { var o = Options(root); o.ConversationVaultDirectory = Path.GetTempPath(); o.Validate(); }, "outside vault rejected");
    Reject(() => { var o = Options(root); o.Port = 18471; o.Validate(); }, "Volti API port rejected");
    Reject(() => { var o = Options(root); o.BackendPort = 18474; o.Validate(); }, "Volti backend port rejected");
    Reject(() => { var o = Options(root); o.LoraFile = "adapter.gguf"; o.Validate(); }, "LoRA disabled");

    // Owned fixture executables simulate conflict without starting/stopping Volti.
    string fixtureDir = Path.Combine(SharedResources.MoaRoot, "tests", "fixtures");
    Process Spawn(string name) => Process.Start(new ProcessStartInfo(Path.Combine(fixtureDir, name + ".exe")) { UseShellExecute = false, CreateNoWindow = true })!;
    if (!CpuModelLease.HasCompetitor())
    {
        using (var lease = new CpuModelLease())
        {
            bool blocked = false; try { using var second = new CpuModelLease(); } catch (ApiFailure) { blocked = true; }
            Check(blocked, "second Moa CPU lease denied");
        }
        using (var competitor = Spawn("VoltiEngine"))
        {
            try
            {
                bool blocked = false; try { using var lease = new CpuModelLease(); } catch (ApiFailure e) { blocked = e.Code == "cpu_model_busy"; }
                Check(blocked, "Volti-named fixture blocks startup"); Check(!competitor.HasExited, "competitor not killed on denial");
            }
            finally { competitor.Kill(); competitor.WaitForExit(); }
        }
        using (var lease = new CpuModelLease())
        using (var owned = Spawn("OwnedModel"))
        {
            lease.Attach(owned);
            using var competitor = Spawn("VoltiEngine");
            try
            {
                Check(owned.WaitForExit(5000) && lease.Conflict, "late competitor stops only owned model");
                Check(!competitor.HasExited, "late competitor left running");
            }
            finally { if (!competitor.HasExited) { competitor.Kill(); competitor.WaitForExit(); } }
        }
    }
    else { results.Add("SKIP lease fixtures: real competitor already present"); Console.WriteLine(results[^1]); }

    var fakeRuntime = new FakeRuntime();
    await using (var app = EngineHost.Build(options, fakeRuntime))
    {
        await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{options.Port}"), Timeout = TimeSpan.FromSeconds(15) };
        var status = await client.GetFromJsonAsync<JsonElement>("/v1/status");
        Check(status.GetProperty("service").GetString() == "moa-engine", "Moa API identity");
        Check((await client.PostAsJsonAsync("/v1/sessions", new { })).StatusCode == HttpStatusCode.Unauthorized, "sessions require own token");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(Path.Combine(options.DataPath, "native-token.txt")).Trim());
        var session = await (await client.PostAsJsonAsync("/v1/sessions", new { })).Content.ReadFromJsonAsync<JsonElement>();
        string id = session.GetProperty("id").GetString()!;
        var reply = await client.PostAsJsonAsync("/v1/chat", new { sessionId = id, requestId = "contract-1", text = "안녕 모아" });
        string sse = await reply.Content.ReadAsStringAsync();
        Check(sse.Contains("event: done") && !sse.Contains("event: error"), "authenticated SSE completes");
        Check(Directory.GetFiles(options.ConversationVaultDirectory!, "*.md").Length == 1, "raw conversation saved in private vault");
        var archive = new ConversationArchive(options.DataPath, options.ConversationVaultDirectory);
        Check(archive.Read(id, "native").Length == 2, "conversation restored from disk");
        await (await client.PostAsJsonAsync("/v1/chat", new { sessionId = id, requestId = "tone-1", text = "반말로 한 문장만 답해 줘. 질문하지 마." })).Content.ReadAsStringAsync();
        var activeRules = ConversationRules.Read(fakeRuntime.LastHistory, "");
        Check(activeRules.Tone == "casual" && activeRules.SentenceLimit == 1 && activeRules.NoQuestions, "tone length and no-question rules update with tools disabled");
        var restoredRules = archive.Find(id, "native")!.Rules;
        Check(restoredRules.Tone == "casual" && restoredRules.NoQuestions, "dialogue preferences persist separately from tools");
        var response = await client.DeleteAsync("/v1/sessions/" + id);
        Check(response.IsSuccessStatusCode, "own session deletion succeeds");
        Check((await client.GetAsync("/v1/sessions/" + id)).StatusCode == HttpStatusCode.NotFound, "deleted session unavailable");
        await app.StopAsync();
    }
    if (args.Contains("--real"))
    {
        Check(!CpuModelLease.HasCompetitor(), "real inference preflight has no competing model");
        var real = Options(Path.Combine(root, "real")); Directory.CreateDirectory(real.RootDirectory);
        await using var app = EngineHost.Build(real); await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{real.Port}"), Timeout = TimeSpan.FromSeconds(200) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(Path.Combine(real.DataPath, "native-token.txt")).Trim());
        string id = (await (await client.PostAsJsonAsync("/v1/sessions", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var timer = Stopwatch.StartNew();
        string body = await (await client.PostAsJsonAsync("/v1/chat", new { sessionId = id, requestId = "real-1", text = "너의 이름은 뭐야? 한 문장으로 답해." })).Content.ReadAsStringAsync();
        File.WriteAllText(Path.Combine(root, "real-response.sse"), body);
        Check(body.Contains("event: done") && !body.Contains("event: error"), "real CPU model completed answer");
        Console.WriteLine("Real completion seconds: " + timer.Elapsed.TotalSeconds.ToString("F2"));
        var conversation = new ConversationArchive(real.DataPath, real.ConversationVaultDirectory).Read(id, "native");
        Check(conversation.Length == 2 && conversation[1].Text.Contains("모아"), "real answer identifies Moa and persists");
        Check(!CpuModelLease.HasCompetitor(), "model server unloaded after reply");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat") { Content = JsonContent.Create(new { sessionId = id, requestId = "real-cancel", text = "우주를 주제로 긴 이야기를 해 줘." }) })
        using (var stream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            await Task.Delay(1000);
            var cancelled = await client.PostAsJsonAsync("/v1/requests/real-cancel/cancel", new { });
            Check(cancelled.IsSuccessStatusCode, "real model cancellation accepted");
            string cancelledBody = await stream.Content.ReadAsStringAsync();
            Check(!cancelledBody.Contains("event: done"), "cancelled real turn does not complete");
            Check(new ConversationArchive(real.DataPath, real.ConversationVaultDirectory).Read(id, "native").Length == 2, "cancelled real turn not stored");
            Check(!CpuModelLease.HasCompetitor(), "model server unloaded after cancellation");
        }
        await app.StopAsync();
    }
}
finally { File.WriteAllLines(Path.Combine(root, "results.txt"), results); Console.WriteLine("Artifacts: " + root); }

sealed class FakeRuntime : IInferenceRuntime
{
    public ChatMessage[] LastHistory { get; private set; } = [];
    public string State => "ready"; public string? Error => null;
    public async IAsyncEnumerable<string> GenerateAsync(ChatMessage[] history, string text, [EnumeratorCancellation] CancellationToken token)
    { LastHistory = history; await Task.Delay(text.Contains("우주") ? 5000 : 20, token); yield return "반가워요. 모아예요."; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
