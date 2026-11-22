using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Editor .env project aktif: dibaca & ditulis lewat SFTP, nilai sensitif
/// disamarkan, dan isi lama di server dibackup ke .env.bak sebelum disimpan.
/// </summary>
internal sealed class EnvPage : PageBase
{
    private readonly EnvModel E = App.Model.Env;
    private bool _autoLoaded;

    public EnvPage() : base("data", "env")
    {
        Render();
    }

    protected override void Render()
    {
        Children.Clear();
        if (M.ActiveProject is not { } p)
        {
            Children.Add(Ui.Scroll(Ui.Col(22,
                Ui.EmptyState(G.Key, "Belum ada project aktif",
                    "Tab Env mengedit file .env satu project di server. Aktifkan project dulu, lalu kembali ke sini.",
                    Ui.Primary("Buka Projects", G.Apps, () => M.Navigate(Section.Projects))))));
            return;
        }

        var host = M.HostById(p.DeployHostId) ?? M.HostById(p.HostIds.FirstOrDefault());
        if (host == null)
        {
            Children.Add(Ui.Scroll(Ui.Col(22,
                Ui.EmptyState(G.Server, "Project ini belum punya server",
                    "Tambahkan host lewat Edit di halaman Projects. Tab Env memakai host deploy project, atau host pertama bila belum diatur.",
                    Ui.Primary("Kelola project", G.Apps, () => M.Navigate(Section.Projects))))));
            return;
        }

        // Buka file otomatis saat halaman pertama kali dibuka atau project berganti host.
        if (!_autoLoaded || E.HostId != host.Id)
        {
            _autoLoaded = true;
            if (E.State == EnvModel.Status.Idle && !E.Loaded && M.SshTarget(host.Id) is { } t)
                _ = E.OpenAsync(p, host, t);
        }

        var reload = Ui.Secondary("Muat ulang", G.Refresh, () =>
        {
            if (M.SshTarget(host.Id) is { } t) _ = E.ReadAsync(t);
        });
        reload.IsEnabled = !E.Busy;

        var save = Ui.Primary(E.State == EnvModel.Status.Saving ? "Menyimpan…" : "Simpan", G.Save, () => _ = E.SaveAsync());
        save.IsEnabled = E.State == EnvModel.Status.Ready;

        var header = Ui.Header("Env",
            $"Project “{p.Name}”  ·  {host.Title()}  ·  {E.PairCount} variabel",
            reload, save);

        var body = Ui.Col(16, header, PathCard(p, host), StatusCard(), ListCard());
        Children.Add(Ui.Scroll(body));
        for (int i = 0; i < body.Children.Count; i++) Ui.StaggerIn(body.Children[i], i);
    }

    // MARK: Path & host

