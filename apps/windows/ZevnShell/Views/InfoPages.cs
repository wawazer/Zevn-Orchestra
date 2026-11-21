using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Server yang sudah dipercaya (%USERPROFILE%\.ssh\known_hosts).</summary>
internal sealed class KnownHostsPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(8);
    private string _query = "";

    public KnownHostsPage() : base("data")
    {
        var header = Ui.Header("Known Hosts", "Fingerprint server yang sudah dipercaya — %USERPROFILE%\\.ssh\\known_hosts",
            Ui.Search("Cari host…", "", q => { _query = q; Render(); }, 180),
            Ui.IconBtn(G.Refresh, "Muat ulang", Render).With(b => b.Background = Theme.Ink(0.07)));
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        var entries = KnownHostsFile.Read();
        if (entries.Count == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Fingerprint, "Belum ada known host", "Fingerprint server otomatis tercatat di sini saat pertama kali kamu connect."));
            return;
        }
        var q = _query.ToLowerInvariant();
        var shown = entries.Where(e => q.Length == 0 || e.Hosts.ToLowerInvariant().Contains(q) || e.KeyType.ToLowerInvariant().Contains(q)).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var e = shown[i];
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Theme.Ink(0.06), Child = new FontIcon { Glyph = G.Fingerprint, FontSize = 17, Foreground = Theme.BrandBrush } };
            var info = Ui.Col(3, Ui.Row(8, Ui.Text(e.Display(M.HideAddresses), 13, FontWeights.SemiBold), Ui.Badge(e.ShortType, Theme.Accent2)),
                Ui.Text(e.Fingerprint, 11, fg: Theme.Text2, mono: true).With(t => { t.IsTextSelectionEnabled = true; t.TextTrimming = TextTrimming.CharacterEllipsis; }));
            var del = Ui.IconBtn(G.Delete, "Hapus dari known hosts", async () =>
            {
                if (!await Ui.Confirm("Hapus fingerprint ini?", "Saat connect berikutnya, fingerprint server akan dicatat ulang. Hapus hanya jika kamu yakin server memang berganti kunci.")) return;
                try { KnownHostsFile.Remove(e.Line); M.Flash("Fingerprint dihapus"); } catch (Exception ex) { M.Flash(ex.Message); }
                Render();
            }, Theme.Brush("#E5484D"));
            var card = Ui.HoverCard(Ui.Columns("Auto,*,Auto", 12, icon, info, del), 12);
            _list.Children.Add(card);
            Ui.StaggerIn(card, i);
        }
    }
}

