using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
namespace VoltiEngine;

// Volti is unmodified and cannot honor our lease; detect it and stop only our child.
public sealed class CpuModelLease : IDisposable
{
    private readonly FileStream lease;
    private readonly object gate = new();
    private Process? owned;
    private Timer? monitor;
    private IntPtr job;
    private bool disposed;
    private volatile bool conflict;
    public bool Conflict { get { return conflict; } }
    public const string BusyMessage = "볼티·다른 모델의 실행 상태를 확인해 주세요. 실행 중이면 정상 종료 후 다시 보내 주세요.";
    public static bool HasCompetitor(int ownPid = -1)
    {
        // Also detect installed Volti across process-visibility boundaries.
        try { if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port is 18471 or 18474)) return true; }
        catch (NetworkInformationException) { return true; } // Cannot establish exclusivity: do not start.
        foreach (string name in new[] { "VoltiEngine", "llama-server" })
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    if (p.Id == ownPid) continue;
                    try { if (!p.HasExited) return true; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                }
        return false;
    }
    public CpuModelLease()
    {
        try { lease = new FileStream(Path.Combine(SharedResources.MoaRoot, "cpu-model.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ApiFailure(409, "cpu_model_busy", "다른 모아가 모델을 사용 중이에요. 답변이 끝난 뒤 다시 보내 주세요."); }
        try
        {
            if (HasCompetitor()) throw new ApiFailure(409, "cpu_model_busy", BusyMessage);
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new InvalidOperationException("모델 수명 관리 핸들을 만들지 못했어요.");
            var info = new ExtendedLimits(); info.Basic.LimitFlags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new InvalidOperationException("모델 수명 제한을 설정하지 못했어요.");
        }
        catch { if (job != IntPtr.Zero) CloseHandle(job); lease.Dispose(); throw; }
    }
    public void Attach(Process process)
    {
        lock (gate)
        {
            owned = process;
            if (!AssignProcessToJobObject(job, process.Handle))
            { StopOwned(); throw new InvalidOperationException("모델 프로세스를 안전하게 관리할 수 없어 시작을 취소했어요."); }
            Check();
            if (Conflict) throw new ApiFailure(409, "cpu_model_busy", BusyMessage);
            monitor = new Timer(_ => { lock (gate) { if (!disposed) Check(); } }, null, 100, 100);
        }
    }
    private void Check()
    {
        try { if (HasCompetitor(owned?.Id ?? -1)) { conflict = true; StopOwned(); } }
        catch { conflict = true; StopOwned(); }
    }
    private void StopOwned()
    {
        if (owned == null) return;
        try { if (!owned.HasExited) { owned.Kill(entireProcessTree: true); owned.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; monitor?.Dispose();
            try { StopOwned(); }
            finally { if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; } lease.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
