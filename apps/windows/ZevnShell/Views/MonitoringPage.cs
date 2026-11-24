using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Monitoring semua server project aktif: satu koneksi SSH per host (ServerMonitor),
/// sampel tiap 3 detik selama halaman terbuka. Tidak ada API Rust baru.
/// </summary>
internal sealed class MonitoringPage : PageBase
{
    private readonly Dictionary<string, ServerStats> _stats = [];
    private readonly ScrollViewer _scroll = Ui.Scroll(new StackPanel(), 20);
    private string _shownIds = "";
    private bool _live;

    public MonitoringPage() : base("data")
    {
        Children.Add(_scroll);
        Loaded += (_, _) => { _live = true; Ensure(); Render(); };
        Unloaded += (_, _) => StopAll();
        Render();
    }

    protected override void OnModelChanged(string what)
    {
        if (what == "data") Ensure();
        Render();
    }

    private Host[] Hosts => M.ProjectHosts;

    /// <summary>Mulai pemantauan host yang belum punya koneksi; hentikan yang sudah tidak ada.</summary>
    private void Ensure()
    {
        if (!_live) return;
        var hosts = Hosts;
        foreach (var id in _stats.Keys.ToList())
        {
            if (hosts.Any(h => h.Id == id)) continue;
            _stats[id].Changed -= OnStats;
            _stats[id].Stop();
            _stats.Remove(id);
        }
        var demo = Environment.GetEnvironmentVariable("ZEVN_DEMO_MONITOR") == "1";
        foreach (var h in hosts)
        {
            if (_stats.ContainsKey(h.Id)) continue;
            var st = new ServerStats(h);
            st.Changed += OnStats;
            _stats[h.Id] = st;
#if DEBUG
            if (demo) { st.StartDemo(); continue; }
#endif
            if (M.SshTarget(h.Id) is { } t) st.Start(t);
        }
    }

    private void StopAll()
    {
        foreach (var st in _stats.Values) { st.Changed -= OnStats; st.Stop(); }
        _stats.Clear();
        _live = false;
    }

    private void OnStats() => Render();

    private void Restart()
    {
        foreach (var st in _stats.Values)
            if (M.SshTarget(st.Host.Id) is { } t) st.Retry(t);
    }

    protected override void Render()
    {
        // Konten diganti tiap sampel: posisi scroll dipertahankan supaya tidak melompat ke atas.
        var offset = _scroll.VerticalOffset;
        var hosts = Hosts;
        var ready = hosts.Count(h => _stats.TryGetValue(h.Id, out var s) && s.State == ServerStats.Status.Ready);
        var failed = hosts.Count(h => _stats.TryGetValue(h.Id, out var s) && s.State == ServerStats.Status.Failed);
        var subtitle = hosts.Length == 0
            ? "Aktifkan project atau tambahkan host untuk mulai memantau."
            : $"{hosts.Length} server di project aktif · {ready} live{(failed > 0 ? $" · {failed} gagal" : "")} · sampel tiap 3 detik";

        var refresh = Ui.Secondary("Segarkan", G.Refresh, Restart);
        refresh.IsEnabled = hosts.Length > 0;
        var header = Ui.Header("Monitoring", subtitle, refresh);

        if (hosts.Length == 0)
        {
            _shownIds = "";
            _scroll.Content = Ui.Col(22, header,
                Ui.EmptyState(G.Heart, "Belum ada server",
                    "Monitoring membaca CPU, RAM, disk, dan jaringan tiap server lewat satu koneksi SSH. Tambahkan host ke project dulu.",
                    Ui.Primary("Buka Projects", G.Apps, () => M.Navigate(Section.Projects))));
            return;
        }

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < hosts.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = Card(hosts[i]);
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            grid.Children.Add(card);
        }

        _scroll.Content = Ui.Col(16, header, grid);
        _scroll.UpdateLayout();
        if (offset > 0) _scroll.ChangeView(null, offset, null, true);

