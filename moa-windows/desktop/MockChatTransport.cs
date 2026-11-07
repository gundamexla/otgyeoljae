using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Moa
{
    public enum DemoScenario { Normal, Choice, Error, Disconnect, Slow }

    // Line-oriented SSE framing, including comments, CRLF, and multiple data lines.
    // This milestone is in-memory only: no sockets, model, token or persistence.
    public sealed class SseReader
    {
        private string kind = "message";
        private readonly StringBuilder data = new StringBuilder();
        private readonly Action<string, string> emit;
        public SseReader(Action<string, string> emit) { this.emit = emit; }
        public void Line(string line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) emit(kind, data.ToString().TrimEnd('\n'));
                kind = "message"; data.Length = 0; return;
            }
            if (line.StartsWith(":")) return;
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            string value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.StartsWith(" ")) value = value.Substring(1);
            if (field == "event") kind = value;
            if (field == "data") data.Append(value).Append('\n');
        }
    }

    public sealed class MockChatTransport
    {
        private readonly int tick;
        public MockChatTransport(int tickMilliseconds) { tick = tickMilliseconds; }
        public async Task RunAsync(string id, DemoScenario scenario, string choice,
            Action<string, string, string> receive, CancellationToken token)
        {
            var json = new JavaScriptSerializer();
            var reader = new SseReader(delegate(string kind, string data)
            {
                var obj = json.Deserialize<Dictionary<string, object>>(data);
                string key = kind == "error" ? "message" : "text";
                object value;
                receive(id, kind, obj.TryGetValue(key, out value) ? Convert.ToString(value) : "");
            });
            Action<string, string> send = delegate(string kind, string value)
            {
                token.ThrowIfCancellationRequested();
                string body = kind == "error" ? json.Serialize(new { message = value }) : json.Serialize(new { text = value });
                using (var lines = new StringReader("event: " + kind + "\r\ndata: " + body + "\r\n\r\n"))
                { string line; while ((line = lines.ReadLine()) != null) reader.Line(line); }
            };
            send("queued", "");
            await Task.Delay(tick * (scenario == DemoScenario.Slow ? 8 : 2), token);
            string answer = scenario == DemoScenario.Choice
                ? (choice == "짧은 인사" ? "반가워요! 모아예요." : "오늘도 한 걸음씩, 같이 해봐요.")
                : "반가워요! 모아의 첫 창에 왔어요. 지금은 움직임과 채팅 흐름을 확인하는 모의 답변이에요.";
            int sent = 0;
            while (sent < answer.Length)
            {
                int length = Math.Min(7, answer.Length - sent);
                send("delta", answer.Substring(sent, length)); sent += length;
                await Task.Delay(tick, token);
                if (sent >= 14 && scenario == DemoScenario.Error)
                { send("error", "모의 응답 중 문제가 생겼어요. 다시 시도할 수 있어요."); return; }
                if (sent >= 14 && scenario == DemoScenario.Disconnect) return;
            }
            send("done", answer);
        }
    }
}
