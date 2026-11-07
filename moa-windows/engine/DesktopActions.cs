using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record DesktopElement(string Ref, string Role, string Name, string[] Actions)
{
    public string ShortRef => Ref[(Ref.LastIndexOf(':') + 1)..];
    public bool Editable => Actions.Any(a => a.Replace("_", "").Replace("-", "").Equals("SetValue", StringComparison.OrdinalIgnoreCase));
}

// Views belong to one chat and are consumed before an action, including failed/cancelled turns.
public sealed record DesktopView(string WindowId, int Pid, string ProcessName, DateTimeOffset ObservedAt, DesktopElement[] Elements, string WindowTitle = "")
{
    private int consumed;
    public bool Used => Volatile.Read(ref consumed) != 0;
    public bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

public sealed partial class DesktopTools
{
    public Func<int, string> ProcessName { get; init; } = pid =>
    {
        try { using var process = Process.GetProcessById(pid); return process.ProcessName.ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    };
    private static bool IsNotepad(string app) => app.Equals("notepad", StringComparison.OrdinalIgnoreCase);
    private static bool IsCalculator(string app) => app.Equals("calculatorapp", StringComparison.OrdinalIgnoreCase) || app.Equals("calculator", StringComparison.OrdinalIgnoreCase) || app.Equals("calc", StringComparison.OrdinalIgnoreCase);
    private static bool CanInput(string app, DesktopElement target) => IsNotepad(app) && target.Editable && Regex.IsMatch(target.Role, "^(edit|textbox|textentry|textfield|text_field|document)$", RegexOptions.IgnoreCase);
    private static bool CanClick(string app, DesktopElement target) => IsCalculator(app) && target.Role.Equals("button", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(target.Name, "닫기|최소화|최대화|복원|메뉴|설정|탐색|Close|Minimize|Maximize|Restore|Menu|Settings|Navigation", RegexOptions.IgnoreCase);
    private static bool LabelMatches(string name, string selector)
    {
        if (name.Equals(selector, StringComparison.OrdinalIgnoreCase)) return true;
        string[] korean = ["영", "일", "이", "삼", "사", "오", "육", "칠", "팔", "구"];
        string[] english = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"];
        return selector.Length == 1 && char.IsAsciiDigit(selector[0]) &&
            (name == korean[selector[0] - '0'] || name.Equals(english[selector[0] - '0'], StringComparison.OrdinalIgnoreCase));
    }
    private static void Collect(JsonElement node, List<DesktopElement> output)
    {
        if (node.ValueKind != JsonValueKind.Object || output.Count >= 150) return;
        var role = Field(node, "role");
        if (role.Contains("password", StringComparison.OrdinalIgnoreCase) || node.TryGetProperty("is_password", out var secret) && secret.ValueKind == JsonValueKind.True) return;
        var reference = Field(node, "ref_id");
        if (Regex.IsMatch(reference, @"^@[A-Za-z0-9_-]+:e\d+$"))
        {
            var actions = node.TryGetProperty("available_actions", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
            output.Add(new(reference, role, Field(node, "name"), actions));
        }
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Collect(child, output);
    }

    public async Task<string> ObserveApp(string app, SkillContext context, CancellationToken token)
    {
        var windows = await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token);
        if (windows.ValueKind != JsonValueKind.Array) throw new ApiFailure(502, "desktop_schema", "창 목록을 읽지 못했어요.");
        var matches = windows.EnumerateArray().Where(w => w.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) &&
            (app == "메모장" ? IsNotepad(ProcessName(pid)) : IsCalculator(ProcessName(pid)))).ToArray();
        if (matches.Length != 1)
        {
            context.Desktop = null;
            context.Windows = Choices(windows).Where(w => app == "메모장" ? IsNotepad(w.App) : IsCalculator(w.App)).ToArray();
            return matches.Length == 0 ? $"{app} 창을 먼저 열어 줘. ‘{app} 열어줘’라고 해도 돼요."
                : FormatChoices(context.Windows);
        }
        return await Snapshot(Field(matches[0], "id"), token, context);
    }

    public async Task<string> Act(string selector, string? content, SkillContext context, CancellationToken token)
    {
        var view = context.Desktop;
        if (view is null || view.Used || DateTimeOffset.UtcNow - view.ObservedAt > TimeSpan.FromMinutes(2))
            throw new ApiFailure(409, "desktop_observation_required", "화면을 먼저 다시 읽어 줘. ‘메모장 화면 보여줘’나 /화면 창ID 를 쓰면 돼요.");
        var matches = view.Elements.Where(e => e.Ref == selector || e.ShortRef == selector || LabelMatches(e.Name, selector)).ToArray();
        if (matches.Length != 1) throw new ApiFailure(400, "desktop_target_ambiguous", "대상이 없거나 여러 개예요. 화면에 나온 e번호로 골라 줘.");
        var target = matches[0];
        var windows = await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token);
        bool present = windows.ValueKind == JsonValueKind.Array && windows.EnumerateArray().Any(w => Field(w, "id") == view.WindowId &&
            Field(w, "title") == view.WindowTitle && w.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) && pid == view.Pid);
        if (!present || ProcessName(view.Pid) != view.ProcessName)
            throw new ApiFailure(409, "desktop_window_changed", "대상 창이 바뀌었어요. 화면을 다시 읽어 줘.");
        if (content is null)
        {
            if (!CanClick(view.ProcessName, target))
                throw new ApiFailure(403, "desktop_action_not_supported", "지금 버튼 누르기는 계산기 계산 버튼에서 쓸 수 있어요. 메모장은 글 입력을 지원해요.");
        }
        else if (!CanInput(view.ProcessName, target))
            throw new ApiFailure(403, "desktop_action_not_supported", "글 입력은 메모장 본문 입력칸에서만 할 수 있어요.");
        if (content is not null && (string.IsNullOrWhiteSpace(content) || content.Length > 800) || content?.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') == true)
            throw new ApiFailure(400, "invalid_desktop_text", "입력할 글은 1~800자로 적어 줘.");
        string? expected = null;
        if (content is not null)
        {
            var before = await bridge.RunAsync(["get", target.Ref, "--property", "value"], token);
            var current = ReadValue(before);
            if (current is null || current.Length > 16000) throw new ApiFailure(409, "desktop_text_unreadable", "기존 글을 확인하지 못해 입력하지 않았어요. 빈 메모장 본문에서 다시 해 줘.");
            expected = current + (current.Length > 0 && !current.EndsWith('\n') && !current.EndsWith('\r') ? Environment.NewLine : "") + content;
        }
        token.ThrowIfCancellationRequested();
        if (!view.Consume()) throw new ApiFailure(409, "desktop_already_used", "이미 사용한 화면이에요. 다시 읽어 줘.");
        context.Desktop = null;
        // Never retry a mutation. agent-desktop re-identifies the qualified ref before delivery.
        ApiFailure? deliveryFailure = null;
        try { await bridge.RunAsync(content is null ? ["click", "--timeout-ms", "5000", "--", target.Ref] : ["set-value", "--timeout-ms", "5000", "--", target.Ref, expected!], token); }
        catch (ApiFailure failure) when (expected is not null) { deliveryFailure = failure; }
        if (expected is not null)
        {
            var after = await bridge.RunAsync(["get", target.Ref, "--property", "value"], token);
            // Windows Notepad exposes CR-only line breaks through UIA after SetValue.
            if (NormalizeLines(ReadValue(after)) != NormalizeLines(expected)) throw deliveryFailure ?? new ApiFailure(502, "desktop_verify_failed", "입력 요청은 보냈지만 글이 일치하는지 확인하지 못했어요. 중복 입력하지 말고 메모장을 확인해 줘.");
        }
        string screen;
        try { screen = await Snapshot(view.WindowId, token, context); }
        catch (ApiFailure) when (expected is not null)
        { return "메모장에 글을 추가하고 내용이 일치하는지 확인했어요. 버튼 목록은 다시 읽지 못했으니 다음 조작 전에 화면을 다시 보여 달라고 해 줘."; }
        return (expected is null ? "계산기 버튼을 누른 뒤 화면을 다시 읽었어요.\n" : "메모장에 글을 추가하고 내용이 일치하는지 확인했어요.\n") + screen;
    }
    private static string? ReadValue(JsonElement data) => data.ValueKind == JsonValueKind.String ? data.GetString() :
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? NormalizeLines(string? text) => text?.Replace("\r\n", "\n").Replace('\r', '\n');

