using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// A session client. The independent engine owns conversation memory and model
// settings; this process retains only its own session ID.
public static class MoaLocalChat
{
    private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
    private static string sessionId;
    private static string SessionFile
    {
        get
        {
            string client = Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
            client = System.Text.RegularExpressions.Regex.Replace(client, "[^A-Za-z0-9_-]", "");
            return Path.Combine(MoaLocalRuntime.DataDirectory, "client-" + client + "-conversation.txt");
        }
    }
    private static void RestoreSessionId()
    {
        if (sessionId != null || !File.Exists(SessionFile)) return;
        string saved = File.ReadAllText(SessionFile).Trim();
        Guid parsed;
        if (Guid.TryParseExact(saved, "N", out parsed)) sessionId = saved;
    }
    private static void SaveSessionId()
    {
        if (sessionId == null) { if (File.Exists(SessionFile)) File.Delete(SessionFile); return; }
        Directory.CreateDirectory(MoaLocalRuntime.DataDirectory);
        File.WriteAllText(SessionFile, sessionId, new UTF8Encoding(false));
    }
    public static int LastDialogueExampleCount { get; private set; }
    public static bool LastLoraApplied { get; private set; }
    public static bool IsClearCommand(string text)
    {
        string command = text.Trim().Replace(" ", "").TrimEnd('.', '!', '?');
        return command == "대화기록지워줘" || command == "대화기록삭제해줘" || command == "/대화초기화";
    }

