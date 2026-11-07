using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// The manager is a local native client. Credentials never enter a web page.
internal static class MoaEngineControlProgram
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MoaEngineControlForm());
    }
}

internal sealed class MoaEngineControlForm : Form
{
    private readonly Label status = new Label();
    private readonly Label notice = new Label();
    private readonly Button start = new Button();
    private readonly Button stop = new Button();
    private readonly TextBox site = new TextBox();
    private readonly TextBox code = new TextBox();
    private readonly ListBox connections = new ListBox();
    private readonly RichTextBox transcript = new RichTextBox();
    private readonly TextBox message = new TextBox();
    private readonly Button send = new Button();
    private readonly Button clear = new Button();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private CancellationTokenSource activeChat;
    private bool refreshing;

    public MoaEngineControlForm()
    {
        Text = "모아 AI 엔진";
        AccessibleName = Text;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10);
        BackColor = Color.FromArgb(248, 246, 240);
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(640, 530);
        StartPosition = FormStartPosition.CenterScreen;

        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(layout);
        status.Dock = DockStyle.Fill;
        status.Font = new Font(Font.FontFamily, 13, FontStyle.Bold);
        status.Text = "AI 엔진을 확인하고 있어요";
        status.AccessibleName = "엔진 상태";
        layout.Controls.Add(status, 0, 0);
        FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        SetupButton(start, "엔진 시작", async delegate { await StartEngine(); });
        SetupButton(stop, "엔진 종료", async delegate { await StopEngine(); });
        actions.Controls.Add(start);
        actions.Controls.Add(stop);
        layout.Controls.Add(actions, 0, 1);

        TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        TabPage chat = new TabPage("대화");
        TabPage pairing = new TabPage("사이트 연결");
        tabs.TabPages.Add(chat);
        tabs.TabPages.Add(pairing);
        layout.Controls.Add(tabs, 0, 2);
        BuildChat(chat);
        BuildPairing(pairing);
        notice.Dock = DockStyle.Fill;
        notice.Text = "이 창을 닫아도 엔진은 계속 실행됩니다. 완전히 끄려면 ‘엔진 종료’를 누르세요.";
        notice.ForeColor = Color.FromArgb(100, 90, 80);
        notice.Padding = new Padding(0, 10, 0, 0);
        notice.AccessibleName = "엔진 안내";
        layout.Controls.Add(notice, 0, 3);
        timer.Interval = 3000;
        timer.Tick += async delegate { await RefreshStatus(); };
        Shown += async delegate {
            timer.Start(); message.ReadOnly = true; send.Enabled = false; clear.Enabled = false;
            await StartEngine();
            try {
                MoaChatMessage[] history = await MoaLocalChat.GetHistoryAsync(lifetime.Token);
                if (!IsDisposed && history.Length > 0) {
                    transcript.Clear();
                    foreach (MoaChatMessage item in history) transcript.AppendText((item.Role == "user" ? "나: " : "모아: ") + item.Text + "\n\n");
                    transcript.SelectionStart = transcript.TextLength; transcript.ScrollToCaret();
                }
            } catch (Exception error) { ShowError(error); }
            finally { if (!IsDisposed) { message.ReadOnly = false; send.Enabled = true; clear.Enabled = true; } }
        };
        FormClosing += delegate { lifetime.Cancel(); if (activeChat != null) activeChat.Cancel(); timer.Stop(); };
    }

    private void BuildChat(Control parent)
    {
        TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        parent.Controls.Add(panel);
        transcript.Dock = DockStyle.Fill;
        transcript.ReadOnly = true;
        transcript.BackColor = Color.White;
        transcript.DetectUrls = false;
        transcript.AccessibleName = "엔진과 나눈 대화";
        transcript.Text = "인터넷 없이 이 PC에서 대화합니다. /도구 로 화면 읽기·메모·기억 사용법을 확인하세요. 사이트와 모아의 대화는 각각 별도로 관리됩니다.\n\n";
        panel.Controls.Add(transcript, 0, 0);
        panel.SetColumnSpan(transcript, 2);
        message.Dock = DockStyle.Fill;
        message.MaxLength = 1000;
        message.AccessibleName = "엔진에게 보낼 메시지";
        message.Margin = new Padding(0, 9, 8, 0);
        message.KeyDown += async delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; if (activeChat == null) await SendMessage(); } };
        panel.Controls.Add(message, 0, 1);
        SetupButton(send, "보내기", async delegate { if (activeChat != null) activeChat.Cancel(); else await SendMessage(); });
        send.Dock = DockStyle.Fill;
        panel.Controls.Add(send, 1, 1);
        SetupButton(clear, "대화 지우기", async delegate {
            try { await MoaLocalChat.ClearHistoryAsync(lifetime.Token); if (!IsDisposed) { transcript.Clear(); notice.Text = "이 대화의 기억을 지웠어요."; } }
            catch (Exception error) { ShowError(error); }
        });
        panel.Controls.Add(clear, 0, 2);
    }

    private void BuildPairing(Control parent)
    {
        TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 6 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        parent.Controls.Add(panel);
        Label help = new Label { Dock = DockStyle.Fill, Text = "연결할 사이트 주소를 넣고 코드를 만드세요.\n사이트의 AI 연결 칸에 코드를 붙여넣으면 이 PC의 엔진과 연결됩니다." };
        panel.Controls.Add(help, 0, 0); panel.SetColumnSpan(help, 2);
        site.Dock = DockStyle.Fill;
        site.AccessibleName = "연결할 사이트 주소";
        panel.Controls.Add(site, 0, 1);
        Button issue = new Button();
        SetupButton(issue, "연결 코드 만들기", async delegate { await CreatePairing(); });
        issue.Dock = DockStyle.Fill;
        panel.Controls.Add(issue, 1, 1);
        code.Dock = DockStyle.Fill;
        code.ReadOnly = true;
        code.AccessibleName = "사이트 연결 코드";
        panel.Controls.Add(code, 0, 2);
        Button copy = new Button();
        SetupButton(copy, "코드 복사", delegate { if (!String.IsNullOrEmpty(code.Text)) { try { Clipboard.SetText(code.Text); notice.Text = "연결 코드를 복사했어요. 해당 사이트에 붙여넣으세요."; } catch { notice.Text = "코드를 선택해 직접 복사해 주세요."; } } });
        copy.Dock = DockStyle.Fill;
        panel.Controls.Add(copy, 1, 2);
        panel.Controls.Add(new Label { Text = "연결을 허용한 사이트", Dock = DockStyle.Fill }, 0, 3);
        connections.Dock = DockStyle.Fill;
        connections.AccessibleName = "연결된 사이트 목록";
        panel.Controls.Add(connections, 0, 4); panel.SetColumnSpan(connections, 2);
        Button refresh = new Button();
        SetupButton(refresh, "목록 새로고침", async delegate { await RefreshConnections(); });
        panel.Controls.Add(refresh, 0, 5);
        Button revoke = new Button();
        SetupButton(revoke, "선택 연결 해제", async delegate {
            ConnectionRow row = connections.SelectedItem as ConnectionRow;
            if (row == null) return;
            try { await EngineManagementApi.Call("DELETE", "/v1/connections/" + Uri.EscapeDataString(row.Id), null, true, lifetime.Token); if (!IsDisposed) notice.Text = "사이트 연결을 해제했어요. 다시 연결하려면 새 코드가 필요합니다."; await RefreshConnections(); }
            catch (Exception error) { ShowError(error); }
        });
        revoke.Dock = DockStyle.Fill;
        panel.Controls.Add(revoke, 1, 5);
    }

    private void SetupButton(Button button, string text, EventHandler action)
    {
        button.Text = text;
        button.AccessibleName = text;
        button.AutoSize = true;
        button.Height = 32;
        button.MinimumSize = new Size(88, 30);
        button.Click += action;
    }

    private async Task StartEngine()
    {
        start.Enabled = false;
        try { await MoaLocalRuntime.EnsureStartedAsync(lifetime.Token); await RefreshStatus(); await RefreshConnections(); }
        catch (Exception error) { ShowError(error); }
        finally { if (!IsDisposed) start.Enabled = true; }
    }

    private async Task StopEngine()
    {
        stop.Enabled = false;
        if (activeChat != null) activeChat.Cancel();
        try { await EngineManagementApi.Call("POST", "/v1/shutdown", new { }, true, lifetime.Token); if (!IsDisposed) notice.Text = "엔진을 종료합니다. 시작 버튼으로 다시 켤 수 있어요."; }
        catch (Exception error) { ShowError(error); }
        finally { if (!IsDisposed) stop.Enabled = true; }
        await RefreshStatus();
    }

    private async Task RefreshStatus()
    {
        if (refreshing || IsDisposed || lifetime.IsCancellationRequested) return;
        refreshing = true;
        try {
            Dictionary<string, object> data = await EngineManagementApi.Call("GET", "/v1/status", null, false, lifetime.Token);
            if (IsDisposed) return;
            string state = Value(data, "state");
            string description = state == "ready" ? "대화 준비 완료" : state == "loading" ? "모델 준비 중" : state == "idle" ? "연결됨 · 첫 대화 때 모델 시작" : "모델 확인 필요";
            status.Text = "CPU 로컬 엔진 · " + description + "\n" + Value(data, "model");
        } catch { if (!IsDisposed) status.Text = "엔진이 꺼져 있거나 연결할 수 없어요\n‘엔진 시작’을 눌러 주세요."; }
        finally { refreshing = false; }
    }

    private async Task CreatePairing()
    {
        Uri uri;
        if (!Uri.TryCreate(site.Text.Trim(), UriKind.Absolute, out uri) || !String.IsNullOrEmpty(uri.UserInfo) || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == 4175))) {
            notice.Text = "https://로 시작하는 사이트 주소를 넣어 주세요. 로컬 검수는 http://127.0.0.1:4175를 사용합니다.";
            return;
        }
        try {
            await MoaLocalRuntime.EnsureStartedAsync(lifetime.Token);
            string origin = uri.GetLeftPart(UriPartial.Authority);
            Dictionary<string, object> result = await EngineManagementApi.Call("POST", "/v1/pairing-codes", new { origin = origin }, true, lifetime.Token);
            if (IsDisposed) return;
            site.Text = origin;
            code.Text = Value(result, "code");
            notice.Text = origin + " 전용 코드입니다. 5분 안에 사이트에 입력하세요. 한 번만 사용할 수 있습니다.";
        } catch (Exception error) { ShowError(error); }
    }

    private async Task RefreshConnections()
    {
        try {
            Dictionary<string, object> result = await EngineManagementApi.Call("GET", "/v1/connections", null, true, lifetime.Token);
            if (IsDisposed) return;
            object rows;
            connections.Items.Clear();
            if (result.TryGetValue("connections", out rows)) foreach (object item in rows as object[] ?? new object[0]) {
                Dictionary<string, object> row = item as Dictionary<string, object>;
                if (row != null) connections.Items.Add(new ConnectionRow { Id = Value(row, "id"), Origin = Value(row, "origin") });
            }
        } catch (Exception error) { ShowError(error); }
    }

    private async Task SendMessage()
    {
        if (activeChat != null || String.IsNullOrWhiteSpace(message.Text)) return;
        string text = message.Text.Trim();
        message.Clear();
        transcript.AppendText("나: " + text + "\n모아: ");
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeChat = request;
        send.Text = "중지"; send.AccessibleName = "답변 생성 중지";
        message.ReadOnly = true; clear.Enabled = false;
        bool output = false;
        try {
            string reply = await MoaLocalChat.ReplyAsync(text, new Progress<string>(delegate(string part) {
                if (IsDisposed || activeChat != request || request.IsCancellationRequested) return;
                output = true; transcript.AppendText(part); transcript.ScrollToCaret();
            }), request.Token);
            if (!IsDisposed && !output) { if (MoaLocalChat.IsClearCommand(text)) transcript.Clear(); transcript.AppendText(reply); }
        } catch (OperationCanceledException) { if (!IsDisposed) transcript.AppendText("\n[답변 수신을 멈췄어요. 다시 말 걸 수 있어요.]"); }
        catch (Exception error) { ShowError(error); }
        finally {
            activeChat = null; request.Dispose();
            if (!IsDisposed) { transcript.AppendText("\n\n"); message.ReadOnly = false; send.Text = "보내기"; send.AccessibleName = "메시지 보내기"; clear.Enabled = true; message.Focus(); }
        }
    }

    private void ShowError(Exception error)
    {
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        notice.Text = error is InvalidOperationException || error is ArgumentException ? error.Message : "엔진에 연결하지 못했어요. 실행 상태를 확인해 주세요.";
    }
    private static string Value(Dictionary<string, object> values, string key) { object value; return values.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
    private sealed class ConnectionRow { public string Id; public string Origin; public override string ToString() { return Origin; } }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}

