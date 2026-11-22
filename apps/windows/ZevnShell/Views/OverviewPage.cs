using System;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Overview project aktif: server, tunnel, deploy, dan Live Log dalam satu halaman,
/// dengan aksi cepat yang memakai model yang sudah ada (tanpa koneksi baru).
/// </summary>
internal sealed class OverviewPage : PageBase
{
    private readonly DeployModel D = App.Model.Deploy;
    private readonly LogModel L = App.Model.LiveLogs;
    private LogModel.Status _shownLog;
    private bool _hooked;

    public OverviewPage() : base("data", "tunnels", "deploy", "sessions", "session")
    {
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        Render();
    }

    protected override void Render()
    {
        Children.Clear();
        _shownLog = L.State;
        if (M.ActiveProject is not { } p)
        {
            Children.Add(Ui.Scroll(Ui.Col(22,
                Ui.EmptyState(G.Grid, "Belum ada project aktif",
                    "Overview merangkum satu project — server, tunnel, deploy, dan log — dalam satu halaman. Aktifkan project dulu.",
                    Ui.Primary("Buka Projects", G.Apps, () => M.Navigate(Section.Projects))))));
            return;
        }

        var hosts = M.Hosts.Where(h => p.HostIds.Contains(h.Id)).ToArray();
        var forwards = M.Forwards.Where(f => p.HostIds.Contains(f.HostId)).ToArray();
        var running = forwards.Count(f => M.Tunnels.IsRunning(f.Id));
        var online = M.Sessions.Count(s => !s.IsLocal && s.State == TerminalSession.Status.Connected
            && hosts.Any(h => h.Id == s.Host.Id));

        var connectAll = Ui.Secondary("Sambung semua", G.Terminal, () => M.Connect(hosts.Select(h => h.Id)));
        connectAll.IsEnabled = hosts.Length > 0;
        var header = Ui.Header("Overview",
            $"Project “{p.Name}”  ·  {hosts.Length} server  ·  {forwards.Length} tunnel ({running} aktif)",
            connectAll, Ui.Secondary("Kelola project", G.Edit, () => M.Navigate(Section.Projects)));

        var tiles = Ui.Columns("*,*,*,*", 12,
            Tile("Server", G.Server, "#3B82F6", hosts.Length.ToString(), $"{online} sesi tersambung"),
            Tile("Tunnel", G.Forward, "#10B981", $"{running}/{forwards.Length}", running > 0 ? "sedang berjalan" : "belum ada yang aktif"),
            Tile("Deploy", G.Upload, "#06B6D4", DeployValue(), DeployDetail()),
            Tile("Live Log", G.Terminal, "#38BDF8", LogValue(), LogDetail(p)));

        var body = Ui.Col(16, header, tiles, HostsCard(hosts), DeployCard(p), TunnelsCard(forwards), LogCard(p));
        Children.Add(Ui.Scroll(body));
        for (int i = 0; i < body.Children.Count; i++) Ui.StaggerIn(body.Children[i], i);
    }

    // MARK: Server