/// <summary>Riwayat sesi SSH: kapan, ke mana, berapa lama, dan hasilnya.</summary>
internal sealed class LogsPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(18);
    private static readonly CultureInfo Id = new("id-ID");

    public LogsPage() : base("data", "logs", "sessions")
    {
        var clear = Ui.Secondary("Bersihkan", G.Delete, async () =>
        {
            if (await Ui.Confirm("Hapus semua riwayat sesi?", "Riwayat di semua perangkat yang tersinkron ikut terhapus.", "Hapus semua")) M.ClearLogs();
        });
        var header = Ui.Header("Logs", "Riwayat sesi SSH di semua perangkat yang tersinkron", clear);
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        if (M.Logs.Length == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Clock, "Belum ada riwayat", "Setiap sesi SSH yang kamu buka akan tercatat di sini."));
            return;
        }
        int n = 0;
        foreach (var day in M.Logs.GroupBy(l => Ui.FromMs(l.StartedAt).LocalDateTime.Date).OrderByDescending(g => g.Key))
        {
            var title = day.Key == DateTime.Today ? "Hari ini" : day.Key == DateTime.Today.AddDays(-1) ? "Kemarin" : day.Key.ToString("dddd, d MMMM yyyy", Id);
            var col = Ui.Col(6, Ui.Caps(title));
            foreach (var l in day)
            {
                var row = Row(l);
                col.Children.Add(row);
                Ui.StaggerIn(row, n++);
            }
            _list.Children.Add(col);
        }
    }

    private FrameworkElement Row(SessionLog l)
    {
        var host = M.HostById(l.HostId);
        (string text, string color) status;
        if (l.EndedAt == null)
        {
            var live = M.Sessions.Any(s => s.Host.Id == l.HostId && s.State != TerminalSession.Status.Connecting);
            status = live ? ("Berjalan", Theme.Green) : ("Terputus", "#94A3B8");
        }
        else status = l.ExitCode switch
        {
            0 => ("Selesai", Theme.Accent),
            255 => ("Gagal connect", Theme.Orange),
            int c => ($"Keluar ({c})", "#EAB308"),
            null => ("Ditutup", "#94A3B8"),
        };
        var start = Ui.FromMs(l.StartedAt);
        var end = l.EndedAt is { } e ? Ui.FromMs(e) : DateTimeOffset.Now;
        var secs = Math.Max(0, (end - start).TotalSeconds);
        var dur = secs >= 3600 ? $"{(int)(secs / 3600)}j {(int)(secs % 3600 / 60)}m" : $"{(int)(secs / 60)}m {(int)(secs % 60)}d";
        var addr = l.Address;
        if (M.HideAddresses && addr.IndexOf('@') is var at and >= 0) addr = addr[..(at + 1)] + "••••••••";
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.Brush(host?.Color ?? "#94A3B8") };
        var info = Ui.Col(2, Ui.Text(l.HostLabel, 13, FontWeights.SemiBold), Ui.Text(addr, 11, fg: Theme.Text2, mono: true));
        var time = Ui.Text(start.LocalDateTime.ToString("HH.mm", Id), 12, fg: Theme.Text2);
        var duration = Ui.Text(dur, 12, fg: Theme.Text2, mono: true).With(t => { t.Width = 70; t.TextAlignment = TextAlignment.Right; });
        var badge = new Border { Width = 110, Child = Ui.Badge(status.text, status.color).With(b => b.HorizontalAlignment = HorizontalAlignment.Right) };
        UIElement? again = host == null ? null : Ui.IconBtn(G.Bolt, "Connect lagi", () => M.Connect([host.Id]), Theme.AccentBrush);
        return Ui.HoverCard(Ui.Columns("Auto,*,Auto,Auto,Auto,Auto", 12, dot, info, time, duration, badge, again ?? new Border { Width = 30 }), 12);
    }
}

/// <summary>Profil akun, status sync, Windows Hello, kunci & keluar.</summary>
internal sealed class AccountPage : PageBase
{
    private readonly StackPanel _body = Ui.Col(18);

    public AccountPage() : base("data", "sync")
    {
        Children.Add(Ui.Scroll(_body));
        Render();
    }