        // Animasi masuk hanya saat daftar server berubah, bukan tiap sampel.
        var ids = string.Join(",", hosts.Select(h => h.Id));
        if (ids == _shownIds) return;
        _shownIds = ids;
        for (int i = 0; i < grid.Children.Count; i++)
            if (grid.Children[i] is FrameworkElement card) Ui.StaggerIn(card, Math.Min(i, 6));
    }

    private Border Card(Host h)
    {
        _stats.TryGetValue(h.Id, out var st);
        var state = st?.State ?? ServerStats.Status.Connecting;
        var info = st?.Info;
        var sample = st?.Latest;

        UIElement badge = state switch
        {
            ServerStats.Status.Ready => Ui.Badge("LIVE", Theme.Accent),
            ServerStats.Status.Failed => Ui.Badge("GAGAL", Theme.Red),
            _ => Ui.Badge("MENGHUBUNGKAN", Theme.Orange),
        };
        var actions = Ui.Row(6, badge);
        if (state == ServerStats.Status.Failed && st != null)
            actions.Children.Add(Ui.IconBtn(G.Refresh, "Coba lagi", () =>
            {
                if (M.SshTarget(h.Id) is { } t) st.Retry(t);
            }));

        var head = Ui.Columns("Auto,*,Auto", 10,
            Ui.TintIcon(G.Server, h.Color, 30, 12, 8),
            Ui.Col(1,
                Ui.Text(info is { Hostname.Length: > 0 } ? info.Hostname : h.Title(), 13, FontWeights.SemiBold)
                    .With(t => t.TextTrimming = TextTrimming.CharacterEllipsis),
                Ui.Text(h.DisplayAddress(M.HideAddresses), 10.5, fg: Theme.Text2, mono: true)),
            actions);

        var mem = sample is { MemTotalBytes: > 0 } ? (double)sample.MemUsedBytes / sample.MemTotalBytes * 100 : 0;
        var disk = info?.Disks.FirstOrDefault(d => d.Mount == "/") ?? info?.Disks.FirstOrDefault();
        var diskPct = disk is { TotalBytes: > 0 } ? (double)disk.UsedBytes / disk.TotalBytes * 100 : 0;
        var net = st?.Points.LastOrDefault();

        var metrics = Ui.Columns("*,*,*,*", 10,
            Metric("CPU", sample != null ? $"{sample.CpuPercent:0}%" : "—",
                sample != null ? $"{sample.Load1:0.00} load" : null, sample?.CpuPercent > 85 ? Theme.Red : Theme.Accent),
            Metric("RAM", sample != null ? $"{mem:0}%" : "—",
                sample != null ? $"{Ui.Bytes(sample.MemUsedBytes)} / {Ui.Bytes(sample.MemTotalBytes)}" : null, mem > 90 ? Theme.Red : Theme.Accent3),
            Metric("Disk", disk != null ? $"{diskPct:0}%" : "—",
                disk != null ? $"{disk.Mount} · {Ui.Bytes(disk.UsedBytes)} / {Ui.Bytes(disk.TotalBytes)}" : null, diskPct > 90 ? Theme.Red : Theme.Orange),
            Metric("Jaringan", net != null ? $"↓ {Rate(net.Rx)}" : "—",
                net != null ? $"↑ {Rate(net.Tx)}" : null, Theme.Accent2, 14));

        var content = Ui.Col(10, head, metrics);
        if (state == ServerStats.Status.Ready)
        {
            content.Children.Add(Ui.Caps("CPU · 3 menit"));
            content.Children.Add(Spark(st?.Points.Select(p => (double)p.Cpu).ToList() ?? [], Theme.Accent));
            if (info != null)
                content.Children.Add(Ui.Text(
                    $"{info.Os} · {info.CpuCores} core · uptime {Uptime(info.UptimeSecs)} · load {info.Load}",
                    10.5, fg: Theme.Text3).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
        }
        else if (state == ServerStats.Status.Connecting)
        {
            content.Children.Add(Ui.Row(8, new ProgressRing { Width = 14, Height = 14, IsActive = true },
                Ui.Secondary("Membaca info server…", 11)));
        }
        else
        {
            content.Children.Add(Ui.Text(st?.Error ?? "Tidak bisa membaca info server.", 11,
                fg: Theme.Brush(Theme.Orange), wrap: true));
        }

        return Ui.Card(content, 14, 12);
    }

    private static UIElement Metric(string label, string value, string? sub, string hex, double size = 16) => Ui.Col(3,
        Ui.Caps(label),
        Ui.Text(value, size, FontWeights.SemiBold, Theme.Brush(hex)).With(t => t.FontFamily = Theme.MonoFont),
        Ui.Text(sub ?? "—", 10, fg: Theme.Text2).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));

    /// <summary>Garis CPU ringkas (60 sampel terakhir) yang mengikuti lebar kartu.</summary>
    private static Canvas Spark(List<double> values, string color)
    {
        var canvas = new Canvas { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        canvas.SizeChanged += (_, e) => DrawSpark(canvas, values, color, e.NewSize.Width, e.NewSize.Height);
        return canvas;
    }

    private static void DrawSpark(Canvas canvas, List<double> values, string color, double w, double h)
    {
        canvas.Children.Clear();
        if (values.Count == 0 || w < 4 || h < 4) return;
        var step = w / (ServerStats.Window - 1);
        var x0 = w - (values.Count - 1) * step;
        var line = new Polyline { Stroke = Theme.Brush(color), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        var area = new Polygon { Fill = Theme.Brush(color, 0.12) };
        area.Points.Add(new Windows.Foundation.Point(x0, h));
        for (int i = 0; i < values.Count; i++)
        {
            var p = new Windows.Foundation.Point(x0 + i * step, h - Math.Clamp(values[i] / 100, 0, 1) * (h - 2));
            line.Points.Add(p);
            area.Points.Add(p);
        }
        area.Points.Add(new Windows.Foundation.Point(w, h));
        canvas.Children.Add(area);
        canvas.Children.Add(line);
    }

    private static string Rate(double bps) => bps < 1 ? "0 B/s" : Ui.Bytes((ulong)bps) + "/s";

    private static string Uptime(ulong? secs)
    {
        if (secs is not { } s) return "—";
        ulong d = s / 86400, hh = s % 86400 / 3600, m = s % 3600 / 60;
        return d > 0 ? $"{d} hari {hh} jam" : hh > 0 ? $"{hh} jam {m} menit" : $"{m} menit";
    }
}
