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

/// <summary>Panel kanan di area terminal: spesifikasi server + grafik live CPU, RAM, jaringan.</summary>
internal sealed class ServerInfoPanel : Grid
{
    private readonly AppModel M = App.Model;
    private readonly ServerStats _stats;
    private readonly Action _onChange;

    public ServerInfoPanel(ServerStats stats)
    {
        _stats = stats;
        Width = 340;
        Background = Theme.Panel;
        BorderBrush = Theme.Ink(0.07);
        BorderThickness = new Thickness(1, 0, 0, 0);
        _onChange = Render;
        _stats.Changed += _onChange;
        Unloaded += (_, _) => _stats.Changed -= _onChange;
        Render();
    }

    private void Render()
    {
        var head = Ui.Columns("Auto,*,Auto", 10,
            Ui.TintIcon("", _stats.Host.Color, 32, 13, 9),
            Ui.Col(1,
                Ui.Text(_stats.Info is { Hostname.Length: > 0 } i0 ? i0.Hostname : _stats.Host.Title(), 14, FontWeights.SemiBold).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis),
                Ui.Text(_stats.State == ServerStats.Status.Ready ? "● Live · tiap 3 detik" : "Info server", 11, fg: _stats.State == ServerStats.Status.Ready ? Theme.Brush(Theme.Green) : Theme.Text2)),
            Ui.IconBtn(G.Close, "Tutup panel (Ctrl+Shift+I)", () => M.ToggleServerInfo(false), size: 10));
        head.Padding = new Thickness(14, 12, 12, 12);

