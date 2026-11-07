using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moa;

class MoaDesktopTests
{
    static List<string> results = new List<string>();
    static string root;
    static int code;
    static void Check(bool passed, string name) { results.Add((passed ? "PASS " : "FAIL ") + name); if (!passed) throw new Exception(name); }
    static Button FindSend(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            var button = control as Button;
            if (button != null && button.AccessibleName == "메시지 보내기") return button;
            var child = FindSend(control); if (child != null) return child;
        }
        return null;
    }
    static void RoamingTests()
    {
        var size = new Size(194, 224); var area = new Rectangle(-1920, -200, 1920, 1040);
        var motion = new MoaRoaming(new Point(-400, 300), 42); Point initial = motion.Position;
        bool contained = true;
        for (int i = 0; i < 10000; i++) { Point p = motion.Step(.033, area, size, false); contained &= area.Contains(new Rectangle(p, size)); }
        Check(contained, "long roaming stays inside negative-coordinate monitor working area");
        Check(motion.Position != initial, "roaming changes actual position");
        Point before = motion.Position;
        double gait = motion.DistanceTravelled;
        for (int i = 0; i < 100; i++) motion.Step(.033, area, size, true);
        Check(motion.Position == before && !motion.Moving, "interaction pauses roaming");
        Check(motion.DistanceTravelled == gait && motion.Speed == 0, "pause freezes footsteps as well as position");
        motion.Enabled = false; motion.Step(1, area, size, false);
        Check(motion.Position == before && !motion.Moving, "manual pause holds position");
        motion.Enabled = true;
        var changed = new Rectangle(0, 0, 1280, 720);
        Check(changed.Contains(new Rectangle(motion.Step(.033, changed, size, false), size)), "monitor removal clamps into available area");
        motion.Place(new Point(9999, -9999), changed, size);
        Check(changed.Contains(new Rectangle(motion.Position, size)), "drag placement constrained to working area");
        Check(MoaRoaming.Clamp(new Point(99, 99), new Rectangle(0, 0, 100, 100), size) == Point.Empty, "undersized working area has safe origin");
    }
    [STAThread] static int Main(string[] args)
    {
        root = args[1]; Directory.CreateDirectory(root);
        try
        {
            RoamingTests();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var context = new MoaDesktopContext(args[0], false))
            {
                bool started = false;
                EventHandler run = null;
                run = async delegate
                {
                    if (started) return; started = true; Application.Idle -= run;
                    try { await DesktopTests(context); }
                    catch (Exception ex) { code = 1; results.Add("FAIL " + ex); }
                    finally { context.Exit(); }
                };
                Application.Idle += run; Application.Run(context);
                Check(context.Pet.IsDisposed && context.Chat.IsDisposed, "exit disposes pet and chat");
            }
        }
        catch (Exception ex) { code = 1; results.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(root, "results.txt"), results.ToArray());
        Console.WriteLine(String.Join(Environment.NewLine, results.ToArray()));
        Console.WriteLine("RESULT " + (code == 0 ? "PASS" : "FAIL") + " (" + results.Count + " checks)");
        return code;
    }
    static async Task DesktopTests(MoaDesktopContext c)
    {
        var pet = c.Pet; var state = c.Chat.State;
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        pet.PlacePet(new Point(area.Left + 25, area.Bottom - pet.Height - 25), area);
        Check(pet.Visible && !c.Chat.Visible && pet.TimerRunning, "starts with visible pet and live animation timer");
        Check(pet.TopMost && !pet.ShowInTaskbar && pet.FormBorderStyle == FormBorderStyle.None, "pet is a borderless desktop companion");
        using (var frame = pet.RenderFrame())
        {
            Check(frame.GetPixel(0, frame.Height - 1).A == 0, "desktop background remains transparent");
            int visible = 0; for (int y = 40; y < frame.Height; y += 4) for (int x = 0; x < frame.Width; x += 4) if (frame.GetPixel(x, y).A > 100) visible++;
            Check(visible > 100, "character remains visible after transparency mask");
            frame.Save(Path.Combine(root, "pet-idle.png"), ImageFormat.Png);
        }
        var positions = new List<string>(); Point start = pet.Location;
        var gaitFrames = new HashSet<int>();
        for (int i = 0; i < 20; i++) { await Task.Delay(100); positions.Add(pet.Location.ToString()); if (pet.IsMoving) { gaitFrames.Add(pet.WalkFrame); using (var frame = pet.RenderFrame()) frame.Save(Path.Combine(root, "walk-" + pet.WalkFrame + ".png"), ImageFormat.Png); } }
        File.WriteAllLines(Path.Combine(root, "positions.txt"), positions.ToArray());
        Check(Math.Abs(start.X - pet.Left) + Math.Abs(start.Y - pet.Top) > 25, "real Windows timer moves pet over two seconds");
        Check(area.Contains(pet.Bounds), "actual pet window remains inside working area");
        Check(gaitFrames.Count >= 3, "actual movement advances multiple walking poses");
        pet.SetRoaming(false); await Task.Delay(80); start = pet.Location; await Task.Delay(250);
        Check(start == pet.Location, "pause command stops actual window movement");
        pet.SetRoaming(true); state.Editing(true); start = pet.Location; await Task.Delay(200);
        Check(start == pet.Location && pet.CurrentMotion == Motion.Listen, "typing pauses pet and displays listening motion");
        state.Editing(false); string id = state.Begin(false); start = pet.Location; await Task.Delay(200);
        Check(start == pet.Location && pet.CurrentMotion == Motion.Work, "generation pauses pet with work motion");
        state.Receive(id, "done", "완료했어요.");
        Check(pet.CurrentMotion == Motion.Done && pet.CurrentNotice.Contains("도착"), "completion is connected to pet state");
        using (var frame = pet.RenderFrame()) frame.Save(Path.Combine(root, "pet-done.png"), ImageFormat.Png);
        id = state.Begin(true); Check(pet.CurrentMotion == Motion.Confirm, "confirmation uses fifth motion"); state.Cancel();
        Check(pet.CurrentMotion == Motion.Idle && pet.CurrentNotice.Contains("취소"), "cancel returns to idle with notice");
        id = state.Begin(false); state.Receive(id, "error", "검사 오류");
        Check(pet.CurrentNotice.Contains("오류"), "errors remain errors on desktop pet");
        using (var frame = pet.RenderFrame()) frame.Save(Path.Combine(root, "pet-error.png"), ImageFormat.Png);
        pet.RequestChat(); await Task.Delay(100);
        Check(c.Chat.Visible && c.Chat.WindowState == FormWindowState.Normal, "pet opens the existing chat window");
        c.Chat.Close(); await Task.Delay(100);
        Check(!c.Chat.IsDisposed && !c.Chat.Visible && pet.Visible, "closing chat keeps pet alive");
        pet.RequestChat(); Check(c.Chat.Visible, "chat can reopen after hiding");
        c.Chat.Close(); state.Editing(false); start = pet.Location;
        for (int i = 0; i < 30 && start == pet.Location; i++) await Task.Delay(100);
        Check(start != pet.Location, "pet resumes roaming after chat and terminal states");
        using (var compact = new MoaCompactChat(pet, false))
        {
            compact.Show(); await Task.Delay(100);
            Check(compact.ClientSize.Width < 550 && compact.ClientSize.Height < 800, "Volti compact conversation layout is restored for Moa");
            var box = (TextBox)compact.Controls.Find("CompactInput", true)[0];
            box.Text = "검사 중"; Check(state.Motion == Motion.Listen, "compact input shares pet listening state");
            string pending = state.Begin(false); box.Text = "멈춰";
            FindSend(compact).PerformClick();
            Check(state.RequestId == pending && box.Text == "멈춰", "reopened compact chat preserves input while previous request finishes");
            state.Cancel();
            using (var bitmap = new Bitmap(compact.Width, compact.Height)) { compact.DrawToBitmap(bitmap, new Rectangle(Point.Empty, compact.Size)); bitmap.Save(Path.Combine(root, "compact-chat.png"), ImageFormat.Png); }
            box.Clear(); compact.Close();
        }
        pet.SetPetSize(270); Check(pet.Width == 270 && area.Contains(pet.Bounds), "size menu preserves screen bounds");
        pet.Hide(); Check(!pet.Visible, "pet can hide to tray"); pet.RestorePet(); Check(pet.Visible, "tray restores pet");
        pet.ApplyState("working", "서비스 검사", "agent"); Check(pet.CurrentState == "working" && pet.CurrentSource == "agent", "external status bridge uses independent Moa state");
        Check(MoaControlServer.PipeName.StartsWith("MoaPet-"), "status pipe is separate from Volti");
        pet.ApplyState("idle", "", "demo");
    }
}
