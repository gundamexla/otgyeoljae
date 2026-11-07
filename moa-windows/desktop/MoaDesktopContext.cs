using System;
using System.Windows.Forms;

namespace Moa
{
    public sealed class MoaDesktopContext : ApplicationContext
    {
        public readonly MoaChatForm Chat;
        public readonly MoaPetForm Pet;
        private bool exiting;
        private readonly bool localMode;
        public MoaCompactChat Compact { get; private set; }
        public MoaDesktopContext(string assets, bool local)
        {
            localMode = local;
            Chat = new MoaChatForm(assets, 220, local);
            Pet = new MoaPetForm(assets, Chat.State);
            MainForm = Pet;
            Chat.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Chat.Hide(); Chat.State.Editing(false); }
            };
            Pet.ChatRequested += OpenChat;
            Pet.ExitRequested += Exit;
            Pet.FormClosed += delegate { Exit(); };
            Pet.Show();
        }
        public void OpenChat()
        {
            if (exiting) return;
            if (localMode)
            {
                if (Compact == null || Compact.IsDisposed)
                {
                    Compact = new MoaCompactChat(Pet);
                    var area = Screen.FromControl(Pet).WorkingArea;
                    int x = Pet.Left - Compact.Width - 12;
                    if (x < area.Left) x = Pet.Right + 12;
                    Compact.Location = new System.Drawing.Point(Math.Max(area.Left, Math.Min(x, area.Right - Compact.Width)), Math.Max(area.Top, Math.Min(Pet.Bottom - Compact.Height, area.Bottom - Compact.Height)));
                    Compact.FormClosed += delegate { Pet.ChatOpen = false; Pet.State.Editing(false); };
                }
                Pet.ChatOpen = true; Compact.Show(); Compact.Activate(); return;
            }
            Chat.Show(); Chat.WindowState = FormWindowState.Normal; Chat.Activate();
        }
        public void Exit()
        {
            if (exiting) return; exiting = true;
            if (Compact != null) Compact.Close();
            Chat.Close(); Pet.Close(); ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { exiting = true; if (Compact != null) Compact.Dispose(); Chat.Dispose(); Pet.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
