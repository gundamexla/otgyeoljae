using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Moa;

class RealLifecycle
{
    static readonly List<string> results = new List<string>();
    static string root, phase;
    static int code, peakModels;
    const string CancelPrompt = "태양계의 여덟 행성을 각각 자세히 소개하고 마지막에 비교표도 만들어 줘.";
    static void Check(bool ok, string name) { results.Add((ok ? "PASS " : "FAIL ") + name); Console.WriteLine(results[results.Count - 1]); if (!ok) throw new Exception(name); }
    static T Find<T>(Form f, string name) where T : Control { return (T)f.Controls.Find(name, true)[0]; }
    static void Capture(Form f, string name) { using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(Point.Empty, f.Size)); b.Save(Path.Combine(root, name + ".png"), ImageFormat.Png); } }
    static void CountModels()
    {
        var processes = Process.GetProcessesByName("llama-server");
        peakModels = Math.Max(peakModels, processes.Length);
        foreach (var p in processes) p.Dispose();
        if (processes.Length > 1) throw new Exception("Multiple CPU model servers observed.");
    }
    static async Task Ready(MoaChatForm f)
    {
        for (int i = 0; i < 600 && !Find<TextBox>(f, "MessageInput").Enabled; i++) await Task.Delay(50);
        Check(Find<TextBox>(f, "MessageInput").Enabled, "history initialization completed");
    }
    static async Task Done(MoaChatForm f)
    {
        for (int i = 0; i < 3800 && f.State.Busy; i++) { CountModels(); await Task.Delay(50); }
        Check(!f.State.Busy, "request terminated");
    }
    static void Send(MoaChatForm f, string value) { Find<TextBox>(f, "MessageInput").Text = value; Find<Button>(f, "Send").PerformClick(); }
    static Task<string> Status()
    {
        return Task.Run(() =>
        {
            var request = (HttpWebRequest)WebRequest.Create(MoaLocalRuntime.BaseUrl + "/v1/status"); request.Proxy = null; request.Timeout = request.ReadWriteTimeout = 2000;
            using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
                return Convert.ToString(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd())["state"]);
        });
    }
    [STAThread] static int Main(string[] args)
    {
        root = AppDomain.CurrentDomain.BaseDirectory; phase = args[1];
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var context = new ApplicationContext())
        using (var f = new MoaChatForm(args[0], 20, true) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000,-20000), ShowInTaskbar = false })
        {
            f.Shown += async delegate
            {
                try
                {
                    await Ready(f);
                    if (phase == "conversation")
                    {
                        var watch = Stopwatch.StartNew();
                        Send(f, "네 이름은 뭐야? 짧게 한 문장으로 답해 줘."); await Done(f);
                        Check(f.State.Outcome == Outcome.Completed && !String.IsNullOrWhiteSpace(f.State.Answer), "real local AI completes in Windows form");
                        File.WriteAllText(Path.Combine(root, "answer.txt"), f.State.Answer);
                        File.WriteAllText(Path.Combine(root, "timing.txt"), "CompleteSeconds=" + watch.Elapsed.TotalSeconds.ToString("F2"));
                        Check(Find<Label>(f, "StatusBanner").Text.Contains("로컬 AI"), "live completion is displayed"); Capture(f, "real-completed");
                        var saved = await MoaLocalChat.GetHistoryAsync(CancellationToken.None);
                        Check(saved.Length == 2 && saved[1].Text == f.State.Answer, "completed turn saved by engine");
                        Check(await Status() == "idle", "model unloaded after completed reply");

                        Send(f, CancelPrompt);
                        bool generating = false;
                        for (int i = 0; i < 900 && f.State.Busy; i++)
                        {
                            CountModels();
                            if (await Status() == "ready") { generating = true; break; }
                            await Task.Delay(100);
                        }
                        Check(generating && f.State.Busy, "real model ready and request active before cancellation");
                        Find<Button>(f, "Stop").PerformClick();
                        for (int i = 0; i < 150 && await Status() != "idle"; i++) { CountModels(); await Task.Delay(100); }
                        Check(f.State.Outcome == Outcome.Cancelled && f.State.Answer == "" && f.State.Draft == "", "cancelled reply never shown as completed");
                        Check(await Status() == "idle", "CPU model released after cancel");
                        var after = await MoaLocalChat.GetHistoryAsync(CancellationToken.None);
                        Check(after.Length == 2, "cancelled turn not persisted");
                        Check(Find<TextBox>(f, "MessageInput").Enabled, "input restored after cancel");
                        Capture(f, "real-cancelled");
                        Check(peakModels == 1, "exactly one CPU model observed at peak");
                    }
                    else
                    {
                        string expected = File.ReadAllText(Path.Combine(root, "answer.txt"));
                        Check(Find<TextBox>(f, "Transcript").Text.Contains(expected), "fresh client and engine restore completed answer");
                        Check(!Find<TextBox>(f, "Transcript").Text.Contains(CancelPrompt), "cancelled prompt absent after restart");
                        var messages = await MoaLocalChat.GetHistoryAsync(CancellationToken.None);
                        Check(messages.Length == 2 && messages[1].Text == expected, "restored API history exactly matches saved turn");
                        Check(await Status() == "idle", "history restore does not start model");
                        Capture(f, "real-restored");
                    }
                }
                catch (Exception ex) { results.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + " | " + f.State.Notice); code = 1; Capture(f, "failure-" + phase); }
                finally { File.WriteAllLines(Path.Combine(root, phase + "-results.txt"), results.ToArray()); f.Close(); context.ExitThread(); }
            };
            f.Show(); Application.Run(context);
        }
        return code;
    }
}
