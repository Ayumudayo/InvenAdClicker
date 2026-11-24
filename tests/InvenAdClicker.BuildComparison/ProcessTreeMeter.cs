using System.Diagnostics;
using System.Runtime.InteropServices;

// Open handles retain final CPU times after a sampled child exits. Processes
// born and gone between samples remain a stated limit of the measurement.
internal sealed class ProcessTreeMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public uint Size, Faults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, Private;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr h, ref Memory counters, uint size);
    private sealed record Handle(uint Id, string Name, IntPtr Value, long Created)
    {
        public long PeakPrivateBytes { get; set; }
    }
    internal sealed record Sample(long Milliseconds, long PrivateBytes, long WorkingSetBytes, double CpuSeconds, int Processes);
    private readonly List<Handle> _handles = new();
    private readonly Stopwatch _clock;
    public List<Sample> Samples { get; } = new();
    public int UnreadableProcesses { get; private set; }
    public string[] ProcessNames => _handles.Select(h => h.Name).Distinct().OrderBy(x => x).ToArray();
    public int ProcessCount => _handles.Count;
    public ProcessTreeMeter(int root, Stopwatch clock)
    {
        _clock = clock;
        var handle = OpenProcess(0x1410, false, (uint)root);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) { CloseHandle(handle); throw new InvalidOperationException("Root process time unavailable."); }
        using var process = Process.GetProcessById(root);
        _handles.Add(new Handle((uint)root, process.ProcessName + ".exe", handle, created));
    }

    public void SampleNow()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entries = new List<Entry>();
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (Process32FirstW(snapshot, ref entry)) do { entries.Add(entry); } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        // A stored parent PID may refer to a long-dead process whose ID Windows
        // has reused for our root. Validate the child's creation time against the
        // actual parent's lifetime before adopting it, including exited parents.
        bool changed;
        do
        {
            changed = false;
            foreach (var e in entries)
            {
                var parents = _handles.Where(h => h.Id == e.Parent).ToArray();
                if (parents.Length == 0) continue;
                if (_handles.Any(h => h.Id == e.Id && GetProcessTimes(h.Value, out _, out var exited, out _, out _) && exited == 0)) continue;
                var handle = OpenProcess(0x1410, false, e.Id);
                if (handle == IntPtr.Zero) { UnreadableProcesses++; continue; }
                if (!GetProcessTimes(handle, out var created, out _, out _, out _)) { CloseHandle(handle); continue; }
                if (_handles.Any(h => h.Id == e.Id && h.Created == created) || !parents.Any(parent =>
                    GetProcessTimes(parent.Value, out _, out var exited, out _, out _) && created >= parent.Created && (exited == 0 || created <= exited)))
                { CloseHandle(handle); continue; }
                _handles.Add(new Handle(e.Id, e.Name, handle, created)); changed = true;
            }
        } while (changed);
        long privateBytes = 0, workingSet = 0, cpuTicks = 0; int running = 0;
        foreach (var h in _handles)
        {
            if (GetProcessTimes(h.Value, out _, out _, out var kernel, out var user)) cpuTicks += kernel + user;
            if (!GetExitCodeProcess(h.Value, out var code) || code != 259) continue;
            running++;
            var memory = new Memory { Size = (uint)Marshal.SizeOf<Memory>() };
            if (GetProcessMemoryInfo(h.Value, ref memory, memory.Size))
            {
                long currentPrivate = (long)memory.Private.ToUInt64();
                h.PeakPrivateBytes = Math.Max(h.PeakPrivateBytes, currentPrivate);
                privateBytes += currentPrivate; workingSet += (long)memory.WorkingSet.ToUInt64();
            }
        }
        Samples.Add(new Sample(_clock.ElapsedMilliseconds, privateBytes, workingSet, cpuTicks / 10_000_000.0, running));
    }

    public object Result() => new
    {
        SampleIntervalMilliseconds = 150,
        PeakPrivateBytes = Samples.Max(s => s.PrivateBytes),
        MeanPrivateBytes = Samples.Sum(s => (double)s.PrivateBytes) / Samples.Count,
        PeakSummedWorkingSetBytes = Samples.Max(s => s.WorkingSetBytes),
        CpuSeconds = Samples.Last().CpuSeconds,
        ProcessCount, PeakConcurrentProcesses = Samples.Max(s => s.Processes), ProcessNames, UnreadableProcesses,
        PerProcess = _handles.Select(h => new { Pid = h.Id, h.Name, h.PeakPrivateBytes,
            CpuSeconds = GetProcessTimes(h.Value, out _, out _, out var kernel, out var user) ? (kernel + user) / 10_000_000.0 : (double?)null }).ToArray(),
        RelationshipValidation = "Child creation time must fall inside the recorded parent's process lifetime, rejecting stale parent PIDs.",
        RemainingProcesses = Samples.Last().Processes,
        Limitations = "Sampled whole process tree; may miss children shorter than 150ms. Private Bytes are committed private memory, not resident RAM. Summed working set double-counts shared pages. Mean memory is sample mean. Controller excluded."
    };
    public void Dispose() { foreach (var h in _handles) CloseHandle(h.Value); }
}
