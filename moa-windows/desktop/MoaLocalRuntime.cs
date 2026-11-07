using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public static class MoaLocalRuntime
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    public static void AllowEngineForeground()
    {
        string expected = Path.Combine(Root, "engine", "MoaEngine.exe");
        foreach (var p in Process.GetProcessesByName("MoaEngine")) using (p)
        {
            try { if (String.Equals(p.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase)) AllowSetForegroundWindow((uint)p.Id); }
            catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        }
    }
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    private static string Root { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    private static Dictionary<string, object> Config()
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Root, "engine.json")));
    }
    public static string DataDirectory
    {
        get
        {
            string value = Convert.ToString(Config()["dataDirectory"]);
            if (Path.IsPathRooted(value)) throw new InvalidOperationException("모아 데이터 경로가 올바르지 않아요.");
            string full = Path.GetFullPath(Path.Combine(Root, value));
            if (!full.StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("모아 데이터가 설치 폴더 밖을 가리키고 있어요.");
            for (string part = full; part != null && part.Length >= Root.TrimEnd(Path.DirectorySeparatorChar).Length; part = Path.GetDirectoryName(part))
                if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("연결된 데이터 폴더는 사용할 수 없어요.");
            return full;
        }
    }
    public static string BaseUrl
    {
        get
        {
            int port = Convert.ToInt32(Config()["port"]);
            if (port < 1024 || port > 65535 || port == 18471 || port == 18474) throw new InvalidOperationException("모아 전용 포트를 확인해 주세요.");
            return "http://127.0.0.1:" + port;
        }
    }
    public static string ReadNativeToken()
    {
        string value = File.ReadAllText(Path.Combine(DataDirectory, "native-token.txt")).Trim();
        if (value.Length < 32 || value.IndexOfAny(new[] { ' ', '\r', '\n' }) >= 0) throw new InvalidOperationException("모아 연결 정보를 읽지 못했어요.");
        return value;
    }
    public static async Task EnsureStartedAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int probe = await Task.Run(() => Probe(), token).ConfigureAwait(false);
            if (probe == 1) return;
            if (probe != 0) throw new InvalidOperationException("모아 포트가 사용 중이거나 응답을 확인할 수 없어요. 기존 프로그램은 종료하지 않았어요.");
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Path.Combine(Root, "engine", "MoaEngine.exe"));
            start.Arguments = "--config \"" + Path.Combine(Root, "engine.json") + "\"";
            start.WorkingDirectory = Root; start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(DataDirectory, "bundle-cache");
            using (var launched = Process.Start(start))
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    token.ThrowIfCancellationRequested();
                    if (await Task.Run(() => Probe(), token).ConfigureAwait(false) == 1) return;
                    if (launched.HasExited) throw new InvalidOperationException("모아 엔진 시작에 실패했어요. 전용 설정과 포트를 확인해 주세요.");
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("모아 엔진의 응답을 기다리지 못했어요.");
        }
        finally { Gate.Release(); }
    }
    private static int Probe()
    {
        using (var tcp = new TcpClient())
        {
            try
            {
                var task = tcp.ConnectAsync("127.0.0.1", new Uri(BaseUrl).Port);
                if (!task.Wait(5000)) return 2;
            }
            catch (AggregateException e)
            {
                var socket = e.GetBaseException() as SocketException;
                return socket != null && socket.SocketErrorCode == SocketError.ConnectionRefused ? 0 : 2;
            }
        }
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(BaseUrl + "/v1/status");
            request.Proxy = null; request.AllowAutoRedirect = false; request.Timeout = request.ReadWriteTimeout = 3000;
            using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                return Convert.ToString(data["service"]) == "moa-engine" && Convert.ToInt32(data["version"]) == 1 && (bool)data["cpuOnly"] ? 1 : 2;
            }
        }
        catch { return 2; }
    }
}
