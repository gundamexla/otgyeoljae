using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moa;

class LocalIntegration
{
    static readonly List<string> checks = new List<string>();
    static string assets, root;
    static int result;
    static T Find<T>(Form f, string name) where T : Control { return (T)f.Controls.Find(name, true)[0]; }
    static void Check(bool ok, string name) { checks.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) throw new Exception(name); }
    static void Capture(Form f, string name) { using (var image = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(image, new Rectangle(Point.Empty, f.Size)); image.Save(Path.Combine(root, name + ".png"), ImageFormat.Png); } }
    static async Task Ready(MoaChatForm f) { for (int i = 0; i < 500 && !Find<TextBox>(f, "MessageInput").Enabled; i++) await Task.Delay(20); Check(Find<TextBox>(f, "MessageInput").Enabled, "history load restores input"); }
    static async Task Done(MoaChatForm f) { for (int i = 0; i < 10000 && f.State.Busy; i++) await Task.Delay(20); Check(!f.State.Busy, "local request terminates"); }
    static void Send(MoaChatForm f, string text) { Find<TextBox>(f, "MessageInput").Text = text; Find<Button>(f, "Send").PerformClick(); }
    static MoaChatForm Create() { return new MoaChatForm(assets, 20, true) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false }; }
    [STAThread] static int Main(string[] args)
    {
        assets = args[0]; root = AppDomain.CurrentDomain.BaseDirectory;
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var context = new ApplicationContext())
        {
            var form = Create();
            form.Shown += async delegate
            {
                try
                {
                    await Ready(form);
                    Check(!Find<ComboBox>(form, "Scenario").Visible, "live UI hides mock selector");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Send(form, "네 이름은 뭐야? 짧게 한 문장으로 답해 줘.");
                    await Done(form);
                    if (args.Length > 1 && args[1] == "--expect-busy")
                    {
                        Check(form.State.Outcome == Outcome.Error && Find<Label>(form, "StatusBanner").Text.Contains("볼티"), "running Volti blocks live UI inference with explanation");
                        Check(form.State.Answer == "" && Find<TextBox>(form, "MessageInput").Enabled, "blocked request leaves no answer and permits retry");
                        Capture(form, "local-blocked");
                        return;
                    }
                    File.WriteAllText(Path.Combine(root, "real-answer.txt"), form.State.Answer + "\r\nSeconds: " + watch.Elapsed.TotalSeconds);
                    Check(form.State.Outcome == Outcome.Completed && form.State.Answer.Contains("모아"), "actual window receives Moa answer");
                    Check(Find<Label>(form, "StatusBanner").Text.Contains("로컬 AI"), "live completion label");
                    string answer = form.State.Answer; Capture(form, "local-completed");
                    Send(form, "우주에 대한 이야기를 길게 해 줘."); await Task.Delay(1200);
                    Find<Button>(form, "Stop").PerformClick(); await Task.Delay(2000);
                    Check(form.State.Outcome == Outcome.Cancelled && form.State.Answer == "", "live request cancellation clears partial");
                    Capture(form, "local-cancelled"); form.Close();
                    using (var restored = Create())
                    {
                        restored.Show(); await Ready(restored);
                        File.WriteAllText(Path.Combine(root, "restored-transcript.txt"), Find<TextBox>(restored, "Transcript").Text);
                        Capture(restored, "local-restored");
                        Check(Find<TextBox>(restored, "Transcript").Text.Contains(answer), "window restart restores persisted answer");
                        Send(restored, "대화 기록 지워줘"); await Done(restored);
                        Check(restored.State.Outcome == Outcome.Completed && !Find<TextBox>(restored, "Transcript").Text.Contains(answer), "test conversation deletion refreshes UI");
                        restored.Close();
                    }
                }
                catch (Exception ex) { checks.Add("FAIL " + ex.Message); result = 1; }
                finally { form.Close(); File.WriteAllLines(Path.Combine(root, "ui-results.txt"), checks.ToArray()); context.ExitThread(); }
            };
            form.Show(); Application.Run(context);
        }
        Console.WriteLine(String.Join(Environment.NewLine, checks.ToArray())); return result;
    }
}