        UIElement body = _stats.State switch
        {
            ServerStats.Status.Connecting => Ui.Col(10, new ProgressRing { IsActive = true, Width = 22, Height = 22 }, Ui.Secondary("Membaca info server…", 12.5))
                .With(c => { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; }),
            ServerStats.Status.Failed => Ui.Col(10, Ui.Icon(G.Warning, 22, Theme.Brush(Theme.Orange)), Ui.Secondary(_stats.Error ?? "", 12.5, true).With(t => t.TextAlignment = TextAlignment.Center),
                    Ui.Secondary("Coba lagi", G.Refresh, M.RetryServerStats))
                .With(c => { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; c.Padding = new Thickness(20); }),
            _ => Ui.Scroll(Content(_stats.Info!), 14),
        };
        var sep = new Border { Height = 1, Background = Theme.Ink(0.07) };
        Children.Clear();
        Children.Add(Ui.Rows("Auto,Auto,*", 0, head, sep, body));
    }

    private FrameworkElement Content(ServerInfo i)
    {
        var col = Ui.Col(12);
        // Spesifikasi
        var spec = Ui.Col(0);
        void Row(string glyph, string label, string value, bool last = false)
        {
            var r = Ui.Columns("20,58,*", 8, Ui.Icon(glyph, 12, Theme.Text2), Ui.Text(label, 12, fg: Theme.Text2),
                Ui.Text(value, 12, FontWeights.Medium).With(t => { t.TextWrapping = TextWrapping.Wrap; t.IsTextSelectionEnabled = true; }));
            r.Padding = new Thickness(12, 9, 12, 9);
            spec.Children.Add(r);
            if (!last) spec.Children.Add(new Border { Height = 1, Background = Theme.Ink(0.06), Margin = new Thickness(40, 0, 0, 0) });
        }
        Row(G.Apps, "OS", i.Os.Length > 0 ? i.Os : "—");
        Row(G.Structure, "Kernel", i.Kernel);
        Row("", "CPU", CpuText(i));
        Row(G.Clock, "Uptime", i.UptimeSecs is { } u ? Uptime(u) : "—", i.Load.Length == 0);
        if (i.Load.Length > 0) Row(G.Bolt, "Load", i.Load, true);
        col.Children.Add(Card(spec, 0));

        // Grafik live
        var pts = _stats.Points;
        var l = _stats.Latest;
        col.Children.Add(Chart("CPU", l != null ? $"{l.CpuPercent:0}%" : "—", Theme.Accent, [pts.Select(p => p.Cpu).ToList()], 100));
        col.Children.Add(Chart("RAM", l != null ? $"{Ui.Bytes(l.MemUsedBytes)} / {Ui.Bytes(l.MemTotalBytes)}" : "—", Theme.Accent3, [pts.Select(p => p.Mem).ToList()], 100));
        var last = pts.LastOrDefault();
        col.Children.Add(Chart("Jaringan", last != null ? $"↓ {Rate(last.Rx)}  ↑ {Rate(last.Tx)}" : "—", Theme.Accent2,
            [pts.Select(p => p.Rx).ToList(), pts.Select(p => p.Tx).ToList()], null, Theme.Orange));

        // Memori & storage
        var use = Ui.Col(12);
        use.Children.Add(Usage("RAM", l?.MemUsedBytes ?? i.MemUsedBytes, l?.MemTotalBytes ?? i.MemTotalBytes, Theme.Accent3));
        if (i.SwapTotalBytes > 0) use.Children.Add(Usage("Swap", i.SwapUsedBytes, i.SwapTotalBytes, "#94A3B8"));
        foreach (var d in i.Disks) use.Children.Add(Usage(d.Mount == "/" ? "Storage (/)" : d.Mount, d.UsedBytes, d.TotalBytes, Theme.Orange));
        col.Children.Add(Card(use, 12));
        return col;
    }

    private static Border Card(UIElement child, double pad) => new()
    {
        Child = child, Padding = new Thickness(pad), CornerRadius = new CornerRadius(12), Background = Theme.Ink(0.04),
    };

    /// <summary>Kartu grafik garis (Polyline) dengan area tipis di bawahnya.</summary>
    private static Border Chart(string title, string value, string color, List<List<double>> series, double? max, string? color2 = null)
    {
        const double W = 286, H = 64;
        var canvas = new Canvas { Width = W, Height = H };
        for (int g = 1; g <= 2; g++)
            canvas.Children.Add(new Line { X1 = 0, X2 = W, Y1 = H * g / 3, Y2 = H * g / 3, Stroke = Theme.Ink(0.07), StrokeThickness = 1 });
        var top = max ?? Math.Max(1, series.SelectMany(s => s).DefaultIfEmpty(0).Max() * 1.15);
        for (int si = 0; si < series.Count; si++)
        {
            var s = series[si];
            if (s.Count < 1) continue;
            var stroke = Theme.Brush(si == 0 ? color : color2 ?? color);
            var step = W / (ServerStats.Window - 1);
            var x0 = W - (s.Count - 1) * step;
            var line = new Polyline { Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var area = new Polygon { Fill = Theme.Brush(si == 0 ? color : color2 ?? color, 0.14) };
            area.Points.Add(new Windows.Foundation.Point(x0, H));
            for (int k = 0; k < s.Count; k++)
            {
                var p = new Windows.Foundation.Point(x0 + k * step, H - Math.Clamp(s[k] / top, 0, 1) * (H - 4));
                line.Points.Add(p);
                area.Points.Add(p);
            }
            area.Points.Add(new Windows.Foundation.Point(W, H));
            if (series.Count == 1) canvas.Children.Add(area);
            canvas.Children.Add(line);
        }
        var head = Ui.Columns("Auto,*,Auto", 7,
            new Ellipse { Width = 7, Height = 7, Fill = Theme.Brush(color) },
            Ui.Text(title, 12, FontWeights.SemiBold),
            Ui.Text(value, 11.5, fg: Theme.Text2).With(t => t.FontFamily = Theme.MonoFont));
        return Card(Ui.Col(8, head, canvas), 12);
    }

    private static FrameworkElement Usage(string label, ulong used, ulong total, string color)
    {
        var f = total > 0 ? Math.Min(1, (double)used / total) : 0;
        var tint = f > 0.9 ? Theme.Red : color;
        var track = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Ink(0.08) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, f), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star) });
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Theme.Brush(tint) });
        var head = Ui.Columns("*,Auto", 8,
            Ui.Text(label, 12, FontWeights.Medium).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis),
            Ui.Text($"{Ui.Bytes(used)} / {Ui.Bytes(total)}", 11, fg: Theme.Text2).With(t => t.FontFamily = Theme.MonoFont));
        return Ui.Col(5, head, track);
    }

    private static string CpuText(ServerInfo i)
    {
        var m = i.CpuModel.Length == 0 ? i.Arch : $"{i.CpuModel} · {i.Arch}";
        return i.CpuCores > 0 ? $"{m} · {i.CpuCores} core" : m;
    }

    private static string Rate(double bps) => bps < 1 ? "0 B/s" : Ui.Bytes((ulong)bps) + "/s";

    private static string Uptime(ulong s)
    {
        ulong d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60;
        return d > 0 ? $"{d} hari {h} jam" : h > 0 ? $"{h} jam {m} menit" : $"{m} menit";
    }
}
