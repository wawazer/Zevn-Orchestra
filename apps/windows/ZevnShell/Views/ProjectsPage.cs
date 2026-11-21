using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Project: kumpulan server untuk satu aplikasi. Project aktif mempersempit Hosts,
/// SFTP, dan daftar tunnel, serta boleh menyalakan tunnel-nya sendiri.
/// </summary>
internal sealed class ProjectsPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(10);

    public ProjectsPage() : base("data", "tunnels")
    {
        var add = Ui.Primary("Project Baru", G.Add, () => _ = Editor(HostExt.BlankProject()));
        var header = Ui.Header("Projects",
            "Satu project = kumpulan server untuk satu aplikasi. Aktifkan untuk mempersempit host, SFTP, dan tunnel.",
            add);
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        if (M.Projects.Length == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Apps, "Belum ada project",
                "Kelompokkan server per aplikasi — misalnya “Toko” berisi staging dan produksi — lalu aktifkan saat mengerjakannya.",
                Ui.Primary("Project Baru", G.Add, () => _ = Editor(HostExt.BlankProject()))));
            return;
        }
        var all = AllHostsCard();
        _list.Children.Add(all);
        for (int i = 0; i < M.Projects.Length; i++)
        {
            var card = ProjectCard(M.Projects[i]);
            _list.Children.Add(card);
            Ui.StaggerIn(card, i + 1);
        }
    }

    /// <summary>Baris “Semua host”: keadaan tanpa project aktif.</summary>
    private FrameworkElement AllHostsCard()
    {
        var on = M.ActiveProject == null;
        var info = Ui.Col(3,
            Ui.Row(8, Ui.Text("Semua host", 14, FontWeights.SemiBold),
                on ? Ui.Badge("AKTIF", Theme.Accent) : null),
            Ui.Secondary("Tanpa project — seluruh vault terlihat apa adanya.", 12));
        UIElement pick = on
            ? Ui.Text("Sedang dipakai", 12, fg: Theme.Text3)
            : Ui.Secondary("Pakai", null, () => M.SetActiveProject(null));
        var row = Ui.Columns("Auto,*,Auto", 14,
            Ui.TintIcon(G.Server, Theme.HostColors[0], 40, 16, 11), info, pick);
        var card = new Border
        {
            Child = new Border { Padding = new Thickness(14), Child = row },
            Background = on ? Theme.Brush(Theme.Accent, 0.06) : Theme.Card,
            BorderBrush = on ? Theme.Brush(Theme.Accent, 0.4) : Theme.CardStroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
        };
        Ui.HoverLift(card);
        return card;
    }

    private FrameworkElement ProjectCard(Project p)
    {
        var active = p.Id == M.ActiveProjectId;
        var live = p.HostIds.Count(id => M.HostById(id) != null);
        var tunnels = M.Forwards.Count(f => p.HostIds.Contains(f.HostId));
        var subtitle = $"{live} server" + (tunnels > 0 ? $"  ·  {tunnels} tunnel" : "");
        if (p.AutoTunnels) subtitle += "  ·  tunnel otomatis";

        var title = Ui.Row(8,
            Ui.Text(p.Name, 14.5, FontWeights.SemiBold),
            active ? Ui.Badge("AKTIF", Theme.Accent) : null);
        var info = Ui.Col(3, title, Ui.Secondary(subtitle, 12),
            p.Notes is { Length: > 0 } n ? Ui.Text(n, 11.5, fg: Theme.Text3, wrap: false) : null);
        var actions = Ui.Row(4,
            active
                ? Ui.Secondary("Nonaktifkan", G.Close, () => M.SetActiveProject(null))
                : Ui.Primary("Aktifkan", G.Play, () => M.SetActiveProject(p.Id)),
            Ui.IconBtn(G.Edit, "Edit project", () => _ = Editor(p)),
            Ui.IconBtn(G.Delete, "Hapus project", async () =>
            {
                if (await Ui.Confirm($"Hapus project “{p.Name}”?",
                    "Host-nya tidak ikut terhapus — hanya pengelompokan project yang dibuang."))
                    M.Delete(p);
            }, Theme.Brush("#E5484D")));
        var row = Ui.Columns("Auto,*,Auto", 14, Ui.TintIcon(G.Apps, p.Color, 40, 16, 11), info, actions);
        var card = new Border
        {
            Child = new Border { Padding = new Thickness(14), Child = row },
            Background = active ? Theme.Brush(Theme.Accent, 0.06) : Theme.Card,
            BorderBrush = active ? Theme.Brush(Theme.Accent, 0.4) : Theme.CardStroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
        };
        card.PointerEntered += (_, _) => { if (!active) card.Background = Theme.CardHover; };
        card.PointerExited += (_, _) => { if (!active) card.Background = Theme.Card; };
        Ui.HoverLift(card);
        return card;
    }

    private async Task Editor(Project original)
    {
        var color = original.Color;
        var picked = new HashSet<string>(original.HostIds);
        var name = Ui.Field("Nama project (mis. Toko Online)", original.Name);
        var notes = Ui.Field("Catatan (opsional)", original.Notes ?? "");

        var swatches = Ui.Row(7);
        void RenderColors()
        {
            swatches.Children.Clear();
            foreach (var hex in Theme.HostColors)
            {
                var b = new Button
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Brush(hex),
                    BorderBrush = Theme.Text, BorderThickness = new Thickness(color == hex ? 2 : 0),
                    Padding = new Thickness(0), MinWidth = 0, MinHeight = 0
                };
                b.Click += (_, _) => { color = hex; RenderColors(); };
                swatches.Children.Add(b);
            }
        }
        RenderColors();

        var deployHost = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Host tujuan deploy" };
        var logHost = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Host untuk log" };
        var hosts = Ui.Col(2);
        foreach (var h in M.Hosts)
        {
            var cb = new CheckBox
            {
                Content = Ui.Row(8, Ui.Text(h.Title(), 13),
                    Ui.Text(h.DisplayAddress(M.HideAddresses), 11, fg: Theme.Text2, mono: true)),
                IsChecked = picked.Contains(h.Id)
            };
            cb.Checked += (_, _) => { picked.Add(h.Id); RenderHostChoices(); };
            cb.Unchecked += (_, _) => { picked.Remove(h.Id); RenderHostChoices(); };
            hosts.Children.Add(cb);
        }
        UIElement hostList = M.Hosts.Length == 0
            ? Ui.Secondary("Belum ada host — tambahkan dulu di menu Hosts.", 12)
            : new ScrollViewer
            {
                Content = hosts, Height = 220, Width = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

        var auto = new ToggleSwitch { IsOn = original.AutoTunnels, OnContent = "", OffContent = "", MinWidth = 0 };
        var autoRow = Ui.Columns("*,Auto", 10,
            Ui.Col(1, Ui.Text("Nyalakan tunnel project", 13, FontWeights.SemiBold),
                Ui.Secondary("Semua aturan port forwarding host di project ini hidup saat project diaktifkan.", 11.5, true)),
            auto);

        // Deploy: host tujuan (hanya host yang dipilih di project) + folder rilis + perintah.
        void RenderDeployHosts()
        {
            var current = (deployHost.SelectedItem as ComboBoxItem)?.Tag as string ?? original.DeployHostId ?? "";
            deployHost.Items.Clear();
            deployHost.Items.Add(new ComboBoxItem { Content = "Tidak ada", Tag = "" });
            foreach (var h in M.Hosts.Where(h => picked.Contains(h.Id)))
                deployHost.Items.Add(new ComboBoxItem { Content = h.Title(), Tag = h.Id });
            deployHost.SelectedItem = deployHost.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                ?? deployHost.Items[0];
        }
        // Host untuk panel log: hanya host yang termasuk project.
        void RenderLogHosts()
        {
            var current = (logHost.SelectedItem as ComboBoxItem)?.Tag as string ?? original.LogHostId ?? "";
            logHost.Items.Clear();
            logHost.Items.Add(new ComboBoxItem { Content = "Tidak ada", Tag = "" });
            foreach (var h in M.Hosts.Where(h => picked.Contains(h.Id)))
                logHost.Items.Add(new ComboBoxItem { Content = h.Title(), Tag = h.Id });
            logHost.SelectedItem = logHost.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                ?? logHost.Items[0];
        }
        void RenderHostChoices() { RenderDeployHosts(); RenderLogHosts(); }
        RenderHostChoices();

        var deployPath = Ui.Field("/srv/aplikasi", original.DeployPath ?? "", mono: true);
        var deployBuild = Ui.Field("mis. npm ci --omit=dev", original.DeployBuild ?? "", mono: true);
        var deployRestart = Ui.Field("mis. sudo systemctl restart toko", original.DeployRestart ?? "", mono: true);
        var deployHealth = Ui.Field("mis. curl -fsS http://127.0.0.1:3000/health", original.DeployHealth ?? "", mono: true);
        var deployHelp = Ui.Secondary(
            "Rilis diunggah ke <folder>/releases/<waktu>, lalu symlink “current” dipindah. Perintah health harus keluar dengan kode 0.",
            11.5, true);
        var deployBox = Ui.Col(8,
            Ui.Text("Deploy", 12, FontWeights.SemiBold), deployHelp,
            deployHost, deployPath, deployBuild, deployRestart, deployHealth);

        var logCommand = Ui.Field("journalctl -u toko -f -n 200 --no-pager", original.LogCommand ?? "", mono: true);
        var logHelp = Ui.Secondary(
            "Perintah yang mengikuti log service; dipakai halaman Live Logs. Contoh: journalctl -u toko -f, docker logs -f toko, atau tail -n 200 -F /var/log/toko.log.",
            11.5, true);
        var logBox = Ui.Col(8,
            Ui.Text("Live Log", 12, FontWeights.SemiBold), logHelp, logHost, logCommand);

        var content = Ui.Col(9,
            Ui.Text("Nama", 12, FontWeights.SemiBold), name,
            Ui.Text("Warna", 12, FontWeights.SemiBold), swatches,
            Ui.Text("Host", 12, FontWeights.SemiBold), hostList,
            Ui.Text("Catatan", 12, FontWeights.SemiBold), notes,
            autoRow,
            Ui.Divider(), deployBox,
            Ui.Divider(), logBox);
        content.Width = 460;
        var scroll = new ScrollViewer
        {
            Content = content, MaxHeight = 580, Width = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var d = Ui.Dialog(string.IsNullOrEmpty(original.Id) ? "Project baru" : "Edit project", scroll, "Simpan");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length == 0) { M.Flash("Nama project belum diisi"); return; }
        M.Save(original with
        {
            Name = name.Text.Trim(),
            Color = color,
            HostIds = picked.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Notes = string.IsNullOrWhiteSpace(notes.Text) ? null : notes.Text.Trim(),
            AutoTunnels = auto.IsOn,
            DeployHostId = ((deployHost.SelectedItem as ComboBoxItem)?.Tag as string) is { Length: > 0 } dh ? dh : null,
            DeployPath = Blank(deployPath.Text),
            DeployBuild = Blank(deployBuild.Text),
            DeployRestart = Blank(deployRestart.Text),
            DeployHealth = Blank(deployHealth.Text),
            LogHostId = ((logHost.SelectedItem as ComboBoxItem)?.Tag as string) is { Length: > 0 } lh ? lh : null,
            LogCommand = Blank(logCommand.Text)
        });
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