    protected override void Render()
    {
        _body.Children.Clear();
        _body.MaxWidth = 720;
        _body.HorizontalAlignment = HorizontalAlignment.Left;

        // Profil
        var avatar = Ui.Avatar(M.ProfileName, M.AccountEmail, 64);
        var name = Ui.Col(3, Ui.Text(M.ProfileName ?? M.AccountEmail ?? "Tanpa akun", 22, FontWeights.Bold),
            Ui.Secondary(M.AccountEmail ?? "Data hanya tersimpan di PC ini. Hubungkan Google untuk sync antar perangkat.", 13, true));
        _body.Children.Add(Ui.Card(Ui.Columns("Auto,*", 16, avatar, name), 20, 16));

        // Statistik
        var stats = new Grid { ColumnSpacing = 10 };
        (string label, int value, string glyph, string tint)[] items =
        [
            ("Host", M.Hosts.Length, G.Server, "#3B82F6"), ("SSH key", M.Keys.Length, G.Key, "#F59E0B"),
            ("Snippet", M.Snippets.Length, G.Code, "#8B5CF6"), ("Database", M.Databases.Length, G.Database, "#F97316"),
        ];
        for (int i = 0; i < items.Length; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            var it = items[i];
            var c = Ui.Card(Ui.Col(6, Ui.TintIcon(it.glyph, it.tint, 26, 12), Ui.Text(it.value.ToString(), 20, FontWeights.Bold), Ui.Secondary(it.label, 12)), 14, 12);
            Grid.SetColumn(c, i);
            stats.Children.Add(c);
        }
        _body.Children.Add(stats);

        // Sync
        var syncCol = Ui.Col(10, Ui.Text("Sync Google Drive", 15, FontWeights.SemiBold));
        if (M.AccountEmail == null)
        {
            syncCol.Children.Add(Ui.Secondary("Vault terenkripsi disimpan di folder tersembunyi Google Drive milikmu (appDataFolder). Google tidak bisa membacanya.", 12.5, true));
            syncCol.Children.Add(Ui.Primary("Hubungkan Google Drive", G.Cloud, () => _ = Connect()).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        }
        else
        {
            var st = Ui.Row(8, Ui.Icon(M.Sync is SyncKind.Failed or SyncKind.NeedsLogin ? G.Warning : G.Cloud, 14,
                M.Sync is SyncKind.Failed or SyncKind.NeedsLogin ? Theme.Brush(Theme.Orange) : Theme.AccentBrush), Ui.Text(M.SyncStatusText, 13));
            syncCol.Children.Add(st);
            if (M.SyncError is { } err) syncCol.Children.Add(Ui.Text(err, 12, fg: Theme.Brush(Theme.Orange), wrap: true));
            var row = Ui.Row(8);
            if (M.Sync == SyncKind.NeedsLogin) row.Children.Add(Ui.Primary("Masuk ulang Google", G.Globe, () => _ = Connect()));
            else row.Children.Add(Ui.Secondary("Sinkronkan sekarang", G.Sync, M.SyncNow));
            row.Children.Add(Ui.Secondary("Putuskan akun Google", null, M.DisconnectGoogle, Theme.Brush("#E5484D")));
            syncCol.Children.Add(row);
        }
        _body.Children.Add(Ui.Card(syncCol, 18, 14));

        // Keamanan
        var hello = new ToggleSwitch { IsOn = HelloUnlock.HasStoredKey, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = false };
        _ = EnableHelloToggle(hello);
        hello.Toggled += (_, _) => { if (hello.IsOn != HelloUnlock.HasStoredKey) M.EnableHello(hello.IsOn); };
        var helloRow = Ui.Columns("Auto,*,Auto", 12, Ui.Icon(G.Hello, 18, Theme.AccentBrush),
            Ui.Col(1, Ui.Text("Buka dengan Windows Hello", 13, FontWeights.Medium), Ui.Secondary("Kunci vault disimpan di Credential Manager PC ini", 11.5)), hello);
        var lockBtn = Ui.Secondary("Kunci vault sekarang  (Ctrl+Shift+L)", G.Lock, M.Lock).With(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        _body.Children.Add(Ui.Card(Ui.Col(12, Ui.Text("Keamanan", 15, FontWeights.SemiBold), helloRow, lockBtn), 18, 14));

        // Keluar
        var outCol = Ui.Col(10, Ui.Text("Keluar", 15, FontWeights.SemiBold),
            Ui.Secondary("Keluar memutus login Google dan mengunci vault. Pilih hapus jika PC ini bukan milikmu.", 12.5, true),
            Ui.Row(8,
                Ui.Secondary("Keluar", G.SignOut, async () => { if (await Ui.Confirm("Keluar dari akun?", "Vault dikunci dan login Google diputus. Data tetap terenkripsi di PC ini.", "Keluar", false)) M.SignOut(false); }),
                Ui.Secondary("Keluar & hapus data dari PC ini", G.Delete, async () =>
                {
                    if (await Ui.Confirm("Hapus semua data dari PC ini?", "Vault lokal dan kunci Windows Hello dihapus. Salinan di Google Drive tidak dihapus, jadi bisa dipulihkan dengan login lagi.", "Hapus & keluar"))
                        M.SignOut(true);
                }, Theme.Brush("#E5484D"))));
        _body.Children.Add(Ui.Card(outCol, 18, 14));
        Ui.StaggerIn(_body, 0);
    }

    private static async Task EnableHelloToggle(ToggleSwitch t) => t.IsEnabled = await HelloUnlock.IsAvailable() || HelloUnlock.HasStoredKey;

    private async Task Connect()
    {
        try { await M.SignInGoogle(); }
        catch (AuthCancelledException) { }
        catch (Exception e) { M.Flash(Errors.Text(e)); }
    }
}

/// <summary>Tentang Zevn Shell: identitas, spesifikasi, dan lingkungan runtime PC ini.</summary>
internal sealed class AboutPage : PageBase
{
    public AboutPage() : base("update")
    {
        Render();
    }

    protected override void Render()
    {
        Children.Clear();
        var core = uniffi.zevn_core.ZevnCoreMethods.CoreVersion();

        // Kepala: ikon + identitas di kiri, pintasan tautan sebagai toolbar ikon di kanan.
        var toolbar = new Border
        {
            Background = Theme.Ink(0.04), BorderBrush = Theme.Ink(0.07), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Row(0,
                Ui.IconBtn(G.Globe, "Website", () => Ui.Open(Config.Website)),
                Ui.IconBtn(G.Code, "GitHub", () => Ui.Open(Config.GitHub)),
                Ui.IconBtn(G.Sparkle, "Catatan rilis", () => Ui.Open(Config.Releases)),
                Ui.IconBtn(G.Bug, "Laporkan bug", () => Ui.Open(Config.Issues)))
        };
        var head = Ui.Columns("Auto,*,Auto", 14,
            new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/logo.png")), Width = 54, Height = 54 },
            Ui.Col(4,
                Ui.Row(8, Ui.Text("Zevn Shell", 25, FontWeights.ExtraBold), Ui.Badge($"v{Config.Version}")),
                Ui.Text($"zevn-core {core} · WinUI 3 · MIT", 11.5, fg: Theme.Text3, mono: true)),
            toolbar);

        var intro = Ui.Secondary("Terminal SSH untuk Windows: buka banyak host sekaligus, kelola key dan snippet, terowongan port, database, serta transfer file dari satu jendela. Vault dienkripsi di PC ini dan hanya disinkronkan lewat Google Drive milikmu.", 13, true)
            .With(t => t.MaxWidth = 640);

        var spec = Ui.Rows("Auto,Auto", 10,
            Ui.Columns("*,*", 10,
                Tile(G.Lock, "Vault", "Argon2id + XChaCha20-Poly1305",
                    "Master password tidak pernah dikirim ke mana pun; isi vault hanya terbuka di perangkat ini."),
                Tile(G.Cloud, "Sinkronisasi", "Google Drive · appDataFolder",
                    "Hanya file terenkripsi yang naik, disimpan di folder tersembunyi milik aplikasi.")),
            Ui.Columns("*,*", 10,
                Tile(G.Terminal, "Terminal", "xterm.js · ConPTY · OpenSSH",
                    "Sesi remote lewat OpenSSH, sesi lokal lewat shell bawaan Windows."),
                Tile(G.Code, "Inti", "Rust · zevn-core",
                    "Logika vault, sinkronisasi, dan keychain dipakai bersama versi macOS.")));

        var env = Ui.Card(Ui.Col(0,
            EnvRow("Windows", RuntimeInformation.OSDescription.Replace("Microsoft ", "")),
            EnvRow("Arsitektur", $"{RuntimeInformation.ProcessArchitecture} · {(Environment.Is64BitProcess ? "proses 64-bit" : "proses 32-bit")}"),
            EnvRow("Runtime", RuntimeInformation.FrameworkDescription),
            EnvRow("Proses", $"PID {Environment.ProcessId} · {Environment.ProcessorCount} core"),
            EnvRow("Shell lokal", Settings.Str(Settings.LocalShell) is { Length: > 0 } shell ? shell : "Bawaan Windows"),
            EnvRow("Folder data", Config.DataDir),
            EnvRow("Vault", VaultSummary(), true)), 6, 10);

        UIElement update = M.Update is { } u
            ? Ui.Card(Ui.Columns("Auto,*,Auto", 12,
                Ui.Icon(G.Sparkle, 16, Theme.BrandBrush),
                Ui.Col(2, Ui.Text($"Versi {u.Version} tersedia", 13, FontWeights.SemiBold),
                    Ui.Secondary("Unduh pembaruan dari halaman rilis.", 11.5)),
                Ui.Primary("Download", G.Download, () => Ui.Open(u.Download ?? u.Page))), 14)
            : Ui.Card(Ui.Columns("Auto,*,Auto", 12,
                Ui.Icon(G.Check, 16, Theme.AccentBrush),
                Ui.Col(2, Ui.Text("Versi terbaru", 13, FontWeights.SemiBold),
                    Ui.Secondary($"Kamu memakai v{Config.Version}, versi terbaru yang diketahui.", 11.5)),
                Ui.Secondary("Periksa lagi", null, () => _ = M.CheckForUpdates(true))), 14);

        var legal = Ui.Row(16,
            Ui.Plain(Ui.Text("Kebijakan Privasi", 12, fg: Theme.AccentBrush), () => Ui.Open(Config.Privacy)),
            Ui.Plain(Ui.Text("Syarat Layanan", 12, fg: Theme.AccentBrush), () => Ui.Open(Config.Terms)));
        var footer = Ui.Columns("*,Auto", 12, Ui.Text("© 2026 Wawazer · lisensi MIT", 11, fg: Theme.Text3), legal);

        var body = Ui.Col(18, head, Ui.Caps("Tentang"), intro, spec, Ui.Caps("Lingkungan"), env, update, footer);
        body.MaxWidth = 720;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.Padding = new Thickness(0, 20, 0, 28);
        for (int i = 0; i < body.Children.Count; i++) Ui.StaggerIn(body.Children[i], i);
        Children.Add(Ui.Scroll(body));
    }

    /// <summary>Kartu satu spesifikasi: ikon + label, nilai besar, lalu keterangan.</summary>
    private static Border Tile(string glyph, string label, string value, string note) =>
        Ui.Card(Ui.Col(9,
            Ui.Columns("Auto,*", 8, Ui.Icon(glyph, 13, Theme.BrandBrush),
                Ui.Caps(label).With(t => t.VerticalAlignment = VerticalAlignment.Center)),
            Ui.Text(value, 13.5, FontWeights.SemiBold, wrap: true),
            Ui.Secondary(note, 11.5, true)), 14, 10);

    /// <summary>Satu baris kunci–nilai untuk kartu lingkungan; nilai bisa disalin.</summary>
    private static UIElement EnvRow(string label, string value, bool last = false) => new Border
    {
        Padding = new Thickness(14, 9, 14, 10),
        BorderBrush = Theme.Ink(0.06), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
        Child = Ui.Columns("140,*", 12,
            Ui.Text(label, 12, fg: Theme.Text3),
            Ui.Text(value, 12, mono: true, wrap: true).With(t => t.IsTextSelectionEnabled = true))
    };

    private static string VaultSummary()
    {
        try
        {
            var f = new FileInfo(Config.VaultPath);
            if (!f.Exists) return "Belum dibuat di perangkat ini";
            var kb = f.Length / 1024.0;
            var size = kb >= 1024 ? $"{kb / 1024:0.0} MB" : $"{kb:0} KB";
            return $"vault.zevn · {size} · {f.LastWriteTime:dd MMM yyyy HH:mm}";
        }
        catch (Exception e) { return e.Message; }
    }
}
