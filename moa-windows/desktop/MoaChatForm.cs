using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Moa
{
    public sealed class MoaChatForm : Form
    {
        private readonly MoaStateController state = new MoaStateController();
        private readonly MockChatTransport transport;
        private readonly Dictionary<Motion, Image> images = new Dictionary<Motion, Image>();
        private readonly PictureBox pet = new PictureBox();
        private readonly Label motionLabel = new Label();
        private readonly Label banner = new Label();
        private readonly TextBox transcript = new TextBox();
        private readonly TextBox input = new TextBox();
        private readonly Button send = new Button(), stop = new Button();
        private readonly ComboBox scenario = new ComboBox();
        private readonly FlowLayoutPanel choices = new FlowLayoutPanel();
        private readonly List<string> history = new List<string>();
        private CancellationTokenSource active;
        private string displayedRequest;
        private bool closing;
        private readonly bool localMode;
        private bool restoring;
        private static readonly Color Ink = Color.FromArgb(34, 65, 57);
        private static readonly Color Paper = Color.FromArgb(250, 248, 243);
        public MoaStateController State { get { return state; } }

        public MoaChatForm(string assetDirectory, int tickMilliseconds, bool useLocal = false)
        {
            localMode = useLocal; state.LocalMode = useLocal; restoring = useLocal;
            transport = new MockChatTransport(tickMilliseconds);
            Text = "모아 · 첫 만남";
            Name = "MoaChatForm";
            Font = new Font("맑은 고딕", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(960, 680); MinimumSize = new Size(900, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Paper; ForeColor = Ink;
            foreach (Motion motion in Enum.GetValues(typeof(Motion)))
            {
                string name = motion.ToString().ToLowerInvariant();
                images.Add(motion, Image.FromFile(Path.Combine(assetDirectory, "moa-" + name + ".gif")));
            }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);
            var headingRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            var heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "MOA  /  모아", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
            heading.Controls.Add(new Label { Text = "작은 움직임으로, 이야기를 함께해요.", AutoSize = true, Location = new Point(2, 49) });
            headingRow.Controls.Add(heading, 0, 0);
            headingRow.Controls.Add(new Label { Text = localMode ? "Qwen 3.5 · 내 PC에서" : "첫 구현 · 모의 대화", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight, Padding = new Padding(0, 18, 0, 0), ForeColor = Color.FromArgb(158, 82, 40) }, 1, 0);
            root.Controls.Add(headingRow, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 274));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 1);
            var companion = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 4, Padding = new Padding(12), Margin = new Padding(0, 0, 20, 0) };
            companion.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            companion.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            companion.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            companion.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pet.Name = "Character"; pet.Dock = DockStyle.Fill; pet.SizeMode = PictureBoxSizeMode.Zoom; pet.TabStop = false;
            companion.Controls.Add(pet, 0, 0);
            motionLabel.Name = "MotionLabel"; motionLabel.Dock = DockStyle.Fill; motionLabel.TextAlign = ContentAlignment.MiddleCenter; motionLabel.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
            companion.Controls.Add(motionLabel, 0, 1);
            companion.Controls.Add(new Label { Text = "대기 · 듣기 · 처리 중\n완료 · 확인 필요", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray }, 0, 2);
            companion.Controls.Add(new Label { Text = "듣기 = 글을 입력하는 중", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 9) }, 0, 3);
            body.Controls.Add(companion, 0, 0);

            var chat = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            chat.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            chat.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            chat.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            chat.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            chat.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            chat.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); body.Controls.Add(chat, 1, 0);
            var demo = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            demo.Controls.Add(new Label { Text = "체험할 흐름", AutoSize = true, Margin = new Padding(0, 6, 14, 0) });
            scenario.Name = "Scenario"; scenario.AccessibleName = "체험할 흐름"; scenario.DropDownStyle = ComboBoxStyle.DropDownList; scenario.Width = 224;
            scenario.Items.AddRange(new object[] { "일반 대화", "대상 선택", "오류 체험", "연결 끊김", "천천히 응답 · 취소 체험" }); scenario.SelectedIndex = 0;
            demo.Controls.Add(scenario); chat.Controls.Add(demo, 0, 0);
            if (localMode) { demo.Visible = false; chat.RowStyles[0].Height = 0; }
            banner.Name = "StatusBanner"; banner.Dock = DockStyle.Fill; banner.Padding = new Padding(10, 5, 10, 5); banner.TextAlign = ContentAlignment.MiddleLeft; banner.Margin = new Padding(0, 0, 0, 8);
            chat.Controls.Add(banner, 0, 1);
            transcript.Name = "Transcript"; transcript.AccessibleName = "대화 내용"; transcript.Dock = DockStyle.Fill; transcript.Multiline = true; transcript.ScrollBars = ScrollBars.Vertical; transcript.ReadOnly = true; transcript.BorderStyle = BorderStyle.None; transcript.BackColor = Color.White; transcript.ForeColor = Ink; transcript.Font = new Font(Font.FontFamily, 11);
            chat.Controls.Add(transcript, 0, 2);
            choices.Name = "Choices"; choices.Dock = DockStyle.Fill; choices.WrapContents = false;
            foreach (string label in new[] { "짧은 인사", "응원 한마디" })
            {
                string choice = label;
                Button b = MakeButton(label == "짧은 인사" ? "ChoiceHello" : "ChoiceCheer", label, false); b.Width = 145;
                b.Click += async delegate { string id = state.RequestId; if (state.Choose(id)) await RunAsync(id, DemoScenario.Choice, choice); };
                choices.Controls.Add(b);
            }
            chat.Controls.Add(choices, 0, 3);
            input.Name = "MessageInput"; input.AccessibleName = "메시지 입력"; input.Dock = DockStyle.Fill; input.Multiline = true; input.MaxLength = 1000; input.ScrollBars = ScrollBars.Vertical; input.BorderStyle = BorderStyle.FixedSingle;
            input.TextChanged += delegate { state.Editing(input.Text.Trim().Length > 0); send.Enabled = !state.Busy && input.Text.Trim().Length > 0; };
            input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (send.Enabled) send.PerformClick(); } };
            chat.Controls.Add(input, 0, 4);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            StyleButton(send, "Send", "보내기", true); StyleButton(stop, "Stop", "중지", false);
            send.Click += async delegate { await SubmitAsync(); };
            stop.Click += delegate
            {
                bool wasChoosing = state.NeedsChoice;
                state.Cancel();
                if (active != null)
                {
                    active.Cancel();
                    if (wasChoosing) { active.Dispose(); active = null; }
                }
            };
            actions.Controls.Add(send); actions.Controls.Add(stop);
            actions.Controls.Add(new Label { Text = "Ctrl + Enter로 보내기", AutoSize = true, Margin = new Padding(0, 12, 12, 0), Font = new Font(Font.FontFamily, 9) });
            chat.Controls.Add(actions, 0, 5);
            root.Controls.Add(new Label { Text = localMode ? "완료된 대화는 모아 전용 Obsidian 폴더에 저장돼요. ‘대화 기록 지워줘’로 지울 수 있어요." : "모의 응답을 체험하는 창이에요. 대화는 창을 닫으면 사라져요.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = new Font(Font.FontFamily, 9), ForeColor = Color.DimGray }, 0, 2);
            history.Add(localMode ? "모아\n반가워요! 이 PC의 로컬 AI와 이야기해요." : "모아\n반가워요! 메시지를 보내면 움직임과 답변 흐름을 볼 수 있어요.");
            state.Changed += RefreshState;
            RefreshState();
            if (localMode) Shown += async delegate
            {
                restoring = true; RefreshState(); banner.Text = "이전 대화를 불러오고 있어요.";
                try
                {
                    var messages = await MoaLocalChat.GetHistoryAsync(CancellationToken.None);
                    if (closing) return;
                    if (messages.Length > 0) { history.Clear(); foreach (var message in messages) history.Add((message.Role == "user" ? "나" : "모아") + "\n" + message.Text); }
                }
                catch (Exception ex) { if (!closing) history.Add("안내\n" + ex.Message); }
                finally { restoring = false; if (!closing) RefreshState(); }
            };
        }
        private Button MakeButton(string name, string text, bool primary) { var b = new Button(); StyleButton(b, name, text, primary); return b; }
        private void StyleButton(Button b, string name, string text, bool primary)
        {
            b.Name = name; b.Text = text; b.AccessibleName = text; b.Size = new Size(96, 36); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? Ink : Color.FromArgb(231, 235, 228); b.ForeColor = primary ? Color.White : Ink; b.Cursor = Cursors.Hand; b.Margin = new Padding(6, 7, 0, 0);
        }
        private async Task SubmitAsync()
        {
            if (restoring || state.Busy || input.Text.Trim().Length == 0) return;
            string message = input.Text.Trim(); DemoScenario demo = (DemoScenario)scenario.SelectedIndex;
            input.Clear();
            // Bound this ephemeral preview; no conversation files are created.
            while (history.Count > 39) history.RemoveAt(0);
            history.Add("나\n" + message);
            active = new CancellationTokenSource();
            if (localMode)
            {
                string localId = state.Begin(false); displayedRequest = localId;
                var cancellation = active;
                try
                {
                    var progress = new Progress<string>(chunk => { if (!closing) state.Receive(localId, "delta", chunk); });
                    string answer = await MoaLocalChat.ReplyAsync(message, progress, cancellation.Token);
                    if (!closing)
                    {
                        if (MoaLocalChat.IsClearCommand(message)) history.Clear();
                        state.Receive(localId, "done", answer);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!closing) state.Receive(localId, "error", ex.Message); }
                finally { cancellation.Dispose(); if (ReferenceEquals(active, cancellation)) active = null; }
                return;
            }
            string id = state.Begin(demo == DemoScenario.Choice); displayedRequest = id;
            if (demo != DemoScenario.Choice) await RunAsync(id, demo, null);
        }
        private async Task RunAsync(string id, DemoScenario demo, string choice)
        {
            var cancellation = active;
            try
            {
                await transport.RunAsync(id, demo, choice, delegate(string request, string kind, string value)
                { if (!closing) state.Receive(request, kind, value); }, cancellation.Token);
                if (!closing) state.StreamEnded(id);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!closing) state.Receive(id, "error", "응답을 표시하지 못했어요. 다시 시도해 주세요."); }
            finally
            {
                cancellation.Dispose();
                if (ReferenceEquals(active, cancellation)) active = null;
            }
        }
        private void RefreshState()
        {
            if (closing) return;
            pet.Image = images[state.Motion]; pet.AccessibleName = "모아 " + state.Motion;
            string[] labels = { "기다리고 있어요", "듣고 있어요", "생각하고 있어요", "답변이 왔어요", "골라 주세요" };
            motionLabel.Text = labels[(int)state.Motion]; banner.Text = state.Notice;
            bool failure = state.Outcome == Outcome.Error || state.Outcome == Outcome.Disconnected;
            banner.BackColor = failure ? Color.FromArgb(252, 229, 216) : Color.FromArgb(222, 235, 227);
            if (!state.Busy && displayedRequest != null)
            {
                if (state.Outcome == Outcome.Completed) history.Add((localMode ? "모아\n" : "모아 · 모의 답변\n") + state.Answer);
                else history.Add("안내\n" + state.Notice);
                displayedRequest = null;
            }
            string partial = state.Draft.Length == 0 ? "" : "\n\n모아 · 작성 중\n" + state.Draft;
            transcript.Text = (String.Join("\n\n", history.ToArray()) + partial).Replace("\n", "\r\n");
            transcript.SelectionStart = transcript.TextLength; transcript.ScrollToCaret();
            input.Enabled = scenario.Enabled = !state.Busy && !restoring;
            send.Enabled = !state.Busy && !restoring && input.Text.Trim().Length > 0;
            stop.Enabled = state.Busy; choices.Visible = state.NeedsChoice;
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true; state.Changed -= RefreshState;
            if (active != null) { active.Cancel(); if (state.NeedsChoice) { active.Dispose(); active = null; } }
            pet.Image = null; foreach (Image img in images.Values) img.Dispose();
            base.OnFormClosed(e);
        }
    }
}
