using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StoDialogueCapture;

internal readonly record struct MemoryRegion(
    ulong BaseAddress,
    ulong Size,
    uint State,
    uint Protect,
    uint Type)
{
    public ulong EndAddress => BaseAddress + Size;

    public bool IsReadable
    {
        get
        {
            const uint memCommit = 0x1000;
            const uint pageGuard = 0x100;
            const uint pageNoAccess = 0x01;
            var baseProtection = Protect & 0xFF;
            return State == memCommit &&
                   (Protect & pageGuard) == 0 &&
                   baseProtection != 0 &&
                   baseProtection != pageNoAccess;
        }
    }
}

internal interface IMemoryReader : IDisposable
{
    ulong MainModuleBase { get; }
    ulong MainModuleSize { get; }
    IReadOnlyList<MemoryRegion> Regions { get; }
    void RefreshRegions();
    bool TryRead(ulong address, byte[] buffer, int offset, int count, out int bytesRead);
}

internal sealed class ProcessMemory : IMemoryReader
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly nint _handle;
    private bool _disposed;

    public ulong MainModuleBase { get; }
    public ulong MainModuleSize { get; }
    public string ProcessPath { get; }
    public IReadOnlyList<MemoryRegion> Regions { get; private set; }

    private ProcessMemory(Process process, nint handle)
    {
        _handle = handle;
        var module = process.MainModule ?? throw new InvalidOperationException("GameClient has no main module.");
        MainModuleBase = unchecked((ulong)module.BaseAddress.ToInt64());
        MainModuleSize = unchecked((ulong)module.ModuleMemorySize);
        ProcessPath = module.FileName;
        Regions = EnumerateRegions(handle);
    }

    public void RefreshRegions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Regions = EnumerateRegions(_handle);
    }

    public static ProcessMemory Open(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            throw new InvalidOperationException($"No running process named {processName}. Start STO first.");
        }
        if (processes.Length > 1)
        {
            throw new InvalidOperationException($"More than one {processName} process is running.");
        }

        var process = processes[0];
        var access = ProcessVmRead | ProcessQueryInformation | ProcessQueryLimitedInformation;
        var handle = NativeMethods.OpenProcess(access, false, process.Id);
        if (handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"OpenProcess({process.Id}) failed. If access is denied, run this capture tool as administrator.");
        }
        return new ProcessMemory(process, handle);
    }

    public bool TryRead(ulong address, byte[] buffer, int offset, int count, out int bytesRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException();
        }

        byte[] target;
        if (offset == 0 && count == buffer.Length)
        {
            target = buffer;
        }
        else
        {
            target = new byte[count];
        }

        var ok = NativeMethods.ReadProcessMemory(
            _handle,
            unchecked((nint)(long)address),
            target,
            (nuint)count,
            out var read);
        bytesRead = checked((int)read);
        if (target != buffer && bytesRead > 0)
        {
            Buffer.BlockCopy(target, 0, buffer, offset, bytesRead);
        }
        return ok || bytesRead > 0;
    }

    private static IReadOnlyList<MemoryRegion> EnumerateRegions(nint handle)
    {
        NativeMethods.GetSystemInfo(out var systemInfo);
        var maximum = unchecked((ulong)systemInfo.MaximumApplicationAddress.ToInt64());
        var regions = new List<MemoryRegion>();
        ulong address = 0;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation64>();

        while (address < maximum)
        {
            var result = NativeMethods.VirtualQueryEx(
                handle,
                unchecked((nint)(long)address),
                out var mbi,
                mbiSize);
            if (result == 0)
            {
                break;
            }
            if (mbi.RegionSize == 0)
            {
                break;
            }

            regions.Add(new MemoryRegion(
                mbi.BaseAddress,
                mbi.RegionSize,
                mbi.State,
                mbi.Protect,
                mbi.Type));

            var next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= address)
            {
                break;
            }
            address = next;
        }
        return regions;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        NativeMethods.CloseHandle(_handle);
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public nint MinimumApplicationAddress;
        public nint MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        nint process,
        nint address,
        out MemoryBasicInformation64 buffer,
        nuint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    internal static extern void GetSystemInfo(out SystemInfo systemInfo);
}
