using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Panel log per project: jalankan perintah yang mengikuti log service di server
/// (journalctl, docker logs, tail -F) dan tampilkan keluarannya langsung.
/// </summary>
internal sealed class LiveLogsPage : PageBase
{
    private readonly LogModel L = App.Model.LiveLogs;
    private readonly ComboBox _host = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Host" };
    private readonly TextBox _command = Ui.Field("journalctl -u toko -f -n 200 --no-pager", mono: true);
    private readonly TextBlock _log;
    private readonly ScrollViewer _logScroll;
    private readonly Border _statusHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _follow = new() { IsOn = true, OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly Button _start;
    private readonly Button _stop;
    private readonly StackPanel _body;
    private readonly UIElement _empty;
    private string? _shownProjectId;
    private bool _hooked;

    public LiveLogsPage() : base("data")
    {
        _command.Header = "Perintah";
        _start = Ui.Primary("Mulai", G.Play, () => _ = Start());
        _stop = Ui.Secondary("Hentikan", G.Stop, () => _ = Stop());
        var clear = Ui.Secondary("Bersihkan", G.Delete, () => L.Clear());

        _log = new TextBlock
        {
            FontFamily = Theme.MonoFont, FontSize = 11.5, Foreground = Theme.Text,
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Text = ""
        };
        _logScroll = new ScrollViewer
        {
            Content = _log, Height = 380, Padding = new Thickness(12),
            Background = Theme.Ink(0.04), CornerRadius = new CornerRadius(8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var header = Ui.Header("Live Logs",
            "Ikuti log service di server untuk project aktif — journalctl, docker logs, atau tail -F.",
            clear, _stop, _start);

        var quick = Ui.Row(7,
            Ui.Caps("Cepat"),
            Preset("systemd", "journalctl -u toko -f -n 200 --no-pager"),
            Preset("docker", "docker logs -f --tail 200 toko"),
            Preset("berkas", "tail -n 200 -F /var/log/toko.log"));

        var target = Ui.Card(Ui.Col(10,
            Ui.Columns("220,*", 10, _host, _command),
            quick,
            Ui.Secondary("Perintah dijalankan di server dengan akun host ini. Host dan perintah disimpan di project, jadi ikut tersinkron ke perangkat lain.", 11.5, true)), 14);

        var logHead = Ui.Columns("Auto,*,Auto,Auto", 10,
            Ui.Caps("Keluaran"), _statusHost,
            Ui.Text("Ikuti", 11.5, fg: Theme.Text2), _follow);
        var logCard = Ui.Card(Ui.Col(8, logHead, Ui.Divider(), _logScroll), 12);

        _body = Ui.Col(18, header, target, logCard);
        _empty = Ui.Scroll(Ui.Col(22,
            Ui.EmptyState(G.Terminal, "Belum ada project aktif",
                "Live Logs mengikuti log service milik satu project. Aktifkan project dulu, lalu pilih host dan perintah lognya.",
                Ui.Primary("Buka Projects", G.Apps, () => M.Navigate(Section.Projects)))));
        _empty.Visibility = Visibility.Collapsed;

        Children.Add(_body);
        Children.Add(_empty);

        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        Render();
    }

    protected override void Render()
    {
        var p = M.ActiveProject;
        if (p == null && _shownProjectId != null)
        {
            var busy = L.Busy;
            _shownProjectId = null;
            if (busy) { _ = L.StopAsync(); L.Clear(); }
        }
        _empty.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p != null && p.Id != _shownProjectId)
        {
            // Log milik project lama tidak boleh terus berjalan di konteks baru.
            var switched = _shownProjectId != null;
            _shownProjectId = p.Id;
            if (switched && L.Busy) { _ = L.StopAsync(); L.Clear(); }
            FillHosts(p);
            _command.Text = p.LogCommand ?? "";
        }
        UpdateStatus();
        if (_log.Text != L.Text)
        {
            _log.Text = L.Text;
            _log.Foreground = L.Text.Length == 0 ? Theme.Text3 : Theme.Text;
            if (_follow.IsOn && L.State == LogModel.Status.Running)
                _logScroll.ChangeView(null, double.MaxValue, null, true);
        }
        else if (L.Text.Length == 0)
        {
            _log.Text = "Menunggu keluaran…";
            _log.Foreground = Theme.Text3;
        }
    }

    private void FillHosts(Project p)
    {
        var pick = p.LogHostId ?? p.DeployHostId ?? p.HostIds.FirstOrDefault();
        _host.Items.Clear();
        foreach (var h in M.Hosts.Where(h => p.HostIds.Contains(h.Id)))
            _host.Items.Add(new ComboBoxItem { Content = h.Title(), Tag = h.Id });
        _host.SelectedItem = _host.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == pick)
            ?? (_host.Items.Count > 0 ? _host.Items[0] : null);
        _host.IsEnabled = _host.Items.Count > 0;
    }

    private void UpdateStatus()
    {
        (string text, string color) = L.State switch
        {
            LogModel.Status.Connecting => ("Menghubungkan…", Theme.Orange),
            LogModel.Status.Running => ("Mengikuti", Theme.Accent),
            LogModel.Status.Ended => (L.ExitCode is > 0 ? $"Berhenti ({L.ExitCode})" : "Berhenti", "#94A3B8"),
            LogModel.Status.Failed => ("Gagal", Theme.Red),
            _ => ("Siap", "#94A3B8"),
        };
        _statusHost.Child = Ui.Badge(text, color);
        ToolTipService.SetToolTip(_statusHost, L.Error ?? text);
        _start.IsEnabled = !L.Busy && M.ActiveProject != null;
        _stop.IsEnabled = L.Busy;
    }

    private async Task Start()
    {
        var p = M.ActiveProject;
        if (p == null) return;
        if ((_host.SelectedItem as ComboBoxItem)?.Tag is not string hostId)
        {
            M.Flash("Pilih host dulu");
            return;
        }
        if (M.SshTarget(hostId) is not { } target)
        {
            M.Flash("Host untuk log tidak ditemukan");
            return;
        }
        var command = _command.Text.Trim();
        if (command.Length == 0)
        {
            M.Flash("Perintah log belum diisi");
            return;
        }
        M.Save(p with { LogHostId = hostId, LogCommand = command });
        _shownProjectId = p.Id;
        await L.StartAsync(target, command);
    }

    private async Task Stop() => await L.StopAsync();

    private Button Preset(string label, string command)
    {
        var b = Ui.Secondary(label, null, () => _command.Text = command);
        b.Padding = new Thickness(8, 3, 8, 3);
        return b;
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) L.Changed += OnLog; else L.Changed -= OnLog;
    }

    private void OnLog() => Render();
}
