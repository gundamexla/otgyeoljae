using Moa;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// One-way status updates only. This is not a command executor or network server.
public sealed class MoaControlServer : IDisposable
{
    private readonly MoaPetForm owner;
    private readonly Dictionary<string, long> sequences = new Dictionary<string, long>();
    private readonly object gate = new object();
    private NamedPipeServerStream pipe;
    private volatile bool stopped;
    public static string PipeName { get { return "MoaPet-" + WindowsIdentity.GetCurrent().User.Value; } }
    public MoaControlServer(MoaPetForm pet) { owner = pet; }
    public void Start() { var thread = new Thread(Listen); thread.IsBackground = true; thread.Name = "Moa status bridge"; thread.Start(); }

    private void Listen()
    {
        while (!stopped)
        {
            try
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                using (var current = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 8192, 8192, security))
                {
                    lock (gate) { if (stopped) return; pipe = current; }
                    current.WaitForConnection();
                    using (var reader = new StreamReader(current, new UTF8Encoding(false, true), false, 1024, true))
                    {
                        var builder = new StringBuilder();
                        while (!stopped && builder.Length <= 4096)
                        {
                            int c = reader.Read();
                            if (c < 0 || c == '\n') break;
                            builder.Append((char)c);
                        }
                        ControlPayload payload;
                        if (builder.Length <= 4096 && TryParse(builder.ToString(), out payload))
                        {
                            string task = payload.TaskId ?? "";
                            long previous;
                            if (payload.Sequence.HasValue && sequences.TryGetValue(task, out previous) && payload.Sequence.Value <= previous) continue;
                            if (payload.Sequence.HasValue)
                            {
                                if (sequences.Count >= 128 && !sequences.ContainsKey(task)) sequences.Clear();
                                sequences[task] = payload.Sequence.Value;
                            }
                            if (!owner.IsDisposed && owner.IsHandleCreated)
                            {
                                owner.BeginInvoke(new Action(delegate { if (!owner.IsDisposed) owner.ApplyState(payload.State, payload.Message, "agent"); }));
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { if (stopped) return; }
            catch (IOException) { if (stopped) return; Thread.Sleep(100); }
            catch (Exception) { if (stopped) return; Thread.Sleep(250); }
            finally { lock (gate) { pipe = null; } }
        }
    }

    public static bool TryParse(string json, out ControlPayload value)
    {
        value = null;
        try
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 8 };
            var obj = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null || !obj.ContainsKey("state") || !(obj["state"] is string)) return false;
            string state = (string)obj["state"];
            if (Array.IndexOf(new[] { "idle", "called", "working", "attention", "done", "error" }, state) < 0) return false;
            var parsed = new ControlPayload { State = state, Message = "" };
            object text;
            if (obj.TryGetValue("message", out text)) { if (!(text is string) || ((string)text).Length > 1000) return false; parsed.Message = (string)text; }
            if (obj.TryGetValue("taskId", out text)) { if (!(text is string) || ((string)text).Length > 160) return false; parsed.TaskId = (string)text; }
            if (obj.TryGetValue("sequence", out text))
            {
                if (!(text is int) && !(text is long)) return false;
                long sequence = Convert.ToInt64(text);
                if (sequence < 0) return false;
                parsed.Sequence = sequence;
            }
            value = parsed;
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        stopped = true;
        lock (gate) { if (pipe != null) { try { pipe.Dispose(); } catch { } pipe = null; } }
    }
}
public sealed class ControlPayload
{
    public string State;
    public string Message;
    public string TaskId;
    public long? Sequence;
}
