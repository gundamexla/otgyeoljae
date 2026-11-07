using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Moa
{
    public sealed class MoaPetForm : Form
    {
        public const string WindowTitle = "모아 · 바탕화면 친구";
        public const int ShowChatMessage = 0x804D;
        private readonly MoaStateController state;
        private readonly Dictionary<Motion, List<Bitmap>> frames = new Dictionary<Motion, List<Bitmap>>();
        private Rectangle crop;
        private Bitmap walkSheet;
        private readonly List<Rectangle> walkFrames = new List<Rectangle>();
        private int walkHeight;
        public int WalkFrame { get { return (int)(roam.DistanceTravelled / (76.0 * Width / 194.0) * 8) % 8; } }
        private readonly Timer timer = new Timer { Interval = 33 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastTick;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem roamItem = new ToolStripMenuItem("돌아다니기") { Checked = true };
        private readonly NotifyIcon tray;
        private readonly MoaRoaming roam;
        private bool dragging, disposed;
        private Point dragMouse, dragLocation;
        private long noticeUntil = 5000;
        private Outcome previousOutcome;
        private string externalState, externalMessage, externalSource;
        private MoaControlServer bridge;
        private bool moved;
        public MoaStateController State { get { return state; } }
        public string CurrentState { get { return externalState ?? (state.Busy ? (state.NeedsChoice ? "attention" : "working") : state.Outcome == Outcome.Error ? "error" : state.Motion == Motion.Done ? "done" : state.Motion == Motion.Listen ? "called" : "idle"); } }
        public string CurrentMessage { get { return externalMessage ?? state.Notice; } }
        public string CurrentSource { get { return externalSource ?? "demo"; } }
        public bool ChatOpen { get; set; }
        public void ApplyState(string value, string message, string source)
        {
            if (state.Busy) return;
            externalState = value; externalMessage = message; externalSource = source; noticeUntil = clock.ElapsedMilliseconds + 6500;
        }
        public void RestorePet() { Show(); PlacePet(Location, Screen.FromRectangle(Bounds).WorkingArea); }
        public void SetPetSize(int width) { int bottom = Bottom; Size = new Size(width, width + 30); PlacePet(new Point(Left, bottom - Height), Screen.FromRectangle(Bounds).WorkingArea); }
        public event Action ChatRequested;
        public event Action ExitRequested;
        public bool IsRoaming { get { return roam.Enabled; } }
        public bool IsMoving { get { return roam.Moving; } }
        public Motion CurrentMotion { get { return state.Motion; } }
        public string CurrentNotice { get { return state.Notice; } }
        public bool TimerRunning { get { return timer.Enabled; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x80 | 0x08000000; return p; } // layered, tool window, no activation
        }
        public MoaPetForm(string assets, MoaStateController controller)
        {
            state = controller;
            Text = WindowTitle; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            Size = new Size(194, 224); Cursor = Cursors.Hand;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = MoaRoaming.Clamp(new Point(area.Right - Width - 36, area.Bottom - Height - 16), area, Size);
            roam = new MoaRoaming(Location, Environment.TickCount);
            LoadFrames(assets);
            LoadWalk(Path.Combine(assets, "moa-walk.png"));
            menu.Items.Add("모아와 대화하기", null, delegate { RequestChat(); });
            roamItem.Click += delegate { SetRoaming(!IsRoaming); }; menu.Items.Add(roamItem);
            var sizes = new ToolStripMenuItem("크기");
            sizes.DropDownItems.Add("작게", null, delegate { SetPetSize(170); });
            sizes.DropDownItems.Add("보통", null, delegate { SetPetSize(210); });
            sizes.DropDownItems.Add("크게", null, delegate { SetPetSize(270); }); menu.Items.Add(sizes);
            var demos = new ToolStripMenuItem("상태 체험 · 실제 작업 아님");
            string[] keys = { "idle", "called", "working", "attention", "done", "error" };
            string[] labels = { "대기", "호출", "작업 중", "확인 필요", "완료", "오류" };
            for (int i = 0; i < keys.Length; i++) { string key = keys[i], label = labels[i]; demos.DropDownItems.Add(label, null, delegate { ApplyState(key, "시연: " + label, "demo"); }); } menu.Items.Add(demos);
            menu.Items.Add("AI 엔진 관리", null, delegate { new MoaEngineControlForm().Show(); });
            menu.Items.Add("정보 · 데이터 출처", null, delegate { MessageBox.Show("모아 · 내 PC의 대화 친구\n날씨: Open-Meteo\n지역: GeoNames\n모아 전용 데이터와 브라우저를 사용해요.", "모아 정보"); });
            menu.Items.Add("트레이로 숨기기", null, delegate { Hide(); });
            menu.Items.Add("모아 다시 보이기", null, delegate { RestorePet(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("모아 종료", null, delegate { if (ExitRequested != null) ExitRequested(); });
            ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "모아 · 더블클릭하면 대화해요", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { RequestChat(); };
            MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RequestChat(); };
            MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = true; moved = false; dragMouse = PointToScreen(e.Location); dragLocation = Location; Capture = true;
            };
            MouseMove += delegate
            {
                if (!dragging) return;
                Point mouse = Cursor.Position;
                if (Math.Abs(mouse.X - dragMouse.X) + Math.Abs(mouse.Y - dragMouse.Y) > 5) moved = true;
                PlacePet(new Point(dragLocation.X + mouse.X - dragMouse.X, dragLocation.Y + mouse.Y - dragMouse.Y), Screen.FromPoint(mouse).WorkingArea);
            };
            MouseUp += delegate(object sender, MouseEventArgs e) { bool click = dragging && !moved && e.Button == MouseButtons.Left; dragging = false; Capture = false; if (click) RequestChat(); };
            MouseCaptureChanged += delegate { if (!Capture) dragging = false; };
            state.Changed += StateChanged;
            timer.Tick += delegate { TickPet(); };
            Shown += delegate { lastTick = clock.ElapsedMilliseconds; RenderLayer(); timer.Start(); try { bridge = new MoaControlServer(this); bridge.Start(); } catch { } };
        }
        public void SetRoaming(bool enabled) { roam.Enabled = enabled; roamItem.Checked = enabled; }
        public void PlacePet(Point point, Rectangle area) { roam.Place(point, area, Size); Location = roam.Position; }
        public void RequestChat() { RestorePet(); externalState = externalMessage = externalSource = null; if (ChatRequested != null) ChatRequested(); }
        private void StateChanged()
        {
            externalState = externalMessage = externalSource = null;
            if (previousOutcome != state.Outcome) { noticeUntil = clock.ElapsedMilliseconds + 6000; previousOutcome = state.Outcome; }
        }
        private void TickPet()
        {
            if (disposed) return;
            long now = clock.ElapsedMilliseconds;
            double elapsed = (now - lastTick) / 1000.0; lastTick = now;
            bool pause = !Visible || ChatOpen || dragging || menu.Visible || Bounds.Contains(Cursor.Position) || state.Busy || state.Motion == Motion.Listen || (externalState != null && externalState != "idle");
            Location = roam.Step(elapsed, Screen.FromRectangle(Bounds).WorkingArea, Size, pause);
            RenderLayer();
        }
        // Interpret the supplied GIF's edge-connected near-white background as the window's
        // transparent region. Original files and enclosed white facial details stay intact.
        private static Bitmap WindowSprite(Image source)
        {
            var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap)) g.DrawImageUnscaled(source, 0, 0);
            var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int w = bitmap.Width, h = bitmap.Height, count = w * h;
                int[] pixels = new int[count]; Marshal.Copy(bits.Scan0, pixels, 0, count);
                var visited = new bool[count]; var queue = new int[count]; int head = 0, tail = 0;
                Action<int> offer = delegate(int i)
                {
                    if (visited[i]) return; visited[i] = true;
                    int c = pixels[i];
                    if (((c >> 16) & 255) >= 238 && ((c >> 8) & 255) >= 238 && (c & 255) >= 238) queue[tail++] = i;
                };
                for (int x = 0; x < w; x++) { offer(x); offer((h - 1) * w + x); }
                for (int y = 0; y < h; y++) { offer(y * w); offer(y * w + w - 1); }
                while (head < tail)
                {
                    int i = queue[head++]; pixels[i] = 0;
                    int x = i % w, y = i / w;
                    if (x > 0) offer(i - 1); if (x + 1 < w) offer(i + 1);
                    if (y > 0) offer(i - w); if (y + 1 < h) offer(i + w);
                }
                Marshal.Copy(pixels, 0, bits.Scan0, count);
            }
            finally { bitmap.UnlockBits(bits); }
            return bitmap;
        }
        private void LoadFrames(string assets)
        {
            int left = 512, top = 512, right = 0, bottom = 0;
            foreach (Motion motion in Enum.GetValues(typeof(Motion)))
            {
                var list = new List<Bitmap>(); frames.Add(motion, list);
                using (Image gif = Image.FromFile(Path.Combine(assets, "moa-" + motion.ToString().ToLowerInvariant() + ".gif")))
                {
                    int count = gif.GetFrameCount(FrameDimension.Time);
                    for (int i = 0; i < count; i++)
                    {
                        gif.SelectActiveFrame(FrameDimension.Time, i);
                        var frame = WindowSprite(gif); list.Add(frame);
                        for (int y = 0; y < frame.Height; y += 2) for (int x = 0; x < frame.Width; x += 2)
                            if (frame.GetPixel(x, y).A > 0) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
                    }
                }
            }
            crop = Rectangle.FromLTRB(Math.Max(0, left - 3), Math.Max(0, top - 3), Math.Min(512, right + 4), Math.Min(512, bottom + 4));
        }
        public Bitmap RenderFrame()
        {
            var canvas = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Motion visual = externalState == null ? state.Motion : externalState == "called" ? Motion.Listen : externalState == "working" ? Motion.Work : externalState == "attention" ? Motion.Confirm : externalState == "done" ? Motion.Done : Motion.Idle;
                var list = frames[visual]; int index = (int)(clock.ElapsedMilliseconds / 170 % list.Count);
                float scale = Math.Min((Width - 20F) / crop.Width, (Height - 48F) / crop.Height);
                float w = crop.Width * scale, h = crop.Height * scale;
                var saved = g.Save();
                // Both source sets face right. Mirror the whole body, including the feet and tail.
                if (roam.FacingLeft) { g.TranslateTransform(Width, 0); g.ScaleTransform(-1, 1); }
                if (roam.Moving)
                {
                    Rectangle cell = walkFrames[WalkFrame];
                    float factor = Math.Min((Height - 66F) / walkHeight, (Width - 20F) / cell.Width);
                    float fw = cell.Width * factor, fh = cell.Height * factor;
                    // A slight slope follows diagonal travel, while keeping the character upright.
                    g.TranslateTransform(Width / 2F, Height - 10);
                    g.RotateTransform((float)(roam.Heading / Math.PI * 12));
                    g.DrawImage(walkSheet, new RectangleF(-fw / 2, -fh, fw, fh), cell, GraphicsUnit.Pixel);
                }
                else g.DrawImage(list[index], new RectangleF((Width - w) / 2, Height - h - 8, w, h), crop, GraphicsUnit.Pixel);
                g.Restore(saved);
                if (state.Busy || state.Motion == Motion.Listen || clock.ElapsedMilliseconds < noticeUntil || Bounds.Contains(Cursor.Position))
                {
                    string label = state.Busy ? (state.NeedsChoice ? "확인해 주세요" : "생각하고 있어요") :
                        state.Motion == Motion.Listen ? "듣고 있어요" : state.Outcome == Outcome.Completed ? "답변이 왔어요" :
                        state.Outcome == Outcome.Cancelled ? "답변을 중지했어요" :
                        state.Outcome == Outcome.Error || state.Outcome == Outcome.Disconnected ? "대화창을 확인해 주세요" : "두 번 클릭하면 대화해요";
                    if (externalMessage != null) label = externalMessage;
                    bool error = state.Outcome == Outcome.Error || state.Outcome == Outcome.Disconnected || externalState == "error";
                    using (var brush = new SolidBrush(error ? Color.FromArgb(255, 250, 226, 215) : Color.FromArgb(255, 246, 246, 233))) g.FillRectangle(brush, 2, 2, Width - 4, 28);
                    using (var font = new Font("맑은 고딕", 8.5F))
                    using (var ink = new SolidBrush(Color.FromArgb(34, 65, 57)))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(label, font, ink, new RectangleF(2, 2, Width - 4, 28), format);
                }
            }
            return canvas;
        }
        private void LoadWalk(string path)
        {
            walkSheet = new Bitmap(path);
            int cw = walkSheet.Width / 4, ch = walkSheet.Height / 2;
            for (int frame = 0; frame < 8; frame++)
            {
                int ox = frame % 4 * cw, oy = frame / 4 * ch;
                int left = cw, top = ch, right = -1, bottom = -1;
                for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
                    if (walkSheet.GetPixel(ox + x, oy + y).A > 20)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
                if (right < left) throw new InvalidDataException("모아 걷기 프레임이 비어 있어요.");
                var bounds = Rectangle.FromLTRB(ox + left, oy + top, ox + right + 1, oy + bottom + 1);
                walkFrames.Add(bounds); walkHeight = Math.Max(walkHeight, bounds.Height);
            }
        }
        private void RenderLayer()
        {
            using (Bitmap canvas = RenderFrame())
            {
                IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen);
                IntPtr bitmap = canvas.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memory, bitmap);
                try
                {
                    var at = new NativePoint(Left, Top); var from = new NativePoint(0, 0); var size = new NativeSize(Width, Height);
                    var blend = new Blend { Alpha = 255, Format = 1 };
                    if (!UpdateLayeredWindow(Handle, screen, ref at, ref size, memory, ref from, 0, ref blend, 2))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowChatMessage) { PlacePet(Location, Screen.FromRectangle(Bounds).WorkingArea); Show(); RequestChat(); return; }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true; timer.Stop(); timer.Dispose(); state.Changed -= StateChanged;
                tray.Visible = false; tray.Dispose(); menu.Dispose();
                if (bridge != null) bridge.Dispose();
                foreach (var list in frames.Values) foreach (var frame in list) frame.Dispose();
                if (walkSheet != null) walkSheet.Dispose();
            }
            base.Dispose(disposing);
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int W, H; public NativeSize(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int key, ref Blend blend, int flags);
    }
}
