using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PS7Studio.Terminal;

/// <summary>Owns a Windows pseudoconsole and an isolated kill-on-close process job.
/// Streams are overlapped named pipes, with bounded OS buffering and native asynchronous I/O.
/// Consumers must continuously read Output while the session runs; VT output is UTF-8.</summary>
public sealed class PtyProcess : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Process process;
    private readonly SafeFileHandle job;
    private nint console;
    private Task? disposal;
    private readonly Task exit;
    private int? exitCode;
    public Stream Input { get; }
    public Stream Output { get; }
    public int ProcessId { get; }
    public bool HasExited => exit.IsCompleted;
    public int? ExitCode => exit.IsCompleted ? exitCode : null;

    private PtyProcess(Process process, SafeFileHandle job, nint console, Stream input, Stream output)
    {
        this.process = process; this.job = job; this.console = console;
        Input = input; Output = output; ProcessId = process.Id;
        exit = ObserveExitAsync();
    }
    private async Task ObserveExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        exitCode = process.ExitCode;
    }
    public Task WaitForExitAsync(CancellationToken token = default) => exit.WaitAsync(token);

    public static PtyProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, int columns = 120, int rows = 30)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var size = Size(columns, rows);
        NamedPipeServerStream? input = null, output = null;
        NamedPipeClientStream? consoleInput = null, consoleOutput = null;
        SafeFileHandle? job = null;
        nint pc = 0, attributes = 0;
        bool attributesInitialized = false;
        PROCESS_INFORMATION pi = default;
        try
        {
            (input, consoleInput) = Pipe(PipeDirection.Out);
            (output, consoleOutput) = Pipe(PipeDirection.In);
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(size, consoleInput.SafePipeHandle, consoleOutput.SafePipeHandle, 0, out pc));
            consoleInput.Dispose(); consoleInput = null;
            consoleOutput.Dispose(); consoleOutput = null;
            job = CreateJobObjectW(0, null);
            Check(!job.IsInvalid);
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
            nuint bytes = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesInitialized = true;
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x20016, pc, (nuint)IntPtr.Size, 0, 0));
            var startup = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>(), Flags = 0x100 }, AttributeList = attributes };
            var commandLine = new StringBuilder(Quote(executable));
            foreach (var argument in arguments) { ArgumentNullException.ThrowIfNull(argument); commandLine.Append(' ').Append(Quote(argument)); }
            // Suspended creation guarantees that even immediately spawned descendants belong to our job.
            Check(CreateProcessW(executable, commandLine, 0, 0, false, 0x80000 | 0x4 | 0x400, 0, workingDirectory, ref startup, out pi));
            Check(AssignProcessToJobObject(job, pi.Process));
            var process = Process.GetProcessById((int)pi.ProcessId);
            try
            {
                if (ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new PtyProcess(process, job, pc, input, output);
                job = null; pc = 0; input = null; output = null;
                return result;
            }
            catch { process.Dispose(); throw; }
        }
        catch
        {
            if (pi.Process != 0) { TerminateProcess(pi.Process, 1); WaitForSingleObject(pi.Process, 5000); }
            throw;
        }
        finally
        {
            if (pi.Thread != 0) CloseHandle(pi.Thread);
            if (pi.Process != 0) CloseHandle(pi.Process);
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            job?.Dispose(); consoleInput?.Dispose(); consoleOutput?.Dispose();
            input?.Dispose(); output?.Dispose();
            if (pc != 0) ClosePseudoConsole(pc);
        }
    }

    private static (NamedPipeServerStream, NamedPipeClientStream) Pipe(PipeDirection direction)
    {
        var name = "PS7Studio.ConPTY." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        NamedPipeClientStream? client = null;
        try
        {
            var connection = server.WaitForConnectionAsync();
            client = new NamedPipeClientStream(".", name, direction == PipeDirection.In ? PipeDirection.Out : PipeDirection.In, PipeOptions.None);
            client.Connect(5000);
            connection.GetAwaiter().GetResult();
            return (server, client);
        }
        catch { client?.Dispose(); server.Dispose(); throw; }
    }
    public void Resize(int columns, int rows)
    {
        var size = Size(columns, rows);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal != null, this);
            Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, size));
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new ValueTask(disposal ??= Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        // Closing the job terminates only processes assigned by this owner, including descendants.
        job.Dispose();
        // Close pipe endpoints before ClosePseudoConsole: this cancels pending overlapped reads/writes
        // and prevents console shutdown from waiting indefinitely on an undrained output pipe.
        Input.Dispose(); Output.Dispose();
        var handle = Interlocked.Exchange(ref console, 0);
        if (handle != 0) ClosePseudoConsole(handle);
        try { await exit.ConfigureAwait(false); }
        finally { process.Dispose(); }
    }
    private static COORD Size(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
        return new COORD((short)columns, (short)rows);
    }
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Command line arguments cannot contain NUL.");
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct COORD(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO
    {
        public int cb; public nint Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public nint Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafePipeHandle input, SafePipeHandle output, uint flags, out nint console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(nint console, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}

