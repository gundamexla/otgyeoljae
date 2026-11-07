using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moa;

internal static class MoaTests
{
    private static readonly List<string> results = new List<string>();
    private static string artifacts;
    private static int exitCode;
    private static void Check(string name, bool condition)
    {
        results.Add((condition ? "PASS " : "FAIL ") + name);
        if (!condition) throw new Exception(name);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        artifacts = Path.GetFullPath(args[1]); Directory.CreateDirectory(artifacts);
        try
        {
            ControllerTests(); ParserTests();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MoaChatForm(Path.GetFullPath(args[0]), 20))
            using (var context = new ApplicationContext())
            {
                // Exercise actual controls on a message loop, rendering the same form offscreen.
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
                form.Shown += async delegate
                {
                    try { await FormTests(form); }
                    catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
                    finally { form.Close(); context.ExitThread(); }
                };
                form.Show();
                Application.Run(context);
            }
        }
        catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
        File.WriteAllLines(Path.Combine(artifacts, "test-results.txt"), results.ToArray(), System.Text.Encoding.UTF8);
        Console.WriteLine(String.Join(Environment.NewLine, results.ToArray()));
        Console.WriteLine("RESULT: " + (exitCode == 0 ? "PASS" : "FAIL") + " (" + results.Count + " checks)");
        return exitCode;
    }
    private static void ControllerTests()
    {
        var s = new MoaStateController();
        Check("initial idle", s.Motion == Motion.Idle && !s.Busy);
        s.Editing(true); Check("typing listens", s.Motion == Motion.Listen);
        s.Editing(false); Check("empty returns idle", s.Motion == Motion.Idle);
        string a = s.Begin(false);
        Check("begin works before queued", s.Motion == Motion.Work && s.Busy);
        bool rejected = false; try { s.Begin(false); } catch (InvalidOperationException) { rejected = true; }
        Check("overlapping submission rejected", rejected);
        s.Receive(a, "delta", "부분"); Check("delta never completes", s.Draft == "부분" && s.Motion == Motion.Work && s.Answer == "");
        s.Receive("wrong", "done", "오래된 응답"); Check("wrong request ignored", s.Busy && s.Answer == "");
        s.Cancel(); s.Receive(a, "done", "늦은 완료");
        Check("cancel rejects late completion", s.Outcome == Outcome.Cancelled && s.Motion == Motion.Idle && s.Draft == "" && s.Answer == "");
        string b = s.Begin(false); s.Receive(a, "error", "old"); s.StreamEnded(a);
        Check("old error and EOF do not affect next request", s.RequestId == b && s.Outcome == Outcome.Running);
        s.Receive(b, "done", "정상 완료"); s.Cancel(); s.StreamEnded(b); s.Receive(b, "done", "duplicate");
        Check("done is final, cancel EOF duplicate ignored", s.Outcome == Outcome.Completed && s.Answer == "정상 완료" && s.Motion == Motion.Done);
        a = s.Begin(false); s.Receive(a, "delta", "미완성"); s.Receive(a, "error", "실패"); s.Receive(a, "done", "bad");
        Check("error clears partial and rejects late done", s.Outcome == Outcome.Error && s.Motion == Motion.Idle && s.Draft == "" && s.Answer == "");
        a = s.Begin(false); s.Receive(a, "delta", "미완성"); s.StreamEnded(a);
        Check("EOF without done is not success", s.Outcome == Outcome.Disconnected && s.Motion == Motion.Idle && s.Answer == "");
        a = s.Begin(true); s.Receive(a, "done", "premature");
        Check("choice waits without starting response", s.NeedsChoice && s.Motion == Motion.Confirm && s.Answer == "");
        Check("wrong choice request rejected", !s.Choose("wrong"));
        Check("choice resolves once", s.Choose(a) && !s.Choose(a) && s.Motion == Motion.Work);
        s.Cancel(); a = s.Begin(true); s.Cancel();
        Check("choice can be cancelled", !s.NeedsChoice && !s.Busy && s.Outcome == Outcome.Cancelled);
    }
    private static void ParserTests()
    {
        var events = new List<string>();
        var reader = new SseReader(delegate(string kind, string data) { events.Add(kind + ":" + data); });
        reader.Line(":heartbeat"); reader.Line("event: delta"); reader.Line("data: first"); reader.Line("data: second");
        Check("SSE waits for event boundary", events.Count == 0);
        reader.Line(""); Check("SSE multiline data", events.Count == 1 && events[0] == "delta:first\nsecond");
        reader.Line(""); reader.Line("event: done"); reader.Line("data: final"); reader.Line("");
        Check("SSE boundaries reset and blank ignored", events.Count == 2 && events[1] == "done:final");
    }
    private static T Find<T>(Form f, string name) where T : Control { return (T)f.Controls.Find(name, true)[0]; }
    private static void Capture(Form f, string name)
    {
        f.PerformLayout(); f.Refresh();
        using (var bitmap = new Bitmap(f.Width, f.Height))
        { f.DrawToBitmap(bitmap, new Rectangle(Point.Empty, f.Size)); bitmap.Save(Path.Combine(artifacts, name + ".png"), ImageFormat.Png); }
    }
    private static async Task Finished(MoaChatForm f)
    {
        for (int i = 0; i < 250 && f.State.Busy; i++) await Task.Delay(10);
        Check("request reaches terminal state", !f.State.Busy);
    }
    private static async Task FormTests(MoaChatForm f)
    {
        var input = Find<TextBox>(f, "MessageInput"); var send = Find<Button>(f, "Send"); var stop = Find<Button>(f, "Stop");
        var modes = Find<ComboBox>(f, "Scenario"); var transcript = Find<TextBox>(f, "Transcript");
        Capture(f, "01-idle");
        Check("blank input disabled", !send.Enabled && !stop.Enabled);
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "..", "..", "app", "assets", "moa-" + motion.ToString().ToLowerInvariant() + ".gif");
            using (Image img = Image.FromFile(path)) Check("animated GIF " + motion, ImageAnimator.CanAnimate(img) && img.GetFrameCount(FrameDimension.Time) > 1);
        }
        input.Text = "안녕 모아"; Capture(f, "02-listen");
        Check("actual typing shows listen", f.State.Motion == Motion.Listen && send.Enabled);
        send.PerformClick(); Capture(f, "03-work");
        Check("busy locks input and mode, permits stop", !input.Enabled && !modes.Enabled && !send.Enabled && stop.Enabled);
        await Finished(f); Capture(f, "04-done");
        Check("normal mock produces done", f.State.Motion == Motion.Done && f.State.Outcome == Outcome.Completed && transcript.Text.Contains("모아 · 모의 답변"));
        Check("terminal restores input", input.Enabled && modes.Enabled && !stop.Enabled);
        modes.SelectedIndex = 1; input.Text = "응답을 골라볼게"; send.PerformClick(); Capture(f, "05-confirm");
        Check("choice controls visible", Find<FlowLayoutPanel>(f, "Choices").Visible && f.State.Motion == Motion.Confirm);
        Find<Button>(f, "ChoiceCheer").PerformClick(); await Finished(f);
        Check("selected choice controls answer", f.State.Answer == "오늘도 한 걸음씩, 같이 해봐요.");
        modes.SelectedIndex = 2; input.Text = "오류 검수"; send.PerformClick(); await Finished(f); Capture(f, "06-error");
        Check("error shown without completed draft", f.State.Outcome == Outcome.Error && f.State.Motion == Motion.Idle && !transcript.Text.Contains("작성 중") && Find<Label>(f, "StatusBanner").Text.StartsWith("오류"));
        modes.SelectedIndex = 3; input.Text = "단절 검수"; send.PerformClick(); await Finished(f); Capture(f, "07-disconnected");
        Check("disconnect shows uncertainty, not done", f.State.Outcome == Outcome.Disconnected && f.State.Motion == Motion.Idle);
        modes.SelectedIndex = 4; input.Text = "대기 중 취소"; send.PerformClick(); stop.PerformClick(); Capture(f, "08-cancelled");
        Check("cancel shows banner and unlocks", f.State.Outcome == Outcome.Cancelled && input.Enabled && !stop.Enabled && Find<Label>(f, "StatusBanner").Text.StartsWith("취소됨"));
        modes.SelectedIndex = 0; input.Text = "취소 직후 다시 보내기"; send.PerformClick(); await Finished(f);
        Check("cancelled task cannot corrupt next reply", f.State.Outcome == Outcome.Completed);
        input.Text = "생성 도중 취소"; send.PerformClick(); await Task.Delay(65); stop.PerformClick(); await Task.Delay(300);
        Check("cancel during streaming discards draft", f.State.Outcome == Outcome.Cancelled && f.State.Answer == "" && f.State.Draft == "");
        modes.SelectedIndex = 1; input.Text = "선택 취소"; send.PerformClick(); stop.PerformClick();
        Check("cancel while choosing hides buttons", !Find<FlowLayoutPanel>(f, "Choices").Visible && f.State.Outcome == Outcome.Cancelled);
        f.Size = f.MinimumSize; Capture(f, "09-minimum-size");
        Check("input and send inside minimum window", f.RectangleToClient(send.RectangleToScreen(send.ClientRectangle)).Bottom <= f.ClientSize.Height && input.Width > 200);
        modes.SelectedIndex = 4; input.Text = "닫기 검수"; send.PerformClick(); f.Close(); await Task.Delay(50);
        Check("close during request disposes window", f.IsDisposed);
    }
}
