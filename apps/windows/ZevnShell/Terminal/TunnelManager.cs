using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Menjalankan aturan port forwarding sebagai proses `ssh -N` di latar belakang.</summary>
internal sealed class TunnelManager
{
    public enum State { Starting, Running, Failed }
    public sealed record Status(State State, string? Message = null);

    private readonly Dictionary<string, Status> _states = [];
    private readonly Dictionary<string, (Process p, SshCommand cmd, List<string> err)> _procs = [];
    public event Action? Changed;

    public Status? StatusOf(string id) => _states.TryGetValue(id, out var s) ? s : null;
    public bool IsRunning(string id) => _states.TryGetValue(id, out var s) && s.State != State.Failed;
    public int RunningCount => _states.Values.Count(s => s.State != State.Failed);

    public void Start(PortForward f, Host host, SshKey? key, Host? jump)
    {
        string[] spec = f.Kind switch
        {
            ForwardKind.Local => new[] { "-L", $"{f.BindAddress}:{f.BindPort}:{f.DestHost}:{f.DestPort}" },
            ForwardKind.Remote => new[] { "-R", $"{f.BindAddress}:{f.BindPort}:{f.DestHost}:{f.DestPort}" },
            _ => new[] { "-D", $"{f.BindAddress}:{f.BindPort}" },
        };
        var cmd = new SshCommand(host, key, jump, new[] { "-N", "-o", "ExitOnForwardFailure=yes", "-o", "BatchMode=no" }.Concat(spec));
        var psi = new ProcessStartInfo(SshCommand.SshExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, RedirectStandardInput = true
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var kv in cmd.Env) psi.Environment[kv.Key] = kv.Value;

        var dq = DispatcherQueue.GetForCurrentThread();
        var err = new List<string>();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var id = f.Id;
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (err) err.Add(e.Data.Trim()); };
        p.Exited += (_, _) => dq.TryEnqueue(() => Ended(id, p));
        try
        {
            p.Start();
            p.BeginErrorReadLine();
            _procs[id] = (p, cmd, err);
            _states[id] = new Status(State.Starting);
            Changed?.Invoke();
            // Jika setelah 2,5 detik proses masih hidup, tunnel dianggap aktif.
            _ = Task.Delay(2500).ContinueWith(_ => dq.TryEnqueue(() =>
            {
                if (_procs.TryGetValue(id, out var x) && x.p == p && !p.HasExited) { _states[id] = new Status(State.Running); Changed?.Invoke(); }
            }));
        }
        catch (Exception e)
        {
            cmd.Cleanup();
            _states[id] = new Status(State.Failed, e.Message);
            Changed?.Invoke();
        }
    }

    public void Stop(string id)
    {
        if (_procs.Remove(id, out var x))
        {
            try { if (!x.p.HasExited) x.p.Kill(true); } catch { }
            x.cmd.Cleanup();
        }
        _states.Remove(id);
        Changed?.Invoke();
    }

    public void StopAll() { foreach (var id in _procs.Keys.ToList()) Stop(id); }

    private void Ended(string id, Process p)
    {
        if (!_procs.TryGetValue(id, out var x) || x.p != p) return; // dihentikan manual
        _procs.Remove(id);
        x.cmd.Cleanup();
        string msg;
        lock (x.err) msg = x.err.LastOrDefault() ?? "";
        _states[id] = new Status(State.Failed, msg.Length == 0 ? $"ssh keluar dengan kode {p.ExitCode}" : msg);
        Changed?.Invoke();
    }
}
