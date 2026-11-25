using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Panel log per project: jalankan perintah yang mengikuti log service di server
/// (journalctl, docker logs, tail -F) dan tampilkan keluarannya langsung. Kode warna
/// ANSI dari server ikut ditampilkan, pencarian menyaring baris yang tampil, dan isi
/// buffer bisa diekspor ke berkas.
/// </summary>
internal sealed class LiveLogsPage : PageBase
{
    private readonly LogModel L = App.Model.LiveLogs;
    private readonly ComboBox _host = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Host" };
    private readonly TextBox _command = Ui.Field("journalctl -u toko -f -n 200 --no-pager", mono: true);
    private readonly TextBox _search = Ui.Field("Cari di log…", width: 220);
    private readonly TextBlock _log;
    private readonly ScrollViewer _logScroll;
    private readonly Border _statusHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _matchHost = new() { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch _follow = new() { IsOn = true, OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly Button _start;
    private readonly Button _stop;
    private readonly StackPanel _body;
    private readonly UIElement _empty;
    private string _filter = "";
    private string? _shownText;
    private string _shownFilter = "\0";
    private string _matchText = "";
    private string? _shownProjectId;
    private bool _hooked;

    public LiveLogsPage() : base("data")
    {
        _command.Header = "Perintah";
        _start = Ui.Primary("Mulai", G.Play, () => _ = Start());
        _stop = Ui.Secondary("Hentikan", G.Stop, () => _ = Stop());
        var clear = Ui.Secondary("Bersihkan", G.Delete, () => L.Clear());
        var export = Ui.Secondary("Ekspor", G.Export, () => _ = Export());
        ToolTipService.SetToolTip(export, "Simpan seluruh isi buffer ke berkas (tanpa kode warna)");

        _search.HorizontalAlignment = HorizontalAlignment.Right;
        _search.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(_search, "Menyaring baris yang tampil; tidak peka huruf besar/kecil");
        _search.TextChanged += (_, _) =>
        {
            _filter = _search.Text.Trim();
            Render();
        };

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
            export, clear, _stop, _start);

        var quick = Ui.Row(7,
            Ui.Caps("Cepat"),
            Preset("systemd", "journalctl -u toko -f -n 200 --no-pager"),
            Preset("docker", "docker logs -f --tail 200 toko"),
            Preset("berkas", "tail -n 200 -F /var/log/toko.log"));

        var target = Ui.Card(Ui.Col(10,
            Ui.Columns("220,*", 10, _host, _command),
            quick,
            Ui.Secondary("Perintah dijalankan di server dengan akun host ini. Host dan perintah disimpan di project, jadi ikut tersinkron ke perangkat lain.", 11.5, true)), 14);

        var logHead = Ui.Columns("Auto,Auto,*,Auto,Auto", 10,
            Ui.Caps("Keluaran"), _matchHost, _search,
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

        var lines = L.Lines;
        var text = string.Join('\n', lines);
        if (text != _shownText || _filter != _shownFilter)
        {
            _shownText = text;
            _shownFilter = _filter;
            Paint(lines, text);
            if (_follow.IsOn && text.Length > 0)
                _logScroll.ChangeView(null, double.MaxValue, null, true);
        }
    }

    /// <summary>
    /// Isi panel log: jalur cepat untuk log tanpa warna (satu teks biasa), atau baris
    /// dipecah per potongan warna ANSI. Pencarian menyaring baris dan menandai yang cocok.
    /// </summary>
    private void Paint(string[] lines, string text)
    {
        if (_filter.Length == 0 && !lines.Any(l => l.Contains('\u001b')))
        {
            _log.Text = lines.Length == 0 ? "Menunggu keluaran…" : text;
            _log.Foreground = lines.Length == 0 ? Theme.Text3 : Theme.Text;
            ShowMatches(0, false);
            return;
        }

        _log.Inlines.Clear();
        _log.Foreground = Theme.Text;
        var q = _filter;
        var shown = 0;
        foreach (var raw in lines)
        {
            if (q.Length > 0 && raw.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (shown > 0) _log.Inlines.Add(new Run { Text = "\n" });
            shown++;
            foreach (var (part, color) in AnsiLog.Segments(raw))
                AddRuns(part, color, q);
        }
        if (shown == 0)
        {
            _log.Inlines.Add(new Run
            {
                Text = lines.Length == 0 ? "Menunggu keluaran…" : $"Tidak ada baris yang cocok dengan “{q}”",
                Foreground = Theme.Text3
            });
        }
        ShowMatches(shown, q.Length > 0);
    }

    /// <summary>Satu potongan teks; bagian yang cocok dengan pencarian diberi aksen.</summary>
    private void AddRuns(string text, string? color, string q)
    {
        if (text.Length == 0) return;
        var fg = color == null ? null : Theme.Brush(color);
        if (q.Length == 0)
        {
            _log.Inlines.Add(new Run { Text = text, Foreground = fg });
            return;
        }
        var at = 0;
        while (true)
        {
            var hit = text.IndexOf(q, at, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            if (hit > at) _log.Inlines.Add(new Run { Text = text[at..hit], Foreground = fg });
            _log.Inlines.Add(new Run
            {
                Text = text.Substring(hit, q.Length),
                Foreground = Theme.AccentBrush,
                FontWeight = FontWeights.SemiBold
            });
            at = hit + q.Length;
        }
        if (at < text.Length) _log.Inlines.Add(new Run { Text = text[at..], Foreground = fg });
    }

    private void ShowMatches(int count, bool visible)
    {
        _matchHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var label = count == 1 ? "1 baris cocok" : $"{count} baris cocok";
        if (label == _matchText) return;
        _matchText = label;
        _matchHost.Child = Ui.Badge(label, Theme.Accent2);
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

    /// <summary>Simpan seluruh buffer ke berkas pilihan pengguna, tanpa kode warna.</summary>
    private async Task Export()
    {
        var lines = L.Lines;
        if (lines.Length == 0)
        {
            M.Flash("Belum ada log untuk diekspor");
            return;
        }
        var name = (M.ActiveProject?.Name ?? "zevn").Trim();
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"log-{Slug(name)}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Hwnd);
        picker.FileTypeChoices.Add("Berkas log", [".log"]);
        picker.FileTypeChoices.Add("Teks", [".txt"]);
        var f = await picker.PickSaveFileAsync();
        if (f == null) return;
        await File.WriteAllTextAsync(f.Path, AnsiLog.Plain(string.Join('\n', lines)) + "\n");
        M.Flash($"Log disimpan ke {f.Name}");
    }

    private static string Slug(string name)
    {
        var s = string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        return s.Length == 0 ? "zevn" : s;
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

/// <summary>
/// Parser kode escape ANSI pada keluaran log: memecah satu baris menjadi potongan
/// berwarna dan membuang kode kontrol lain (gerak kursor, judul jendela) supaya yang
/// terlihat hanya teksnya. Palet 16 warna mengikuti tema terminal yang aktif.
/// </summary>
internal static class AnsiLog
{
    private static readonly string[] Palette = TerminalThemes.Get(null).Ansi;

    /// <summary>Potongan teks beserta warna hex-nya (`null` = warna bawaan log).</summary>
    public static IEnumerable<(string Text, string? Color)> Segments(string line)
    {
        var parts = new List<(string, string?)>();
        var sb = new StringBuilder();
        string? color = null;
        var i = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            parts.Add((sb.ToString(), color));
            sb.Clear();
        }

        while (i < line.Length)
        {
            if (line[i] != '\u001b')
            {
                sb.Append(line[i]);
                i++;
                continue;
            }

            // ESC [ … huruf: urutan kontrol CSI. Hanya `m` (SGR/warna) yang dipakai.
            if (i + 1 < line.Length && line[i + 1] == '[')
            {
                var j = i + 2;
                while (j < line.Length && !char.IsLetter(line[j])) j++;
                if (j >= line.Length) break;
                if (line[j] == 'm')
                {
                    Flush();
                    color = Sgr(line[(i + 2)..j], color);
                }
                i = j + 1;
                continue;
            }

            // ESC ] … BEL / ESC \ : judul jendela dan sejenisnya — dibuang.
            if (i + 1 < line.Length && line[i + 1] == ']')
            {
                var j = i + 2;
                while (j < line.Length && line[j] != '\a' && !(line[j] == '\u001b' && j + 1 < line.Length && line[j + 1] == '\\')) j++;
                i = j;
                if (i < line.Length && line[i] == '\a') i++;
                else if (i + 1 < line.Length) i += 2;
                else i = line.Length;
                continue;
            }

            // Escape lain (dua karakter): dibuang.
            i += Math.Min(2, line.Length - i);
        }

        Flush();
        return parts;
    }

    /// <summary>Teks tanpa kode escape/warna, untuk ekspor dan pencarian.</summary>
    public static string Plain(string text) =>
        string.Join('\n', text.Split('\n').Select(l => string.Concat(Segments(l).Select(s => s.Text))));

    private static string? Sgr(string args, string? current)
    {
        var codes = args.Length == 0 ? ["0"] : args.Split(';');
        for (var k = 0; k < codes.Length; k++)
        {
            if (!int.TryParse(codes[k], out var n)) continue;
            switch (n)
            {
                case 0 or 39:
                    current = null;
                    break;
                case >= 30 and <= 37:
                    current = Palette[n - 30];
                    break;
                case >= 90 and <= 97:
                    current = Palette[n - 90 + 8];
                    break;
                case 38 when k + 2 < codes.Length && codes[k + 1] == "5" && int.TryParse(codes[k + 2], out var idx):
                    current = Cube(idx);
                    k += 2;
                    break;
                case 38 when k + 4 < codes.Length && codes[k + 1] == "2"
                    && byte.TryParse(codes[k + 2], out var r) && byte.TryParse(codes[k + 3], out var g) && byte.TryParse(codes[k + 4], out var b):
                    current = $"#{r:X2}{g:X2}{b:X2}";
                    k += 4;
                    break;
            }
        }
        return current;
    }

    /// <summary>Warna ANSI 256: 0–15 palet, 16–231 kubus 6×6×6, 232–255 abu-abu.</summary>
    private static string Cube(int n)
    {
        n = Math.Clamp(n, 0, 255);
        if (n < 16) return Palette[n];
        if (n >= 232)
        {
            var v = 8 + (n - 232) * 10;
            return $"#{v:X2}{v:X2}{v:X2}";
        }
        n -= 16;
        static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
        return $"#{Level(n / 36):X2}{Level(n / 6 % 6):X2}{Level(n % 6):X2}";
    }
}