    private UIElement PathCard(Project p, Host host)
    {
        var path = Ui.Field("Contoh: /srv/app/.env", E.Path, mono: true);
        path.HorizontalAlignment = HorizontalAlignment.Stretch;
        var load = Ui.Secondary("Muat", G.Refresh, () => _ = Load(p, host, path));
        path.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) _ = Load(p, host, path); };
        load.IsEnabled = !E.Busy;

        var row = Ui.Columns("*,Auto", 8, path, load);
        var left = Ui.Col(2,
            Ui.Caps("File di server"),
            Ui.Secondary("Path disimpan per perangkat, bukan di vault. Backup otomatis ke .env.bak sebelum menyimpan.", 11.5, true));

        var head = Ui.Columns("Auto,*,Auto", 10,
            Ui.TintIcon(G.Key, "#F59E0B", 26, 11, 7),
            Ui.Col(1, Ui.Text(host.Title(), 12.5, FontWeights.SemiBold), Ui.Text(host.DisplayAddress(M.HideAddresses), 11, fg: Theme.Text2, mono: true)),
            Ui.Pill(E.Reveal ? "NILAI TERBUKA" : "NILAI DISAMARKAN", E.Reveal ? Theme.Brush(Theme.Orange) : Theme.Text3));

        return Ui.Card(Ui.Col(10, head, Ui.Divider(), left, row));
    }

    private async Task Load(Project p, Host host, TextBox box)
    {
        if (M.SshTarget(host.Id) is not { } t) return;
        await E.SetPathAsync(p, box.Text, t);
    }

    private UIElement StatusCard()
    {
        var (label, color) = E.State switch
        {
            EnvModel.Status.Connecting => ("MEMBACA", Theme.Orange),
            EnvModel.Status.Saving => ("MENYIMPAN", Theme.Orange),
            EnvModel.Status.Failed => ("GAGAL", Theme.Red),
            EnvModel.Status.Ready => E.Exists ? ("TERBUKA", Theme.Accent) : ("FILE BARU", "#38BDF8"),
            _ => ("SIAP", "#94A3B8"),
        };
        var row = Ui.Row(10, Ui.Badge(label, color));
        if (E.State is EnvModel.Status.Connecting or EnvModel.Status.Saving)
            row.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });

        if (E.Error is { Length: > 0 } err)
            row.Children.Add(Ui.Text(err, 12, fg: Theme.Brush(Theme.Orange), wrap: true));
        else if (E.Note is { Length: > 0 } note)
            row.Children.Add(Ui.Text(note, 11.5, fg: Theme.Text2, mono: note.StartsWith("Backup")));
        if (E.Dirty)
            row.Children.Add(Ui.Badge("ADA PERUBAHAN BELUM DISIMPAN", Theme.Orange));

        return Ui.Card(row, 10);
    }

    // MARK: Editor

    private UIElement ListCard()
    {
        if (E.State == EnvModel.Status.Connecting && !E.Loaded)
            return Ui.Card(Ui.Row(10, new ProgressRing { Width = 16, Height = 16, IsActive = true },
                Ui.Secondary("Membaca file dari server…")), 14);

        if (!E.Loaded)
        {
            return Ui.Card(Ui.Col(10,
                Ui.Text("File belum dibaca", 13, FontWeights.SemiBold),
                Ui.Secondary(E.State == EnvModel.Status.Failed
                    ? "Periksa path dan koneksi ke server, lalu tekan Muat ulang."
                    : "Tekan Muat ulang untuk membaca file.", 12, true)));
        }

        var list = Ui.Col(2);
        if (E.Lines.Count == 0)
            list.Children.Add(Ui.Secondary("File kosong. Tambahkan variabel pertama lewat tombol di bawah.", 12, true).With(t => t.Margin = new Thickness(2, 6, 0, 6)));
        foreach (var line in E.Lines) list.Children.Add(Row(line));

        var add = Ui.Primary("Tambah variabel", G.Add, () => _ = AddVariable());
        var reveal = new ToggleSwitch
        {
            IsOn = E.Reveal, OnContent = "Tampilkan nilai", OffContent = "Sembunyikan nilai",
            MinWidth = 0, Margin = new Thickness(0, 2, 0, 0)
        };
        reveal.Toggled += (_, _) => { if (reveal.IsOn != E.Reveal) E.ToggleReveal(); };
        var footer = Ui.Row(10, add, reveal);
        footer.HorizontalAlignment = HorizontalAlignment.Left;

        var content = Ui.Col(12, list, Ui.Divider(), footer);
        return Ui.Card(content, 10);
    }

    private UIElement Row(EnvLine line)
    {
        if (line.Type == EnvLine.Kind.Blank) return new Border { Height = 3 };
        if (line.Type == EnvLine.Kind.Comment)
            return Ui.Text(line.Text, 11.5, fg: Theme.Text3, mono: true, wrap: false).With(t => t.Margin = new Thickness(8, 2, 8, 2));

        UIElement editor;
        if (line.Secret && !E.Reveal)
        {
            var pwd = new PasswordBox
            {
                Password = line.Value, PasswordRevealMode = PasswordRevealMode.Peek,
                FontFamily = Theme.MonoFont, FontSize = 12.5, MinWidth = 0,
            };
            pwd.PasswordChanged += (_, _) => E.SetValue(line, pwd.Password);
            editor = pwd;
        }
        else
        {
            var tb = Ui.Field("nilai", line.Value, mono: true);
            tb.FontSize = 12.5;
            tb.TextChanged += (_, _) => E.SetValue(line, tb.Text);
            editor = tb;
        }

        var key = Ui.Col(1,
            Ui.Row(6, Ui.Text(line.Key, 12.5, FontWeights.SemiBold, mono: true),
                line.Secret ? Ui.Icon(G.Lock, 10, Theme.Brush(Theme.Orange)) : null),
            line.Added ? Ui.Badge("BARU", "#38BDF8") : null);

        var del = Ui.IconBtn(G.Delete, "Hapus baris (berlaku setelah Simpan)", () => E.Remove(line), Theme.Text3, 11);
        var row = Ui.Columns("240,*,Auto", 10, key, editor, del);
        row.Margin = new Thickness(4, 2, 2, 2);
        return row;
    }

    private async Task AddVariable()
    {
        var name = Ui.Field("NAMA_VARIABEL", mono: true);
        var value = Ui.Field("nilai", mono: true);
        var form = Ui.Col(8, Ui.Caps("Nama"), name, Ui.Caps("Nilai"), value,
            Ui.Secondary("Nama yang mengandung SECRET, TOKEN, PASSWORD, KEY, dan sejenisnya otomatis disamarkan.", 11, true));
        var d = Ui.Dialog("Tambah variabel", form, "Tambah");
        d.PrimaryButtonClick += (_, _) =>
        {
            if (name.Text.Trim().Length > 0) E.AddPair(name.Text, value.Text);
        };
        await d.ShowAsync();
    }
}
