using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Moa
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static bool RestoreExisting()
        {
            string executable = Application.ExecutablePath;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
            {
                if (process.Id == Process.GetCurrentProcess().Id) continue;
                try
                {
                    if (!String.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr pet = FindWindow(null, MoaPetForm.WindowTitle); uint owner;
                    if (pet != IntPtr.Zero && GetWindowThreadProcessId(pet, out owner) != 0 && owner == process.Id)
                        return PostMessage(pet, MoaPetForm.ShowChatMessage, IntPtr.Zero, IntPtr.Zero);
                    IntPtr handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    ShowWindowAsync(handle, 9); // Restore a minimized window.
                    return SetForegroundWindow(handle);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return false;
        }
        [STAThread]
        private static void Main(string[] args)
        {
            bool first;
            using (var instance = new Mutex(true, "Local\\MoaLocal.Desktop.2026", out first))
            {
                if (!first) { RestoreExisting(); return; }
                try
                {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var desktop = new MoaDesktopContext(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets"), !(args.Length == 1 && args[0] == "--demo")))
                        Application.Run(desktop);
                }
                catch (Exception) { MessageBox.Show("모아 창을 준비하지 못했어요. assets 폴더에 모아 GIF 5개가 있는지 확인해 주세요.", "모아", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
