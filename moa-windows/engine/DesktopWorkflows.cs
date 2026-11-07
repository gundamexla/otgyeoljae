using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record WindowChoice(string Id, int Pid, string Title, string App);

public sealed partial class DesktopTools
{
    private WindowChoice[] Choices(JsonElement data) => data.ValueKind == JsonValueKind.Array
        ? data.EnumerateArray().Take(40).Select(w => new WindowChoice(Field(w, "id"),
            w.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : 0, Field(w, "title"), ""))
            .Select(w => w with { App = ProcessName(w.Pid) }).ToArray() : [];

    public async Task<string> ListChoices(SkillContext context, CancellationToken token)
    {
        context.Windows = Choices(await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token));
        return FormatChoices(context.Windows);
    }

    private static string FormatChoices(WindowChoice[] windows) => windows.Length == 0 ? "열린 창이 없어요." :
        "열린 창이에요. ‘2번 창 선택해줘’처럼 골라 줘.\n" +
        string.Join("\n", windows.Select((w, i) => $"{i + 1}. {Clip(w.Title, 90)} ({w.Id})"));

    private async Task ValidateWindow(WindowChoice choice, CancellationToken token)
    {
        var now = Choices(await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token));
        if (!now.Contains(choice)) throw new ApiFailure(409, "desktop_window_changed", "그 창이 닫히거나 내용이 바뀌었어요. 창 목록을 다시 보여 달라고 해 줘.");
    }

    public async Task<string> RefreshSelected(SkillContext context, CancellationToken token)
    {
        var prior = context.Desktop ?? throw new ApiFailure(409, "desktop_observation_required", "어느 창인지 먼저 골라 줘. ‘창 목록 보여줘’라고 하면 돼요.");
        await ValidateWindow(new(prior.WindowId, prior.Pid, prior.WindowTitle, prior.ProcessName), token);
        return await Snapshot(prior.WindowId, token, context);
    }

    public async Task<string?> TryWindowCommand(string text, bool native, SkillContext context, CancellationToken token)
    {
        var compact = Regex.Replace(text, @"\s+", "").TrimEnd('.', '!', '?');
        bool list = compact is "창목록" or "창목록보여줘" or "열린창보여줘" or "열린창목록" or "열린창목록보여줘" or "/창";
        var select = Regex.Match(compact, @"^(?<target>w-\d+|\d+번|첫번째|두번째|세번째|그|이)(?:창)(?:을|으로)?(?:선택해줘|전환해줘|보여줘|가져와줘|골라줘)$");
        var named = Regex.Match(compact, @"^(메모장|계산기)(?:창)?(?:으로)?(?:전환해줘|가져와줘|선택해줘)$");
        if (!list && !select.Success && !named.Success) return null;
        if (!native) throw new ApiFailure(403, "native_only", "PC 창 조작은 Windows 모아에서 해 줘.");
        if (list) return await ListChoices(context, token);
        WindowChoice? target = null;
        if (named.Success)
        {
            var read = await ObserveApp(named.Groups[1].Value, context, token);
            if (context.Desktop is null) return read;
            var v = context.Desktop; target = new(v.WindowId, v.Pid, v.WindowTitle, v.ProcessName);
        }
        else
        {
            var key = select.Groups["target"].Value;
            if (key is "그" or "이" && context.Desktop is { } v) target = new(v.WindowId, v.Pid, v.WindowTitle, v.ProcessName);
            else if (key.StartsWith("w-")) target = context.Windows.SingleOrDefault(w => w.Id == key);
            else
            {
                var n = key switch { "첫번째" => 1, "두번째" => 2, "세번째" => 3, _ => int.TryParse(key.Replace("번", ""), out int number) ? number : 0 };
                if (n > 0 && n <= context.Windows.Length) target = context.Windows[n - 1];
            }
        }
        if (target is null) return "먼저 ‘창 목록 보여줘’로 목록을 보고 번호를 골라 줘.";
        await ValidateWindow(target, token);
        bool focusConfirmed = false;
        try
        {
            var result = await bridge.RunAsync(["focus-window", "--window-id", target.Id, "--headed"], token);
            focusConfirmed = result.TryGetProperty("focused", out var focused) && Field(focused, "id") == target.Id &&
                focused.TryGetProperty("is_focused", out var yes) && yes.ValueKind == JsonValueKind.True;
        }
        catch (ApiFailure failure) when (failure.Code is "desktop_failed" or "desktop_timeout")
        {
            // Windows may deny a background process foreground rights. Do not repeat focus.
            var inventory = await bridge.RunAsync(["list-windows", "--timeout-ms", "5000"], token);
            focusConfirmed = inventory.ValueKind == JsonValueKind.Array && inventory.EnumerateArray().Any(w =>
                Field(w, "id") == target.Id && w.TryGetProperty("is_focused", out var yes) && yes.ValueKind == JsonValueKind.True);
        }
        await Snapshot(target.Id, token, context);
        return focusConfirmed ? $"‘{Clip(target.Title, 90)}’ 창을 앞으로 가져왔어요." :
            $"‘{Clip(target.Title, 90)}’ 창을 선택했어요. Windows가 앞으로 가져오기는 허용하지 않았어요. 선택한 창에 이어 쓰는 건 가능해요.";
    }

    // Save a verified copy, not an assumed Ctrl+S operation on an unknown document.
    public async Task<string> ReadNotepad(SkillContext context, CancellationToken token)
    {
        if (context.Desktop is { } selected && IsNotepad(selected.ProcessName)) await RefreshSelected(context, token);
        else
        {
            var result = await ObserveApp("메모장", context, token);
            if (context.Desktop is null) throw new ApiFailure(409, "notepad_selection_required", result);
        }
        var view = context.Desktop!;
        var edits = view.Elements.Where(e => CanInput(view.ProcessName, e)).ToArray();
        if (edits.Length != 1) throw new ApiFailure(409, "desktop_target_ambiguous", "저장할 메모장 본문을 하나로 확인하지 못했어요.");
        var value = ReadValue(await bridge.RunAsync(["get", edits[0].Ref, "--property", "value"], token));
        await ValidateWindow(new(view.WindowId, view.Pid, view.WindowTitle, view.ProcessName), token);
        if (value is null || value.Length > 16000) throw new ApiFailure(409, "desktop_text_unreadable", "메모장 글을 읽지 못했거나 16,000자를 넘어서 저장하지 않았어요.");
        return NormalizeLines(value)!.Replace("\n", Environment.NewLine);
    }
}
