using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ZevnShell;

/// <summary>Proses di dalam pseudo console Windows (ConPTY) — padanan LocalProcess di SwiftTerm.</summary>
internal sealed class PtyProcess : IDisposable
{
    public event Action<string>? Output;
    public event Action<int>? Exited;

    private IntPtr _hpc, _process, _thread, _attrList;
    private FileStream? _input;
    private SafeFileHandle? _outRead;
    private bool _disposed;
    private readonly object _lock = new();

    public static PtyProcess Start(string commandLine, IDictionary<string, string> env, int cols, int rows, string? workingDir = null)
    {
        var p = new PtyProcess();
        p.Launch(commandLine, env, cols, rows, workingDir);
        return p;
    }

    private void Launch(string commandLine, IDictionary<string, string> env, int cols, int rows, string? workingDir)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        var hr = CreatePseudoConsole(new COORD((short)Math.Max(cols, 20), (short)Math.Max(rows, 5)), inRead, outWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr);
        // Ujung pipe milik console sudah diduplikasi oleh ConPTY
        inRead.Dispose();
        outWrite.Dispose();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES; // jangan warisi handle std milik app
        si.lpAttributeList = _attrList;

        // Blok environment: "k=v\0...\0\0", diurutkan seperti yang diharapkan Windows
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        var envBlock = Marshal.StringToHGlobalUni(sb.ToString());
        try
        {
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock, workingDir, ref si, out var pi))
                throw new Win32Exception();
            _process = pi.hProcess;
            _thread = pi.hThread;
        }
        finally { Marshal.FreeHGlobal(envBlock); }

        _input = new FileStream(inWrite, FileAccess.Write, 1);
        _outRead = outRead;
        new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    private void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var buf = new byte[32 * 1024];
        var chars = new char[32 * 1024 + 4];
        using var s = new FileStream(_outRead!, FileAccess.Read, 1);
        try
        {
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                int c = decoder.GetChars(buf, 0, n, chars, 0, false);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch { }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out var code);
        // Tutup console agar pipe output selesai (dilakukan di thread ini, bisa memblok sebentar)
        lock (_lock)
        {
            if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
        }
        Exited?.Invoke((int)code);
    }

    public void Write(string text)
    {
        if (_input == null || _disposed) return;
        try
        {
            var b = Encoding.UTF8.GetBytes(text);
            _input.Write(b, 0, b.Length);
            _input.Flush();
        }
        catch { }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            if (_hpc != IntPtr.Zero && cols > 0 && rows > 0) ResizePseudoConsole(_hpc, new COORD((short)cols, (short)rows));
        }
    }

    public void Kill()
    {
        if (_process != IntPtr.Zero) TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        try { _input?.Dispose(); } catch { }
        lock (_lock)
        {
            if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
        }
        if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero; }
        if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
    }

    // MARK: Win32

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; public COORD(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
