using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public enum PcInfoTopic { All, Ram, Cpu, Gpu, Storage, Os, Model, GpuMemory }
public sealed record PcDrive(string Name, ulong? SizeBytes, ulong? FreeBytes);
public sealed record PcInfoSnapshot
{
    public string? Model { get; init; }
    public string? Os { get; init; }
    public string[] Cpu { get; init; } = [];
    public int? Cores { get; init; }
    public int? LogicalProcessors { get; init; }
    public string[] Gpu { get; init; } = [];
    public ulong? InstalledRamBytes { get; init; }
    public ulong? VisibleRamBytes { get; init; }
    public ulong? AvailableRamBytes { get; init; }
    public PcDrive[] Drives { get; init; } = [];
}

public interface IPcInfoReader
{
    bool Available { get; }
    Task<PcInfoSnapshot> ReadAsync(CancellationToken token);
}

// Fixed, local, read-only queries. No chat text, identifiers, credentials or user files enter this script.
public sealed class WindowsPcInfoReader : IPcInfoReader
{
    private static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    public bool Available => OperatingSystem.IsWindows() && File.Exists(Executable);
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $result = [ordered]@{}
        try {
            $system = Get-CimInstance Win32_ComputerSystem -Property Manufacturer,Model
            $result.model = ($system.Manufacturer + ' ' + $system.Model).Trim()
        } catch {}
        try {
            $os = Get-CimInstance Win32_OperatingSystem -Property Caption,OSArchitecture,TotalVisibleMemorySize,FreePhysicalMemory
            $result.os = ($os.Caption + ' ' + $os.OSArchitecture).Trim()
            if ($null -ne $os.TotalVisibleMemorySize) { $result.visibleRamBytes = [long]$os.TotalVisibleMemorySize * 1024 }
            if ($null -ne $os.FreePhysicalMemory) { $result.availableRamBytes = [long]$os.FreePhysicalMemory * 1024 }
        } catch {}
        try {
            $ram = @(Get-CimInstance Win32_PhysicalMemory -Property Capacity)
            if ($ram.Count -gt 0 -and @($ram | Where-Object { !$_.Capacity }).Count -eq 0) {
                $result.installedRamBytes = [long](($ram | Measure-Object Capacity -Sum).Sum)
            }
        } catch {}
        try {
            $cpu = @(Get-CimInstance Win32_Processor -Property Name,NumberOfCores,NumberOfLogicalProcessors)
            $result.cpu = @($cpu | ForEach-Object { $_.Name.Trim() } | Select-Object -Unique)
            $result.cores = [int](($cpu | Measure-Object NumberOfCores -Sum).Sum)
            $result.logicalProcessors = [int](($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
        } catch {}
        try {
            $result.gpu = @(Get-CimInstance Win32_VideoController -Property Name | ForEach-Object { $_.Name.Trim() } | Select-Object -Unique)
        } catch {}
        try {
            $result.drives = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -Property DeviceID,Size,FreeSpace | ForEach-Object {
                @{ name=$_.DeviceID; sizeBytes=$_.Size; freeBytes=$_.FreeSpace }
            })
        } catch {}
        $result | ConvertTo-Json -Compress -Depth 4
        """;

    public async Task<PcInfoSnapshot> ReadAsync(CancellationToken token)
    {
        if (!Available) throw new ApiFailure(503, "pc_info_unavailable", "지금은 PC 정보를 읽을 수 없어요. Windows 모아에서 다시 확인해 줘.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(Executable) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var error = ReadBounded(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            if (process.ExitCode != 0) throw Failure();
            return JsonSerializer.Deserialize<PcInfoSnapshot>(await output, JsonDefaults.Options) ?? throw Failure();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ApiFailure(504, "pc_info_timeout", "PC 정보를 읽는 데 시간이 걸리네요. 잠시 뒤 다시 물어봐."); }
        catch (Exception e) when (e is JsonException or IOException or System.ComponentModel.Win32Exception)
        { throw Failure(); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
    private static ApiFailure Failure() => new(502, "pc_info_failed", "지금 PC 정보를 읽지 못했어요. 잠시 뒤 다시 물어봐.");
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[2048];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + length > 65536) throw Failure();
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
}

public sealed class PcInfoSkills(IPcInfoReader reader)
{
    public bool Available => reader.Available;
    public async Task<string?> TryHandle(string text, bool native, CancellationToken token, SkillContext context)
    {
        var previous = context.LastPcInfo;
        context.LastPcInfo = null;
        var compact = Regex.Replace(text.ToLowerInvariant(), @"[\s?？!！.,]", "");
        var topic = SelectTopic(compact);
        if (topic is null && previous is not null && Regex.IsMatch(compact,
            @"^(?:그럼|그래서)?(?:용량은?|얼마야|얼마예요|몇기가야|몇gb야|얼마나남았어|확인했어|확인했어요|용량확인했어|조회했어|알려줘|다시확인해줘|다시봐줘)$")) topic = previous;
        if (topic is null) return null;
        if (!native) throw new ApiFailure(403, "native_only", "PC 사양은 Windows 모아 채팅창에서만 확인할 수 있어요.");
        token.ThrowIfCancellationRequested();
        if (topic == PcInfoTopic.GpuMemory)
        {
            context.LastPcInfo = topic;
            return "그래픽카드의 전용 메모리 용량은 아직 조회하지 못해요. ‘내 GPU 알려줘’로 모델 이름은 확인할 수 있어요.";
        }
        if (!reader.Available) throw new ApiFailure(503, "pc_info_unavailable", "지금 PC 정보 조회 도구를 사용할 수 없어요. Windows 모아에서 다시 확인해 줘.");
        var data = await reader.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        context.LastPcInfo = topic;
        return Format(data, topic.Value);
    }
    private static PcInfoTopic? SelectTopic(string text)
    {
        if (text is "/pc" or "/사양" or "/시스템") return PcInfoTopic.All;
        // Definitions, shopping and instructions stay conversational; they are not requests to inspect this PC.
        if (Regex.IsMatch(text, "검색|조사|추천|가격|구매|비교|뜻|의미|원리|설명|방법|어떻게|이면|충분")) return null;
        bool own = Regex.IsMatch(text, "^(?:내|우리|이pc|이컴퓨터|이노트북)|사용중인");
        bool asking = own || Regex.IsMatch(text, "용량|사양|정보|얼마|몇|확인|알려|모델|이름|남았|남은|여유");
        if (Regex.IsMatch(text, "vram|비디오메모리|(?:gpu|그래픽(?:카드)?)(?:메모리|램)") && asking) return PcInfoTopic.GpuMemory;
        if (Regex.IsMatch(text, "(?:^|[^a-z])ram(?:[^a-z]|$)|램|메모리") && (asking || text is "ram" or "램" or "메모리")) return PcInfoTopic.Ram;
        if (Regex.IsMatch(text, "cpu|프로세서|시피유") && (asking || text is "cpu" or "프로세서")) return PcInfoTopic.Cpu;
        if (Regex.IsMatch(text, "gpu|그래픽카드|그래픽칩|지피유") && (asking || text is "gpu" or "그래픽카드")) return PcInfoTopic.Gpu;
        if (Regex.IsMatch(text, "저장공간|디스크|드라이브|ssd|hdd|하드용량") && (asking || text is "저장공간" or "디스크용량")) return PcInfoTopic.Storage;
        if (Regex.IsMatch(text, "윈도우|windows|운영체제|^os") && (asking || text.Contains("버전"))) return PcInfoTopic.Os;
        if (Regex.IsMatch(text, "노트북|컴퓨터|pc|피씨|기기") && text.Contains("모델")) return PcInfoTopic.Model;
        if (Regex.IsMatch(text, "노트북|컴퓨터|pc|피씨|시스템|기기") && Regex.IsMatch(text, "정보|사양|스펙")) return PcInfoTopic.All;
        return null;
    }
    private static string Format(PcInfoSnapshot data, PcInfoTopic topic)
    {
        string ram = data.InstalledRamBytes is > 0 ? $"설치 RAM은 {Memory(data.InstalledRamBytes.Value)}야." : "설치 RAM 용량을 확인하지 못했어요.";
        if (data.AvailableRamBytes is ulong free && data.VisibleRamBytes is > 0 && free <= data.VisibleRamBytes.Value)
            ram += $"\n지금 사용 가능한 메모리는 {Memory(free)}예요.";
        if (data.InstalledRamBytes is null or 0 && data.VisibleRamBytes is > 0)
            ram += $"\nWindows에서 인식한 RAM은 {Memory(data.VisibleRamBytes.Value)}야.";
        string cpu = Names(data.Cpu);
        if (cpu.Length > 0 && data.Cores is > 0 && data.LogicalProcessors is > 0) cpu += $" ({data.Cores}코어 / {data.LogicalProcessors}스레드)";
        string gpu = Names(data.Gpu);
        string storage = string.Join("\n", data.Drives.Where(d => Regex.IsMatch(d.Name ?? "", @"^[A-Za-z]:$") && d.SizeBytes is > 0).Take(12).Select(d =>
            $"{d.Name} 전체 {Memory(d.SizeBytes!.Value)}" + (d.FreeBytes is ulong available && available <= d.SizeBytes.Value ? $", 남은 공간 {Memory(available)}" : ", 남은 공간 확인 못함")));
        return topic switch {
            PcInfoTopic.Ram => ram,
            PcInfoTopic.Cpu => cpu.Length > 0 ? "CPU는 " + cpu + "예요." : "CPU 정보를 읽지 못했어요.",
            PcInfoTopic.Gpu => gpu.Length > 0 ? "Windows에서 인식한 GPU야.\n" + string.Join("\n", data.Gpu.Select(Clean).Where(s => s.Length > 0).Distinct()) : "GPU 정보를 읽지 못했어요.",
            PcInfoTopic.Storage => storage.Length > 0 ? "드라이브별 저장공간이야.\n" + storage + "\n용량은 Windows 표시 기준이에요." : "저장공간 정보를 읽지 못했어요.",
            PcInfoTopic.Os => Clean(data.Os).Length > 0 ? "운영체제는 " + Clean(data.Os) + "예요." : "운영체제 정보를 읽지 못했어요.",
            PcInfoTopic.Model => Clean(data.Model).Length > 0 ? "이 PC 모델은 " + Clean(data.Model) + "이야." : "PC 모델 정보를 읽지 못했어요.",
            _ => "이 PC에서 읽은 사양이야.\n" +
                "기기: " + Known(Clean(data.Model)) + "\nCPU: " + Known(cpu) + "\n" + ram +
                "\nGPU: " + Known(gpu) + "\n운영체제: " + Known(Clean(data.Os)) + "\n" + (storage.Length > 0 ? storage + " (Windows 표시 기준)" : "저장공간: 확인 못함")
        };
    }
    private static string Memory(ulong bytes) => (bytes / 1073741824d).ToString("0.#", CultureInfo.InvariantCulture) + "GB";
    private static string Clean(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "" : Regex.Replace(value.Trim(), @"[\r\n\t]+", " ");
        return text[..Math.Min(180, text.Length)];
    }
    private static string Names(string[] values) => string.Join(", ", values.Select(Clean).Where(v => v.Length > 0).Distinct().Take(8));
    private static string Known(string value) => value.Length == 0 ? "확인 못함" : value;
}
