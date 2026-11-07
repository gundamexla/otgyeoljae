using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoltiEngine;

public interface IInferenceRuntime : IAsyncDisposable
{
    string State { get; }
    string? Error { get; }
    IAsyncEnumerable<string> GenerateAsync(ChatMessage[] history, string text, CancellationToken cancellationToken);
}

public sealed record InferenceTrace(string Stage, int Attempt, string Text, string[] Issues);

// The optional observer is used only by local quality tests. Production never logs raw replies.
public sealed class ModelRuntime(EngineOptions options, Action<InferenceTrace>? observe = null) : IInferenceRuntime
{
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private Process? server;
    private CpuModelLease? cpuLease;
    private string backendKey = "";
    private volatile string state = "idle";
    private string? error;
    private volatile bool stopped;
    public string State => state == "ready" && (server is null || server.HasExited) ? "error" : state;
    public string? Error => State == "error" ? error ?? "모델 실행기가 종료됐어요. 다시 메시지를 보내면 재시작해요." : null;
    private string Endpoint(string path) => $"http://127.0.0.1:{options.BackendPort}{path}";

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        await startupGate.WaitAsync(token);
        try
        {
            if (stopped) throw new OperationCanceledException("엔진을 종료하고 있어요.");
            if (server is null || server.HasExited)
            {
                server?.Dispose(); server = null;
                if (!File.Exists(options.RuntimePath) || !File.Exists(options.ModelPath))
                    throw new ApiFailure(503, "model_missing", "모델 또는 CPU 실행기 파일이 없어요. 설치 폴더의 setup-local.ps1로 준비해 주세요.");
                if (options.LoraPath is { } adapter && !File.Exists(adapter))
                    throw new ApiFailure(503, "adapter_missing", "추가 학습 파일이 없어요. 설정을 확인해 주세요.");
                // Never attach to or stop an unrelated service using this port.
                using (var probe = new TcpClient())
                {
                    try
                    {
                        await probe.ConnectAsync(IPAddress.Loopback, options.BackendPort, token);
                        throw new ApiFailure(503, "backend_port_in_use", "모델 연결 포트를 다른 프로그램이 사용하고 있어요. 엔진 설정의 backendPort를 확인해 주세요.");
                    }
                    catch (SocketException) { }
                }
                cpuLease = new CpuModelLease();
                backendKey = AuthStore.NewToken();
                var start = new ProcessStartInfo(options.RuntimePath)
                {
                    WorkingDirectory = options.RootDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                string[] arguments = ["--model", options.ModelPath, "--alias", options.ModelAlias,
                    "--host", "127.0.0.1", "--port", options.BackendPort.ToString(), "--offline",
                    "--device", "none", "--gpu-layers", "0", "--no-op-offload", "--fit", "off",
                    "--threads", options.Threads.ToString(), "--threads-batch", options.Threads.ToString(), "--threads-http", "2",
                    "--ctx-size", options.ContextTokens.ToString(), "--parallel", "1", "--cache-ram", "0",
                    "--no-webui", "--no-ui-mcp-proxy", "--no-agent", "--no-slots",
                    "--cors-origins", $"http://127.0.0.1:{options.BackendPort}", "--no-cors-credentials",
                    "--reasoning", "off", "--reasoning-budget", "0", "--log-disable"];
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                if (options.LoraPath is { } loraPath)
                {
                    start.ArgumentList.Add("--lora");
                    start.ArgumentList.Add(loraPath);
                }
                start.Environment["HF_HUB_OFFLINE"] = "1";
                start.Environment["HF_DATASETS_OFFLINE"] = "1";
                start.Environment["LLAMA_ARG_OFFLINE"] = "1";
                start.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
                // API-key support verified against the bundled b11960 --help.
                // Keep this secret off process arguments and diagnostic output.
                start.Environment["LLAMA_API_KEY"] = backendKey;
                var launched = new Process { StartInfo = start };
                launched.OutputDataReceived += (_, _) => { };
                launched.ErrorDataReceived += (_, _) => { };
                launched.Start(); server = launched;
                cpuLease.Attach(launched);
                launched.BeginOutputReadLine(); launched.BeginErrorReadLine();
                try { launched.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                state = "loading"; error = null;
            }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(90))
            {
                token.ThrowIfCancellationRequested();
                if (cpuLease?.Conflict == true) throw new ApiFailure(409, "cpu_model_busy", CpuModelLease.BusyMessage);
                if (server.HasExited) throw new ApiFailure(503, "model_start_failed", "CPU 모델 실행기를 시작하지 못했어요. 모델과 실행기 파일을 확인해 주세요.");
                using var probe = new HttpRequestMessage(HttpMethod.Get, Endpoint("/v1/models"));
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backendKey);
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using var response = await client.SendAsync(probe, probeTimeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(probeTimeout.Token));
                        if (!data.RootElement.TryGetProperty("data", out var models) || !models.EnumerateArray().Any(m => m.TryGetProperty("id", out var id) && id.GetString() == options.ModelAlias))
                            throw new ApiFailure(503, "model_mismatch", "설정한 모델과 실행 중인 모델이 달라요.");
                        state = "ready"; error = null; return;
                    }
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                        throw new ApiFailure(503, "backend_auth_failed", "CPU 모델 연결을 인증하지 못했어요. 엔진을 다시 시작해 주세요.");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(250, token);
            }
            throw new ApiFailure(503, "model_start_timeout", "CPU 모델 준비 시간이 너무 길어요. 다른 작업을 줄인 뒤 다시 시도해 주세요.");
        }
        catch (ApiFailure failure) { state = "error"; error = failure.Message; throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception or JsonException)
        {
            state = "error"; error = "CPU 모델 실행기를 시작하지 못했어요. 설치 파일을 확인해 주세요.";
            throw new ApiFailure(503, "model_start_failed", error);
        }
        finally { startupGate.Release(); }
    }

    public async IAsyncEnumerable<string> GenerateAsync(ChatMessage[] history, string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Buffer before display so rejected text cannot flash in the UI or enter history.
        string[] issues = [];
        try
        {
            var answer = await ReplyQuality.GenerateAsync(text,
                (attempt, token) => ObserveRawAsync(history, text, attempt, issues, token), cancellationToken, history, reasons => { issues = reasons; observe?.Invoke(new("rejected", -1, "", reasons)); });
            if (cpuLease?.Conflict == true) throw new ApiFailure(409, "cpu_model_busy", CpuModelLease.BusyMessage);
            var display = KoreanTone.Apply(text, answer, ConversationRules.Read(history, text));
            observe?.Invoke(new("accepted_raw", -1, answer, []));
            observe?.Invoke(new("display", -1, display, []));
            yield return display;
        }
        finally
        {
            // Free CPU/model memory after each completed, failed, or cancelled turn.
            await startupGate.WaitAsync();
            try
            {
                if (cpuLease?.Conflict == true) { state = "error"; error = CpuModelLease.BusyMessage; }
                cpuLease?.Dispose(); cpuLease = null; server?.Dispose(); server = null; if (state != "error") state = "idle";
            }
            finally { startupGate.Release(); }
        }
    }

    private async IAsyncEnumerable<string> ObserveRawAsync(ChatMessage[] history, string text, int attempt, string[] issues, [EnumeratorCancellation] CancellationToken token)
    {
        var raw = new StringBuilder();
        try
        {
            await foreach (var chunk in GenerateRawAsync(history, text, attempt, issues, token)) { raw.Append(chunk); yield return chunk; }
        }
        finally { observe?.Invoke(new("raw", attempt, raw.ToString(), [])); }
    }

    private async IAsyncEnumerable<string> GenerateRawAsync(ChatMessage[] history, string text, int attempt, string[] issues, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        // Qwen's bundled chat template accepts a single leading system message.
        // Memory context is server-created; ordinary session history has user/assistant roles.
        var system = options.SystemPrompt + string.Concat(history.Where(m => m.Role == "system" && !m.Text.StartsWith(ConversationRules.Marker + "\n")).Select(m => "\n\n" + ConversationRules.ForModel(m.Text)));
        system += "\n일상 대화는 쉬운 한국어로 짧게 답하세요. 사용자가 요청하지 않은 중국어·일본어를 섞지 마세요. 필요한 영문 기술 용어와 고유명사는 유지해도 됩니다.";
        system += "\n" + KoreanDialogueReview.Guidance;
        var rules = ConversationRules.Read(history, text);
        system += DialogueExamples.For(text, rules);
        system += "\n현재 지킬 조건:\n" + ConversationRules.ForModel(rules.Prompt());
        system += MoaLanguageGuidance.For(text, rules);
        if (ReplyQuality.WantsNoQuestions(text, history))
            system += "\n사용자는 추가 질문을 원하지 않습니다. 사용자의 말을 내 경험처럼 되풀이하지 말고, 지금 말에 짧게 반응한 뒤 끝내세요.";
        if (attempt > 0) system += "\n답변 검수 후 다시 작성합니다. " + KoreanDialogueReview.Repair(issues);
        var messages = new List<object> { new { role = "system", content = system } };
        messages.AddRange(history.Where(m => m.Role != "system").Select(m => (object)new { role = m.Role, content = m.Text }));
        messages.Add(new { role = "user", content = MoaLanguageGuidance.ContentRequest(text) });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/v1/chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backendKey);
        request.Headers.Accept.Add(new("text/event-stream"));
        request.Content = JsonContent.Create(new
        {
            model = options.ModelAlias, messages, stream = true, max_tokens = options.MaxReplyTokens,
            // Qwen3.5's official non-thinking defaults; configurable for measured comparisons.
            temperature = options.Temperature, top_p = options.TopP, top_k = 20, min_p = 0, presence_penalty = options.PresencePenalty,
            repeat_penalty = 1.0, cache_prompt = true, chat_template_kwargs = new { enable_thinking = false }
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ApiFailure(503, "model_request_failed", "CPU 모델이 요청을 처리하지 못했어요. 잠시 후 다시 보내 주세요.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var completed = false;
        var length = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { completed = true; break; }
            if (data.Length == 0) continue;
            using var packet = JsonDocument.Parse(data);
            if (packet.RootElement.TryGetProperty("error", out _))
                throw new ApiFailure(503, "model_stream_failed", "모델이 답변 생성을 완료하지 못했어요.");
            if (!packet.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
                throw new ApiFailure(502, "reply_truncated", "답변이 길어 중간에 끊겼어요. 더 짧게 질문해 주세요. 잘린 답변은 저장하지 않았어요.");
            if (!choice.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
            var chunk = content.GetString();
            if (string.IsNullOrEmpty(chunk)) continue;
            length += chunk.Length;
            if (length > 12000) throw new ApiFailure(502, "reply_too_long", "답변이 너무 길어 생성을 멈췄어요.");
            yield return chunk;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!completed) throw new ApiFailure(502, "incomplete_reply", "모델 연결이 중간에 끊겼어요. 이 답변은 기억에 저장하지 않았어요.");
        if (length == 0) throw new ApiFailure(502, "empty_reply", "모델이 빈 답변을 보냈어요. 다시 말 걸어 주세요.");
    }

    public async ValueTask DisposeAsync()
    {
        stopped = true;
        await startupGate.WaitAsync();
        try
        {
            cpuLease?.Dispose(); cpuLease = null;
            if (server is not null)
            {
                try { if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); } }
                catch (InvalidOperationException) { }
                finally { server.Dispose(); server = null; }
            }
            client.Dispose();
        }
        finally { startupGate.Release(); }
    }
}