    public async Task<string?> TryAction(string text, bool native, SkillContext context, CancellationToken token)
    {
        var observe = Regex.Match(text, @"^(메모장|계산기)\s*(?:화면|창)(?:을)?\s*(?:보여\s*줘|읽어\s*줘)[.!?]*$");
        var input = Regex.Match(text, @"^(?:메모장\s*에|(?:그|이)\s*창\s*에|이어서)\s*(.+?)\s*(?:입력해\s*줘(?:요)?|써\s*줘(?:요)?|적어\s*줘(?:요)?|추가해\s*줘(?:요)?)[.!?]*$", RegexOptions.Singleline);
        var click = Regex.Match(text, @"^(?:계산기(?:에서)?\s*)?(e\d+|\d|더하기|빼기|곱하기|나누기|같음|등호)\s*(?:눌러\s*줘|클릭해\s*줘)[.!?]*$");
        bool command = text.StartsWith("/클릭") || text.StartsWith("/입력");
        if (!command && !observe.Success && !input.Success && !click.Success) return null;
        if (!native) throw new ApiFailure(403, "native_only", "PC 조작은 Windows 모아 채팅창에서 해 줘.");
        if (observe.Success) return await ObserveApp(observe.Groups[1].Value, context, token);
        if (input.Success)
        {
            var followup = !text.StartsWith("메모장");
            if (followup && (context.Desktop is null || !IsNotepad(context.Desktop.ProcessName)))
                return "글을 이어 쓸 메모장 창을 먼저 골라 줘. ‘메모장 화면 보여줘’라고 하면 돼요.";
            var read = context.Desktop is { } v && IsNotepad(v.ProcessName)
                ? await RefreshSelected(context, token) : await ObserveApp("메모장", context, token);
            var edits = context.Desktop?.Elements.Where(e => CanInput(context.Desktop.ProcessName, e)).ToArray() ?? [];
            if (edits.Length != 1) return read + "\n입력칸이 하나로 확인되면 /입력 e번호 내용 으로 적을 수 있어요.";
            return (await Act(edits[0].Ref, input.Groups[1].Value, context, token)).Split('\n')[0];
        }
        if (click.Success)
        {
            if (text.StartsWith("계산기"))
            {
                var read = await ObserveApp("계산기", context, token);
                if (context.Desktop is null) return read;
            }
            return await Act(click.Groups[1].Value, null, context, token);
        }
        if (text.StartsWith("/클릭 ")) return await Act(text[4..].Trim(), null, context, token);
        var fill = Regex.Match(text, @"^/입력\s+(\S+)\s+(.+)$", RegexOptions.Singleline);
        if (fill.Success) return await Act(fill.Groups[1].Value, fill.Groups[2].Value, context, token);
        return "계산기 버튼은 /클릭 e번호, 메모장 글은 /입력 e번호 내용 으로 적어 줘.";
    }
}