    private UIElement HostsCard(Host[] hosts)
    {
        var list = Ui.Col(6);
        if (hosts.Length == 0)
            list.Children.Add(Ui.Secondary("Project ini belum punya server. Tambahkan host lewat Edit di halaman Projects.", 12, true));
        foreach (var h in hosts)
        {
            var connected = M.Sessions.Any(s => s.Host.Id == h.Id && s.State == TerminalSession.Status.Connected);
            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                Background = connected ? Theme.Brush(Theme.Green) : Theme.Ink(0.25)
            };
            var info = Ui.Col(2,
                Ui.Text(h.Title(), 12.5, FontWeights.SemiBold),
                Ui.Text(h.DisplayAddress(M.HideAddresses), 11, fg: Theme.Text2, mono: true));
            var row = Ui.Columns("Auto,*,Auto", 12, dot, info,
                Ui.Row(2,
                    Ui.Secondary("Terminal", G.Terminal, () => M.Connect([h.Id])),
                    Ui.IconBtn(G.Folder, "Buka SFTP", () => _ = M.OpenSftp(h))));
            list.Children.Add(Item(row));
        }
        return Card(Ui.Col(12, CardHead("Server", $"{hosts.Length} host di project ini"), list));
    }

    // MARK: Deploy

    private UIElement DeployCard(Project p)
    {
        var host = M.HostById(p.DeployHostId);
        var basePath = (p.DeployPath ?? "").Trim();
        var local = D.LocalDir(p);
        _ = D.EnsureGitAsync(p);
        var ready = host != null && basePath.Length > 0;
        var canDeploy = ready && !D.Busy && !string.IsNullOrWhiteSpace(local);

        UIElement status;
        if (D.State == DeployModel.Status.Failed)
        {
            status = Ui.Row(8, Ui.Icon(G.Warning, 13, Theme.Brush(Theme.Orange)),
                Ui.Text(D.Error ?? "Deploy gagal", 12, fg: Theme.Brush(Theme.Orange), wrap: true));
        }
        else if (D.Busy)
        {
            status = Ui.Row(8, new ProgressRing { Width = 14, Height = 14, IsActive = true },
                Ui.Text((D.Step + " — " + D.Detail).Trim(' ', '—'), 12, fg: Theme.Text2));
        }
        else
        {
            var current = D.Releases.FirstOrDefault(r => r.IsCurrent)?.Name;
            var (glyph, color, text) = !ready
                ? (G.Warning, Theme.Orange, "Host tujuan dan folder rilis belum lengkap — atur di Projects → Edit.")
                : current == null
                    ? (G.Check, Theme.Green, "Belum ada rilis aktif — siap untuk deploy pertama.")
                    : (G.Check, Theme.Green, $"Rilis aktif: {current}");
            status = Ui.Row(8, Ui.Icon(glyph, 13, Theme.Brush(color)),
                Ui.Text(text, 12, fg: Theme.Text2, wrap: true));
        }

        var deploy = Ui.Primary(D.Busy ? "Berjalan…" : "Deploy sekarang", G.Upload, async () =>
        {
            if (!await GitStatus.ConfirmDirtyAsync(D.Git)) return;
            if (host != null && M.SshTarget(host.Id) is { } t) _ = D.DeployAsync(p, t);
        });
        deploy.IsEnabled = canDeploy;

        var content = Ui.Col(10,
            CardHead("Deploy", host?.Title() ?? "host tujuan belum dipilih",
                Ui.Secondary("Halaman Deploy", G.Next, () => M.Navigate(Section.Deploy)), deploy),
            status,
            D.Git is { IsRepo: true, Changed: > 0 } dirty
                ? Ui.Row(8, Ui.Icon(G.Warning, 13, Theme.Brush(Theme.Orange)),
                    Ui.Text($"{dirty.Changed} berkas belum di-commit di folder lokal — deploy mengunggah apa adanya.",
                        11.5, fg: Theme.Brush(Theme.Orange), wrap: true))
                : null,
            Ui.Divider(),
            Meta("Server", host?.Title()),
            Meta("Folder rilis", basePath, mono: true),
            Meta("Folder lokal", local, mono: true),
            Meta("Git", GitText()),
            Meta("Restart", (p.DeployRestart ?? "").Trim(), mono: true));
        return Card(content);
    }

    // MARK: Tunnel

    private UIElement TunnelsCard(PortForward[] forwards)
    {
        var list = Ui.Col(6);
        if (forwards.Length == 0)
            list.Children.Add(Ui.Secondary("Belum ada aturan tunnel untuk host di project ini. Buat satu di halaman Port Forwarding.", 12, true));
        foreach (var f in forwards)
        {
            var host = M.HostById(f.HostId);
            var st = M.Tunnels.StatusOf(f.Id);
            var on = M.Tunnels.IsRunning(f.Id);
            var info = Ui.Col(2,
                Ui.Text(string.IsNullOrEmpty(f.Label) ? f.Kind.Short() : f.Label, 12.5, FontWeights.SemiBold),
                Ui.Text(f.Summary(host?.Title() ?? "host terhapus", M.HideAddresses), 11, fg: Theme.Text2, mono: true));
            UIElement? state = st?.State switch
            {
                TunnelManager.State.Starting => Ui.Badge("MENGHUBUNGKAN", Theme.Orange),
                TunnelManager.State.Running => Ui.Badge("AKTIF", Theme.Accent),
                TunnelManager.State.Failed => Ui.Badge("GAGAL", Theme.Red),
                _ => null,
            };
            var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) => { if (toggle.IsOn != M.Tunnels.IsRunning(f.Id)) M.ToggleTunnel(f); };
            if (st?.State == TunnelManager.State.Failed && st.Message is { Length: > 0 } msg)
                ToolTipService.SetToolTip(state, msg);
            list.Children.Add(Item(Ui.Columns("*,Auto,Auto", 10, info, state, toggle)));
        }

        var startAll = Ui.Secondary("Nyalakan semua", G.Play, () =>
        {
            foreach (var f in forwards.Where(f => !M.Tunnels.IsRunning(f.Id))) M.ToggleTunnel(f);
        });
        startAll.IsEnabled = forwards.Any(f => !M.Tunnels.IsRunning(f.Id));

        var running = forwards.Count(f => M.Tunnels.IsRunning(f.Id));
        var content = Ui.Col(12,
            CardHead("Tunnel", $"{running} dari {forwards.Length} aturan berjalan",
                Ui.Secondary("Port Forwarding", G.Next, () => M.Navigate(Section.Forwarding)), startAll),
            list);
        return Card(content);
    }

    // MARK: Live log

    private UIElement LogCard(Project p)
    {
        var host = M.HostById(p.LogHostId) ?? M.HostById(p.DeployHostId) ?? M.HostById(p.HostIds.FirstOrDefault());
        var command = (p.LogCommand ?? "").Trim();
        var (label, color) = L.State switch
        {
            LogModel.Status.Connecting => ("MENGHUBUNGKAN", Theme.Orange),
            LogModel.Status.Running => ("MENGIKUTI", Theme.Accent),
            LogModel.Status.Ended => (L.ExitCode is > 0 ? $"BERHENTI ({L.ExitCode})" : "BERHENTI", "#94A3B8"),
            LogModel.Status.Failed => ("GAGAL", Theme.Red),
            _ => ("SIAP", "#94A3B8"),
        };

        var start = Ui.Primary("Mulai", G.Play, () =>
        {
            if (host != null && M.SshTarget(host.Id) is { } t) _ = L.StartAsync(t, command);
        });
        start.IsEnabled = host != null && command.Length > 0 && !L.Busy;
        var stop = Ui.Secondary("Hentikan", G.Stop, () => _ = L.StopAsync());
        stop.IsEnabled = L.Busy;

        var info = Ui.Col(3,
            Ui.Row(8, Ui.Badge(label, color),
                Ui.Text(host?.Title() ?? "host belum dipilih", 12, fg: Theme.Text2)),
            command.Length == 0
                ? Ui.Secondary("Perintah log belum diisi — atur di Projects → Edit (journalctl, docker logs, atau tail -F).", 11.5, true)
                : Ui.Text(command, 11, fg: Theme.Text2, mono: true));
        if (L.Error is { Length: > 0 } err && L.State == LogModel.Status.Failed)
            info.Children.Add(Ui.Text(err, 11.5, fg: Theme.Brush(Theme.Orange), wrap: true));

        var content = Ui.Col(12,
            CardHead("Live Log", "Ikuti log service tanpa membuka tab terminal",
                Ui.Secondary("Panel log", G.Terminal, () => M.Navigate(Section.LiveLogs)), stop, start),
            info);
        return Card(content);
    }

    // MARK: Bagian kecil

    private string DeployValue() => D.State switch
    {
        DeployModel.Status.Busy or DeployModel.Status.Connecting => "JALAN",
        DeployModel.Status.Ready => "SIAP",
        DeployModel.Status.Failed => "GAGAL",
        _ => "—",
    };

    private string DeployDetail()
    {
        if (D.State == DeployModel.Status.Failed) return D.Error ?? "deploy gagal";
        var current = D.Releases.FirstOrDefault(r => r.IsCurrent)?.Name;
        return current ?? (M.ActiveProject is { } p && !string.IsNullOrWhiteSpace(p.DeployPath) ? "siap deploy" : "belum diatur");
    }

    private string LogValue() => L.State switch
    {
        LogModel.Status.Connecting => "JALAN",
        LogModel.Status.Running => "JALAN",
        LogModel.Status.Ended => "BERHENTI",
        LogModel.Status.Failed => "GAGAL",
        _ => "SIAP",
    };

    private string LogDetail(Project p)
    {
        var host = M.HostById(p.LogHostId) ?? M.HostById(p.DeployHostId);
        if (host != null) return host.Title();
        return string.IsNullOrWhiteSpace(p.LogCommand) ? "belum diatur" : "host belum dipilih";
    }

    /// <summary>Branch + commit terakhir folder lokal, atau alasan Git tidak terbaca.</summary>
    private string? GitText()
    {
        if (D.Git is not { } git) return null;
        if (git.Error is { } error) return error;
        return git.Subject is { Length: > 0 } subject ? $"{git.Head} · {subject}" : git.Head;
    }

    private static Border Tile(string label, string glyph, string hex, string value, string detail) => Card(Ui.Col(6,
        Ui.Row(8, Ui.TintIcon(glyph, hex, 26, 11, 7), Ui.Caps(label)),
        Ui.Text(value, 20, FontWeights.SemiBold),
        Ui.Secondary(detail, 11)), 13);

    private static UIElement CardHead(string title, string subtitle, params UIElement?[] actions)
    {
        var left = Ui.Col(2,
            Ui.Text(title, 13.5, FontWeights.SemiBold),
            Ui.Secondary(subtitle, 11.5, true));
        var right = Ui.Row(6, actions);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.Columns("*,Auto", 12, left, right);
    }

    private static UIElement Meta(string label, string? value, bool mono = false) =>
        Ui.Columns("86,*", 8,
            Ui.Text(label, 11.5, fg: Theme.Text3),
            value is { Length: > 0 }
                ? Ui.Text(value, 11.5, fg: Theme.Text2, mono: mono)
                : Ui.Text("belum diatur", 11.5, fg: Theme.Brush(Theme.Orange)));

    private static Border Item(UIElement content) => new()
    {
        Child = new Border { Padding = new Thickness(10, 8, 10, 8), Child = content },
        Background = Theme.Ink(0.02), BorderBrush = Theme.CardStroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9)
    };

    private static Border Card(UIElement content, double pad = 14) => new()
    {
        Child = new Border { Padding = new Thickness(pad), Child = content },
        Background = Theme.Card, BorderBrush = Theme.CardStroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
    };

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) L.Changed += OnLog; else L.Changed -= OnLog;
    }

    /// <summary>Log mengalir tiap ~120 ms: gambar ulang hanya saat statusnya berubah.</summary>
    private void OnLog() { if (L.State != _shownLog) Render(); }
}
