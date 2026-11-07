using Moa;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Local CPU chat. Only explicit pet commands change the desktop companion.
public sealed class MoaCompactChat : Form
{
    private readonly MoaPetForm pet;
    private readonly RichTextBox transcript;
    private readonly TextBox input;
    private readonly Label stateLabel;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly Button send;
    private readonly FlowLayoutPanel suggestions;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private bool lifetimeDisposed;
    private bool historyLoading = true;
    private CancellationTokenSource activeRequest;
    private DateTime requestStarted;
    private static readonly Color Paper = Color.FromArgb(255, 253, 248);
    private static readonly Color Ink = Color.FromArgb(48, 44, 41);
    private static readonly Color Muted = Color.FromArgb(117, 109, 100);
    private static readonly Color Accent = Color.FromArgb(164, 89, 62);

    public MoaCompactChat(MoaPetForm pet, bool live = true)
    {
        if (pet == null) throw new ArgumentNullException("pet");
        this.pet = pet;
        // All dimensions below are authored at 96 DPI. Without this baseline,
        // a system-DPI-aware process keeps the pixel-sized rows while point fonts
        // grow at 125/150/200%, clipping text and shrinking the apparent window.
        SuspendLayout();
        Text = "모아와 대화";
        AccessibleName = "모아와 대화";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(364, 540);
        MinimumSize = new Size(340, 480);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Paper;
        ForeColor = Ink;
        Font = new Font("맑은 고딕", 9F);
        KeyPreview = true;
        Padding = new Padding(1);
        DoubleBuffered = true;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(18, 14, 18, 14);
        layout.ColumnCount = 1;
        layout.RowCount = 5;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(layout);

        Panel header = new Panel();
        header.Dock = DockStyle.Fill;
        header.Margin = Padding.Empty;
        Label title = new Label();
        title.Text = "모아";
        title.Font = new Font(Font.FontFamily, 20F, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(0, 0);
        Label subtitle = new Label();
        subtitle.Text = "이 PC에서 나누는 대화";
        subtitle.ForeColor = Muted;
        subtitle.AutoSize = true;
        subtitle.Location = new Point(2, 39);
        Button close = MakeButton("×", Paper, Muted);
        close.Size = new Size(34, 34);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Location = new Point(292, 2);
        close.Font = new Font(Font.FontFamily, 18F);
        close.AccessibleName = "대화 닫고 모아로 돌아가기";
        close.Click += delegate { Close(); };
        header.Resize += delegate { close.Left = header.ClientSize.Width - close.Width; };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(close);
        header.MouseDown += DragHeader;
        title.MouseDown += DragHeader;
        subtitle.MouseDown += DragHeader;
        layout.Controls.Add(header, 0, 0);

        stateLabel = new Label();
        stateLabel.AutoEllipsis = true;
        stateLabel.Dock = DockStyle.Fill;
        stateLabel.Margin = new Padding(0, 4, 0, 5);
        stateLabel.ForeColor = Accent;
        stateLabel.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);
        stateLabel.AccessibleName = "모아 현재 상태";
        layout.Controls.Add(stateLabel, 0, 1);

        RoundedChatPanel conversation = new RoundedChatPanel();
        conversation.Dock = DockStyle.Fill;
        conversation.Margin = Padding.Empty;
        conversation.Padding = new Padding(12);
        conversation.FillColor = Color.FromArgb(246, 242, 234);
        transcript = new RichTextBox();
        transcript.Dock = DockStyle.Fill;
        transcript.BorderStyle = BorderStyle.None;
        transcript.BackColor = conversation.FillColor;
        transcript.ForeColor = Ink;
        transcript.ReadOnly = true;
        transcript.DetectUrls = false;
        transcript.Font = new Font(Font.FontFamily, 9.5F);
        transcript.ScrollBars = RichTextBoxScrollBars.Vertical;
        transcript.AccessibleName = "모아와 나눈 대화";
        transcript.TabStop = true;
        conversation.Controls.Add(transcript);
        layout.Controls.Add(conversation, 0, 2);

        suggestions = new FlowLayoutPanel();
        suggestions.Dock = DockStyle.Fill;
        suggestions.Margin = Padding.Empty;
        suggestions.Padding = new Padding(0, 9, 0, 0);
        suggestions.WrapContents = false;
        AddSuggestion(suggestions, "기능", "/도구");
        AddSuggestion(suggestions, "창 목록", "/창");
        AddSuggestion(suggestions, "산책해", "산책해");
        AddSuggestion(suggestions, "멈춰", "멈춰");
        layout.Controls.Add(suggestions, 0, 3);

        TableLayoutPanel compose = new TableLayoutPanel();
        compose.Dock = DockStyle.Fill;
        compose.Margin = new Padding(0, 5, 0, 3);
        compose.ColumnCount = 2;
        compose.RowCount = 1;
        compose.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        compose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        RoundedChatPanel inputPanel = new RoundedChatPanel();
        inputPanel.FillColor = Color.White;
        inputPanel.LineColor = Color.FromArgb(224, 214, 202);
        inputPanel.Dock = DockStyle.Fill;
        inputPanel.Margin = new Padding(0, 0, 5, 0);
        inputPanel.Padding = new Padding(10, 11, 10, 7);
        input = new TextBox();
        input.Dock = DockStyle.Fill;
        input.BorderStyle = BorderStyle.None;
        input.BackColor = Color.White;
        input.ForeColor = Ink;
        input.MaxLength = 1000;
        input.Name = "CompactInput";
        input.TextChanged += delegate { pet.State.Editing(input.TextLength > 0); };
        transcript.Name = "CompactTranscript";
        input.AccessibleName = "모아에게 보낼 메시지";
        input.KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.SuppressKeyPress = true;
                SendCurrentMessage();
            }
        };
        inputPanel.Controls.Add(input);
        compose.Controls.Add(inputPanel, 0, 0);
        send = MakeButton("보내기", Ink, Paper);
        send.Dock = DockStyle.Fill;
        send.Margin = Padding.Empty;
        send.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
        send.AccessibleName = "메시지 보내기";
        send.Click += delegate
        {
            if (activeRequest != null) activeRequest.Cancel();
            else SendCurrentMessage();
        };
        compose.Controls.Add(send, 1, 0);
        layout.Controls.Add(compose, 0, 4);

        input.ReadOnly = true;
        suggestions.Enabled = false;
        send.Enabled = false;
        RefreshPetStatus();
        refreshTimer = new System.Windows.Forms.Timer();
        refreshTimer.Interval = 1000;
        refreshTimer.Tick += delegate { RefreshPetStatus(); };
        refreshTimer.Start();
        Shown += delegate { if (live) RestoreHistory(); else { historyLoading = false; input.ReadOnly = false; suggestions.Enabled = true; send.Enabled = true; } };
        KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Escape) { args.Handled = true; Close(); }
        };
        // Scale once after every child and absolute table row is present. Doing
        // this before returning also lets the owner place the real-sized window.
        ResumeLayout(false);
        PerformAutoScale();
        Rectangle workArea = Screen.FromControl(pet).WorkingArea;
        int availableWidth = Math.Max(280, workArea.Width - 24);
        int availableHeight = Math.Max(380, workArea.Height - 24);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, availableWidth), Math.Min(MinimumSize.Height, availableHeight));
        ClientSize = new Size(Math.Min(ClientSize.Width, availableWidth), Math.Min(ClientSize.Height, availableHeight));
        PerformLayout();
    }

    public void RefreshPetStatus()
    {
        if (IsDisposed || pet.IsDisposed) return;
        if (historyLoading)
        {
            stateLabel.Text = "대화를 불러오고 있어요";
            return;
        }
        if (activeRequest != null)
        {
            int elapsed = (int)(DateTime.UtcNow - requestStarted).TotalSeconds;
            stateLabel.Text = "답변을 쓰고 있어요  ·  " + elapsed + "초  ·  중지 가능";
            return;
        }
        string origin = pet.CurrentSource == "agent" ? "서비스 이벤트" : "기본 모드";
        stateLabel.Text = MoaLocalChat.LastLoraApplied ? "CPU 채팅  ·  추가 학습 시험 모델" : MoaLocalChat.LastDialogueExampleCount > 0 ? "로컬 채팅  ·  AI Hub 대화 예시 참고" : StateName(pet.CurrentState) + "  ·  " + (pet.CurrentSource == "agent" ? origin : "로컬 채팅") + "  ·  " + (pet.IsRoaming ? "대화 후 산책" : "산책 멈춤");
    }

    private async void RestoreHistory()
    {
        try
        {
            MoaChatMessage[] history = await MoaLocalChat.GetHistoryAsync(lifetime.Token);
            if (IsDisposed || Disposing) return;
            if (history.Length == 0)
                AppendMessage("모아", "안녕! 오늘은 어떤 이야기를 나눌까요?\n창을 닫으면 다시 곁에서 지낼게요.", Accent);
            else
                foreach (MoaChatMessage message in history)
                    AppendMessage(message.Role == "user" ? "나" : "모아", message.Text, message.Role == "user" ? Color.FromArgb(62, 111, 90) : Accent);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
                AppendMessage("연결 안내", error is InvalidOperationException ? error.Message : "이전 대화를 불러오지 못했어요. 다시 말 걸어 주세요.", Muted);
        }
        finally
        {
            historyLoading = false;
            if (!IsDisposed && !Disposing)
            {
                input.ReadOnly = false;
                suggestions.Enabled = true;
                send.Enabled = true;
                RefreshPetStatus();
                input.Focus();
            }
        }
    }

    private static Button MakeButton(string text, Color background, Color foreground)
    {
        Button button = new Button();
        button.Text = text;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.TabStop = true;
        button.Resize += delegate
        {
            Region old = button.Region;
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Math.Max(1, button.Width), Math.Max(1, button.Height)), 10))
                button.Region = new Region(path);
            if (old != null) old.Dispose();
        };
        return button;
    }

    private void AddSuggestion(FlowLayoutPanel panel, string label, string text)
    {
        Button button = MakeButton(label, Color.FromArgb(237, 232, 222), Muted);
        button.Height = 28;
        button.Width = label.Length > 3 ? 76 : 58;
        button.Font = new Font(Font.FontFamily, 8F);
        button.Margin = new Padding(0, 0, 6, 0);
        button.Click += delegate { input.Text = text; SendCurrentMessage(); };
        panel.Controls.Add(button);
    }

    private async void SendCurrentMessage()
    {
        if (activeRequest != null || historyLoading) return;
        if (pet.IsDisposed) { Close(); return; }
        if (pet.State.Busy) { stateLabel.Text = "앞선 작업을 마무리하고 있어요. 잠시 후 보내 주세요."; return; }
        string text = input.Text.Trim();
        if (text.Length == 0) { input.Focus(); return; }
        input.Clear();
        AppendMessage("나", text, Color.FromArgb(62, 111, 90));
        string normalized = text.Replace(" ", "");
        string answer = null;
        if (normalized == "산책해")
        {
            pet.SetRoaming(true);
            answer = "좋아요! 대화창을 닫으면 다시 뽈뽈 돌아다닐게요.";
        }
        else if (normalized == "멈춰")
        {
            pet.SetRoaming(false);
            answer = "좋아요, 여기서 기다릴게요. “산책해”라고 말하면 다시 움직여요.";
        }
        else if (normalized == "지금상태")
        {
            answer = "지금은 “" + StateName(pet.CurrentState) + "” 상태예요.";
            if (!String.IsNullOrWhiteSpace(pet.CurrentMessage)) answer += "\n" + pet.CurrentMessage;
            answer += pet.CurrentSource == "agent" ? "\n연결된 서비스가 보낸 상태를 표시하고 있어요." : "\n로컬 대화와 도구를 사용할 수 있어요. 기능 버튼에서 사용법을 확인해 주세요.";
        }
        else if (normalized == "도움말")
        {
            answer = "편하게 한국어로 이야기해 줘.\n• 계산기를 좀 켜줄래? — 앱 실행\n• 그거 열어줘 — 앞서 말한 앱·사이트 열기\n• 방금 말한 내용 기억해줘 — 앞선 내 말 저장\n• 금융 / 착장 / 병원 / 3D 스튜디오 / 옷장 / 재고 관리 / 아이디어 열어줘 — 사이트 연결\n• 옷 사고싶어 → 응 → 응 — 예산·보유 옷 확인·착장 사이트 연결\n• 물세탁 가능한 걸로 볼래 → 모의 결제 승인 → 일주일 후 — 면접 구매 시연\n• 결재함 보여줘 / 구매 기준 보여줘 — 모아 시연 기록\n• 머리가 아파 / 오늘 입을 옷 좀 골라줘 / 이번 달 돈을 너무 썼어 — 관련 사이트 제안\n• 응 열어줘 / 아니 지금은 됐어 / 병원 말고 착장 — 제안에 답하기\n• 갈피 열어줘 → 여기서 로봇 찾아줘 — 사이트 열기·검색\n• 자료 탐색 메뉴를 눌러줘 / 이전 페이지로 가줘 — 열린 사이트 조작\n• 내가 좋아하는 색은 파랑이야. 기억해줘 — 기억 저장\n• 내가 좋아하는 색이 뭐였지? — 저장한 기억 확인\n• 열린 창 보여줘 / 기능 알려줘 — 창 목록·기능 안내\n• 대화 기록 지워줘 — 이 대화의 기록 비우기\n• 산책해 / 멈춰 — 산책 조절\n대화는 Obsidian 노트에 저장하고 다시 켜도 이어서 읽어요. 오래된 내용은 놓칠 수 있어요. 답변 중에는 중지 버튼을 누르면 돼요.";
        }
        if (answer != null)
        {
            AppendMessage("모아", answer, Accent);
            RefreshPetStatus();
            input.Focus();
            return;
        }
        CancellationTokenSource request = new CancellationTokenSource();
        string requestId = pet.State.Begin(false);
        activeRequest = request;
        requestStarted = DateTime.UtcNow;
        input.ReadOnly = true;
        suggestions.Enabled = false;
        send.Text = "중지";
        send.AccessibleName = "답변 생성 중지";
        RefreshPetStatus();
        bool hasOutput = false;
        IProgress<string> progress = new Progress<string>(delegate(string chunk)
        {
            if (IsDisposed || Disposing || activeRequest != request || request.IsCancellationRequested) return;
            if (!hasOutput) { BeginAssistantMessage(); hasOutput = true; }
            AppendAnswerChunk(chunk);
        });
        try
        {
            if (normalized.Contains("창선택") || normalized.Contains("창을선택") || normalized.Contains("전환해") || normalized.Contains("가져와"))
            {
                await MoaLocalRuntime.EnsureStartedAsync(request.Token);
                MoaLocalRuntime.AllowEngineForeground();
            }
            string result = await MoaLocalChat.ReplyAsync(text, progress, request.Token);
            pet.State.Receive(requestId, "done", result);
            if (IsDisposed || Disposing) return;
            if (MoaLocalChat.IsClearCommand(text)) transcript.Clear();
            if (!hasOutput) AppendMessage("모아", result, Accent);
            else AppendAnswerChunk("\n\n");
        }
        catch (OperationCanceledException)
        {
            pet.State.Cancel();
            if (!IsDisposed && !Disposing)
            {
                if (hasOutput) AppendAnswerChunk("\n\n");
                AppendMessage("알림", "답변 수신을 멈췄어요. 다시 말 걸 수 있어요.", Muted);
            }
        }
        catch (Exception error)
        {
            pet.State.Receive(requestId, "error", error.Message);
            if (!IsDisposed && !Disposing)
            {
                if (hasOutput) AppendAnswerChunk("\n\n");
                AppendMessage("연결 안내", error is InvalidOperationException || error is ArgumentException ? error.Message : "대화 연결에 문제가 생겼어요. 다시 말 걸어 주세요.", Muted);
            }
        }
        finally
        {
            if (activeRequest == request) activeRequest = null;
            request.Dispose();
            if (!IsDisposed && !Disposing)
            {
                input.ReadOnly = false;
                suggestions.Enabled = true;
                send.Text = "보내기";
                send.AccessibleName = "메시지 보내기";
                RefreshPetStatus();
                input.Focus();
            }
        }
    }

    private void BeginAssistantMessage()
    {
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionLength = 0;
        transcript.SelectionColor = Accent;
        using (Font bold = new Font(transcript.Font, FontStyle.Bold))
        {
            transcript.SelectionFont = bold;
            transcript.AppendText("모아\n");
        }
        transcript.SelectionFont = transcript.Font;
        transcript.SelectionColor = Ink;
    }

    private void AppendAnswerChunk(string text)
    {
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionLength = 0;
        transcript.SelectionFont = transcript.Font;
        transcript.SelectionColor = Ink;
        transcript.AppendText(text);
        transcript.SelectionStart = transcript.TextLength;
        transcript.ScrollToCaret();
    }

    private void AppendMessage(string speaker, string text, Color color)
    {
        if (transcript.TextLength > 40000) transcript.Clear();
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionLength = 0;
        transcript.SelectionColor = color;
        using (Font bold = new Font(transcript.Font, FontStyle.Bold))
        {
            transcript.SelectionFont = bold;
            transcript.AppendText(speaker + "\n");
        }
        transcript.SelectionColor = Ink;
        transcript.SelectionFont = transcript.Font;
        transcript.AppendText(text + "\n\n");
        transcript.SelectionStart = transcript.TextLength;
        transcript.ScrollToCaret();
    }

    private static string StateName(string state)
    {
        switch (state)
        {
            case "called": return "불렀나요?";
            case "working": return "작업 중";
            case "attention": return "확인 필요";
            case "done": return "완료";
            case "error": return "문제 발생";
            default: return "대기 중";
        }
    }

    private void DragHeader(object sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 1 || Height < 1) return;
        Region old = Region;
        using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width, Height), 20))
            Region = new Region(path);
        if (old != null) old.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 20))
        using (Pen pen = new Pen(Color.FromArgb(215, 204, 188))) e.Graphics.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!lifetimeDisposed) { lifetime.Cancel(); lifetime.Dispose(); lifetimeDisposed = true; }
            if (activeRequest != null) activeRequest.Cancel();
            if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer.Dispose(); }
        }
        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class RoundedChatPanel : Panel
    {
        public Color FillColor = Color.White;
        public Color LineColor = Color.Transparent;
        public RoundedChatPanel() { DoubleBuffered = true; BackColor = Paper; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Paper);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 14))
            {
                using (SolidBrush brush = new SolidBrush(FillColor)) e.Graphics.FillPath(brush, path);
                if (LineColor.A > 0) using (Pen pen = new Pen(LineColor)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
