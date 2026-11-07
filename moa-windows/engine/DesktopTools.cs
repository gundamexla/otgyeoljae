using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace VoltiEngine;

public interface IDesktopBridge
{
    bool Available { get; }
    Task<JsonElement> RunAsync(string[] arguments, CancellationToken token);
}

public sealed class AgentDesktopBridge(EngineOptions options) : IDesktopBridge
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    public bool Available => options.DesktopToolsEnabled && OperatingSystem.IsWindows() && File.Exists(options.DesktopToolPath);
    public async Task<JsonElement> RunAsync(string[] arguments, CancellationToken token)
    {
        try { return await RunOnceAsync(arguments, token); }
        catch (ApiFailure failure) when (failure.SafeObservationRetry && !token.IsCancellationRequested)
        {
            // Only a read that the CLI explicitly marks safe. Never repeat a mutation.
            await Task.Delay(120, token);
            return await RunOnceAsync(arguments, token);
        }
    }
    private async Task<JsonElement> RunOnceAsync(string[] arguments, CancellationToken token)
    {
        if (!Available) throw new ApiFailure(503, "desktop_unavailable", "화면 도구가 준비되지 않았어요. setup-desktop-tools.ps1과 엔진 설정을 확인해 주세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(options.DesktopToolPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = options.RootDirectory
        };
        // Keep CLI snapshots/cache inside this installation, not in a global home.
        start.Environment["AGENT_DESKTOP_HOME"] = Path.Combine(options.DataPath, "agent-desktop");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested(); process.Start();
            if (OperatingSystem.IsWindows() && arguments[0] == "focus-window") AllowSetForegroundWindow((uint)process.Id);
            var output = ReadBounded(process.StandardOutput, 2_000_000, timeout.Token);
            var error = ReadBounded(process.StandardError, 32_000, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            using var json = JsonDocument.Parse(await output, new JsonDocumentOptions { MaxDepth = 64 });
            var root = json.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True || process.ExitCode != 0)
            {
                // Do not retry actions, or put arbitrary tool error text into the model.
                var code = root.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetString() : "FAILED";
                bool retry = arguments[0] is "snapshot" or "get" or "list-windows" &&
                    root.TryGetProperty("error", out var err) && err.TryGetProperty("disposition", out var disposition) &&
                    disposition.TryGetProperty("retry", out var safe) && safe.GetString() == "safe";
                throw new ApiFailure(502, "desktop_failed", $"화면 도구가 작업을 완료하지 못했어요 ({arguments[0]}: {code}). 창 상태를 확인해 주세요. 조작을 자동으로 재실행하지 않았어요.") { SafeObservationRetry = retry };
            }
            return root.GetProperty("data").Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ApiFailure(504, "desktop_timeout", "화면 도구의 응답 시간이 지났어요. 이미 실행된 작업은 취소되지 않을 수 있어요. 창을 확인해 주세요."); }
        catch (Exception e) when (e is JsonException or System.ComponentModel.Win32Exception)
        { throw new ApiFailure(502, "desktop_failed", "화면 도구를 실행하거나 결과를 읽지 못했어요."); }
        finally
        {
            // Only stop the CLI worker, never the user's launched application.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > limit) throw new ApiFailure(502, "desktop_output_limit", "화면 정보가 너무 커요. 더 작은 창에서 시도해 주세요.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

public sealed partial class DesktopTools(IDesktopBridge bridge)
{
    public bool Available => bridge.Available;
    public async Task<string> Windows(CancellationToken token)
    {
        var data = await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token);
        if (data.ValueKind != JsonValueKind.Array) throw new ApiFailure(502, "desktop_schema", "창 목록의 형식을 읽지 못했어요.");
        var rows = data.EnumerateArray().Take(25).Select(w => $"{Field(w, "id")} · {Clip(Field(w, "title"), 100)}").ToArray();
        return rows.Length == 0 ? "읽을 수 있는 열린 창이 없어요." : "열린 창이에요. /화면 창ID 로 해당 창의 버튼과 이름을 읽을 수 있어요.\n" + string.Join("\n", rows);
    }
    public async Task<string> Snapshot(string id, CancellationToken token, SkillContext? context = null)
    {
        if (!Regex.IsMatch(id, @"^w-\d{1,20}$"))
            throw new ApiFailure(400, "invalid_window", "/창 목록에 나온 창ID를 넣어 주세요. 예: /화면 w-12345");
        var inventory = await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token);
        if (inventory.ValueKind != JsonValueKind.Array || !inventory.EnumerateArray().Any(w => Field(w, "id") == id))
            throw new ApiFailure(404, "window_missing", "현재 목록에 없는 창이에요. /창 으로 다시 확인해 주세요.");
        var window = inventory.EnumerateArray().Single(w => Field(w, "id") == id);
        var data = await bridge.RunAsync(["snapshot", "--window-id", id, "--max-depth", "8", "--compact", "--timeout-ms", "7000"], token);
        var elements = new List<DesktopElement>();
        if (data.TryGetProperty("tree", out var root)) Collect(root, elements);
        if (context is not null)
        {
            var pid = window.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : 0;
            context.Desktop = new DesktopView(id, pid, ProcessName(pid), DateTimeOffset.UtcNow, elements.ToArray(), Field(window, "title"));
        }
        var lines = new List<string>();
        if (data.TryGetProperty("tree", out var tree)) Flatten(tree, lines);
        var partial = data.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.False;
        var actionable = elements.Where(e => CanInput(context?.Desktop?.ProcessName ?? "", e) || CanClick(context?.Desktop?.ProcessName ?? "", e)).Take(40).ToArray();
        return "접근성 정보로 읽은 화면이에요. 입력값은 표시하지 않아요.\n" +
            (lines.Count == 0 ? "읽을 수 있는 이름이 없어요." : string.Join("\n", lines)) +
            (actionable.Length == 0 ? "" : "\n조작 대상:\n" + string.Join("\n", actionable.Select(e => $"{e.ShortRef} · {Clip(e.Role, 25)} · {Clip(e.Name, 60)}")) + "\n계산기: /클릭 e번호 · 메모장: /입력 e번호 내용 (기존 글 뒤에 추가)") +
            (partial || lines.Count >= 30 ? "\n일부 요소만 표시했어요." : "");
    }
    private static void Flatten(JsonElement node, List<string> lines)
    {
        if (node.ValueKind != JsonValueKind.Object || lines.Count >= 30) return;
        var role = Field(node, "role");
        if (role.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            node.TryGetProperty("is_password", out var password) && password.ValueKind == JsonValueKind.True) return;
        var name = Field(node, "name");
        if (name.Length > 0) lines.Add($"{Clip(role, 30)} · {Clip(name, 70)}");
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Flatten(child, lines);
    }
    public async Task<string> Launch(string app, CancellationToken token)
    {
        var executable = app switch { "메모장" => "notepad.exe", "계산기" => "calc.exe", "탐색기" => "explorer.exe", _ => null };
        if (executable is null) throw new ApiFailure(400, "app_not_allowed", "지금은 /앱 메모장, /앱 계산기, /앱 탐색기 를 지원해요.");
        await bridge.RunAsync(["launch", executable, "--activate", "--headed", "--timeout", "8000"], token);
        // A successful launch is not proof that a usable window exists.
        return app + " 실행 요청을 보냈어요.\n" + await Windows(token);
    }
    private static string Field(JsonElement node, string name) => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static string Clip(string value, int maximum) => value[..Math.Min(value.Length, maximum)].Replace('\n', ' ').Replace('\r', ' ');
}
