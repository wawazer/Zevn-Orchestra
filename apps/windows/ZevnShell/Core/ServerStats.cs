using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Info & grafik satu server lewat satu koneksi SSH (ServerMonitor) — padanan ServerStats di versi macOS.</summary>
internal sealed class ServerStats
{
    public enum Status { Connecting, Ready, Failed }

    public sealed record Point(double Cpu, double Mem, double Rx, double Tx);

    /// <summary>Jumlah titik grafik (3 detik × 60 = 3 menit).</summary>
    public const int Window = 60;

    public Host Host { get; }
    public Status State { get; private set; } = Status.Connecting;
    public string? Error { get; private set; }
    public ServerInfo? Info { get; private set; }
    public ServerSample? Latest { get; private set; }
    public List<Point> Points { get; } = [];
    public event Action? Changed;

    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;
    private ServerMonitor? _monitor;
    private (ServerSample s, DateTime t)? _prev;

    public ServerStats(Host host) { Host = host; }

    public void Start(SshTarget target)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var m = await ServerMonitor.Connect(target);
                _monitor = m;
                var info = await m.Info();
                if (cts.IsCancellationRequested) { await m.Close(); return; }
                Post(() => { Info = info; State = Status.Ready; });
                while (!cts.IsCancellationRequested)
                {
                    var s = await m.Sample();
                    Post(() => Add(s));
                    await Task.Delay(3000, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!cts.IsCancellationRequested) Post(() => { State = Status.Failed; Error = Errors.Text(e); });
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        var m = _monitor;
        _monitor = null;
        if (m != null) _ = Task.Run(async () => { try { await m.Close(); } catch { } });
    }

    public void Retry(SshTarget target)
    {
        Stop();
        State = Status.Connecting;
        Error = null;
        Changed?.Invoke();
        Start(target);
    }

    private void Post(Action a) => _dq.TryEnqueue(() => { a(); Changed?.Invoke(); });

    private void Add(ServerSample s)
    {
        var now = DateTime.UtcNow;
        double rx = 0, tx = 0;
        if (_prev is { } p && s.NetRxBytes >= p.s.NetRxBytes && s.NetTxBytes >= p.s.NetTxBytes)
        {
            var dt = Math.Max(0.5, (now - p.t).TotalSeconds);
            rx = (s.NetRxBytes - p.s.NetRxBytes) / dt;
            tx = (s.NetTxBytes - p.s.NetTxBytes) / dt;
        }
        _prev = (s, now);
        Latest = s;
        var mem = s.MemTotalBytes > 0 ? (double)s.MemUsedBytes / s.MemTotalBytes * 100 : 0;
        Points.Add(new Point(s.CpuPercent, mem, rx, tx));
        if (Points.Count > Window) Points.RemoveRange(0, Points.Count - Window);
    }
}