    public static async Task<MoaChatMessage[]> GetHistoryAsync(CancellationToken token)
    {
        await RequestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RestoreSessionId();
            if (sessionId == null) return new MoaChatMessage[0];
            await MoaLocalRuntime.EnsureStartedAsync(token).ConfigureAwait(false);
            return await Task.Run(() => LoadHistory(token), token).ConfigureAwait(false);
        }
        finally { RequestGate.Release(); }
    }

    public static async Task ClearHistoryAsync(CancellationToken token)
    {
        await RequestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RestoreSessionId();
            if (sessionId == null) return;
            await MoaLocalRuntime.EnsureStartedAsync(token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                try { JsonRequest("DELETE", "/v1/sessions/" + Uri.EscapeDataString(sessionId), null, token); }
                catch (EngineHttpException error) { if (error.Status != 404) throw; }
                sessionId = null;
                SaveSessionId();
            }, token).ConfigureAwait(false);
        }
        finally { RequestGate.Release(); }
    }

    public static async Task<string> ReplyAsync(string text, IProgress<string> progress, CancellationToken cancellationToken)
    {
        LastDialogueExampleCount = 0;
        LastLoraApplied = false;
        if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("메시지를 입력해 주세요.");
        if (text.Length > 1000) throw new ArgumentException("한 번에 1,000자 이내로 보내 주세요.");
        if (IsClearCommand(text))
        {
            await ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
            return "이 대화 기록을 지웠어요. Obsidian 노트의 자동 기록 구간도 비웠고, 따로 저장한 기억과 직접 적은 메모는 유지했어요.";
        }
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(180000);
                try
                {
                    await MoaLocalRuntime.EnsureStartedAsync(timeout.Token).ConfigureAwait(false);
                    return await Task.Run(() =>
                    {
                        // Detect an engine restart before starting the new request.
                        RestoreSessionId();
                        if (sessionId != null) LoadHistory(timeout.Token);
                        if (sessionId == null)
                        {
                            Dictionary<string, object> created = JsonRequest("POST", "/v1/sessions", new { }, timeout.Token);
                            sessionId = StringValue(created, "id");
                            if (String.IsNullOrWhiteSpace(sessionId)) throw new InvalidOperationException("엔진이 대화 ID를 보내지 않았어요.");
                            SaveSessionId();
                        }
                        return StreamReply(text, progress, timeout.Token);
                    }, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new InvalidOperationException("답변을 기다리는 시간이 길어졌어요. 잠시 후 더 짧게 다시 말 걸어 주세요.");
                }
            }
        }
        finally { RequestGate.Release(); }
    }

    private static MoaChatMessage[] LoadHistory(CancellationToken token)
    {
        Dictionary<string, object> data;
        try { data = JsonRequest("GET", "/v1/sessions/" + Uri.EscapeDataString(sessionId), null, token); }
        catch (EngineHttpException error)
        {
            if (error.Status != 404) throw;
            sessionId = null;
            SaveSessionId();
            return new MoaChatMessage[0];
        }
        object value;
        object[] messages = data.TryGetValue("messages", out value) ? value as object[] : null;
        if (messages == null) throw new InvalidOperationException("대화 기억을 읽지 못했어요.");
        List<MoaChatMessage> result = new List<MoaChatMessage>();
        foreach (object item in messages)
        {
            Dictionary<string, object> message = item as Dictionary<string, object>;
            if (message == null) continue;
            string role = StringValue(message, "role");
            if (role == "user" || role == "assistant") result.Add(new MoaChatMessage(role, StringValue(message, "text") ?? ""));
        }
        return result.ToArray();
    }

    private static string StreamReply(string text, IProgress<string> progress, CancellationToken token)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string nativeToken = MoaLocalRuntime.ReadNativeToken();
        HttpWebRequest request = CreateRequest("POST", "/v1/chat", nativeToken, 180000);
        request.Accept = "text/event-stream";
        byte[] body = Encoding.UTF8.GetBytes(Serializer().Serialize(new { sessionId = sessionId, text = text, requestId = requestId }));
        request.ContentLength = body.Length;
        using (token.Register(delegate { request.Abort(); }))
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using (Stream outgoing = request.GetRequestStream()) outgoing.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    StringBuilder total = new StringBuilder();
                    StringBuilder eventData = new StringBuilder();
                    string eventName = "message";
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line.Substring(6).Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            if (eventData.Length > 0) eventData.Append('\n');
                            eventData.Append(line.Substring(5).TrimStart(' '));
                            if (eventData.Length > 65536) throw new InvalidOperationException("엔진 응답이 너무 커서 중단했어요.");
                        }
                        else if (line.Length == 0)
                        {
                            if (eventData.Length > 0)
                            {
                                Dictionary<string, object> packet = Serializer().DeserializeObject(eventData.ToString()) as Dictionary<string, object>;
                                if (packet == null) throw new InvalidOperationException("엔진 응답 형식이 올바르지 않아요.");
                                if (eventName == "error") throw new InvalidOperationException(StringValue(packet, "message") ?? "엔진이 답변을 완료하지 못했어요.");
                                if (eventName == "delta")
                                {
                                    string chunk = StringValue(packet, "text") ?? "";
                                    total.Append(chunk);
                                    if (total.Length > 12000) throw new InvalidOperationException("답변이 너무 길어 중단했어요.");
                                    if (progress != null && chunk.Length > 0) progress.Report(chunk);
                                }
                                if (eventName == "done")
                                {
                                    token.ThrowIfCancellationRequested();
                                    string answer = StringValue(packet, "text");
                                    if (String.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("엔진이 빈 답변을 보냈어요.");
                                    if (total.Length > 0 && answer.Trim() != total.ToString().Trim()) throw new InvalidOperationException("엔진의 최종 답변과 표시한 내용이 일치하지 않아요.");
                                    object model;
                                    if (packet.TryGetValue("model", out model) && model is Dictionary<string, object>)
                                    {
                                        object applied;
                                        if (((Dictionary<string, object>)model).TryGetValue("loraApplied", out applied) && applied is bool) LastLoraApplied = (bool)applied;
                                    }
                                    object assistance;
                                    if (packet.TryGetValue("assistance", out assistance) && assistance is Dictionary<string, object>)
                                    {
                                        Dictionary<string, object> info = (Dictionary<string, object>)assistance;
                                        object count;
                                        if (StringValue(info, "mode") == "local_dialogue_examples" && info.TryGetValue("count", out count) && Convert.ToInt32(count) == 1) LastDialogueExampleCount = 1;
                                    }
                                    return answer.Trim();
                                }
                            }
                            eventName = "message";
                            eventData.Length = 0;
                        }
                    }
                    throw new InvalidOperationException("엔진 연결이 답변 도중에 끊겼어요. 다시 말 걸어 주세요.");
                }
            }
            catch (WebException error) { token.ThrowIfCancellationRequested(); throw Translate(error); }
            catch (IOException) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("엔진 연결이 끊겼어요. 다시 말 걸어 주세요."); }
            catch (Exception) { token.ThrowIfCancellationRequested(); throw; }
            finally
            {
                if (token.IsCancellationRequested)
                {
                    // The engine also cancels on disconnect. This explicit fallback
                    // uses a separate, short request after the SSE request aborts.
                    try { JsonRequest("POST", "/v1/requests/" + requestId + "/cancel", new { }, CancellationToken.None, nativeToken, 1500); }
                    catch { }
                }
            }
        }
    }

    private static Dictionary<string, object> JsonRequest(string method, string path, object data, CancellationToken token)
    {
        return JsonRequest(method, path, data, token, MoaLocalRuntime.ReadNativeToken(), 10000);
    }

    private static Dictionary<string, object> JsonRequest(string method, string path, object data, CancellationToken token, string nativeToken, int timeout)
    {
        HttpWebRequest request = CreateRequest(method, path, nativeToken, timeout);
        using (token.Register(delegate { request.Abort(); }))
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (data != null)
                {
                    byte[] body = Encoding.UTF8.GetBytes(Serializer().Serialize(data));
                    request.ContentLength = body.Length;
                    using (Stream outgoing = request.GetRequestStream()) outgoing.Write(body, 0, body.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    token.ThrowIfCancellationRequested();
                    if (body.Length == 0) return new Dictionary<string, object>();
                    Dictionary<string, object> result = Serializer().DeserializeObject(body) as Dictionary<string, object>;
                    if (result == null) throw new InvalidOperationException("엔진 응답 형식이 올바르지 않아요.");
                    return result;
                }
            }
            catch (WebException error) { token.ThrowIfCancellationRequested(); throw Translate(error); }
            catch (IOException) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("엔진 연결이 끊겼어요."); }
            catch (Exception) { token.ThrowIfCancellationRequested(); throw; }
        }
    }

    private static HttpWebRequest CreateRequest(string method, string path, string token, int timeout)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(MoaLocalRuntime.BaseUrl + path);
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.Method = method;
        request.ContentType = "application/json; charset=utf-8";
        request.Accept = "application/json";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        request.KeepAlive = false;
        return request;
    }

    private static Exception Translate(WebException error)
    {
        HttpWebResponse response = error.Response as HttpWebResponse;
        if (response == null) return new InvalidOperationException("독립 엔진에 연결하지 못했어요. 엔진 관리 창에서 상태를 확인해 주세요.", error);
        using (response)
        {
            string message = "엔진이 요청을 처리하지 못했어요.";
            try
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    Dictionary<string, object> packet = Serializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                    if (packet != null)
                    {
                        object nested;
                        Dictionary<string, object> details = packet.TryGetValue("error", out nested) ? nested as Dictionary<string, object> : packet;
                        if (details != null) message = StringValue(details, "message") ?? message;
                    }
                }
            }
            catch { }
            return new EngineHttpException((int)response.StatusCode, message);
        }
    }

    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 262144 }; }
    private static string StringValue(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) ? value as string : null; }
    private sealed class EngineHttpException : InvalidOperationException
    {
        public int Status { get; private set; }
        public EngineHttpException(int status, string message) : base(message) { Status = status; }
    }
}

public sealed class MoaChatMessage
{
    public string Role { get; private set; }
    public string Text { get; private set; }
    public MoaChatMessage(string role, string text) { Role = role; Text = text; }
}