internal static class EngineManagementApi
{
    public static Task<Dictionary<string, object>> Call(string method, string path, object body, bool authenticated, CancellationToken token)
    {
        return Task.Run(delegate {
            JavaScriptSerializer json = new JavaScriptSerializer();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(MoaLocalRuntime.BaseUrl + path);
            request.Proxy = null; request.AllowAutoRedirect = false; request.Method = method; request.Timeout = 5000; request.ReadWriteTimeout = 5000; request.KeepAlive = false;
            if (authenticated) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + MoaLocalRuntime.ReadNativeToken();
            using (token.Register(delegate { request.Abort(); })) {
                try {
                    if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(body)); request.ContentType = "application/json"; request.ContentLength = bytes.Length; using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) using (StreamReader reader = new StreamReader(response.GetResponseStream())) {
                        string result = reader.ReadToEnd();
                        return String.IsNullOrEmpty(result) ? new Dictionary<string, object>() : json.DeserializeObject(result) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    }
                } catch (WebException error) {
                    token.ThrowIfCancellationRequested();
                    if (error.Response != null) using (WebResponse response = error.Response) using (StreamReader reader = new StreamReader(response.GetResponseStream())) {
                        Dictionary<string, object> result = json.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                        object errorValue; object messageValue;
                        if (result != null && result.TryGetValue("error", out errorValue)) { Dictionary<string, object> detail = errorValue as Dictionary<string, object>; if (detail != null && detail.TryGetValue("message", out messageValue)) throw new InvalidOperationException(Convert.ToString(messageValue)); }
                    }
                    throw new InvalidOperationException("엔진에 연결하지 못했어요. 실행 상태를 확인해 주세요.");
                }
            }
        }, token);
    }
}
