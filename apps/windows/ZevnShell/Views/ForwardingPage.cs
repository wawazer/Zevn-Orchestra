using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Aturan port forwarding (-L / -R / -D) yang bisa dinyalakan dengan satu saklar.</summary>
internal sealed class ForwardingPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(10);

    public ForwardingPage() : base("data", "tunnels")
    {
        var add = Ui.Primary("Aturan Baru", G.Add, () => _ = Editor(HostExt.BlankForward(M.ProjectHosts.FirstOrDefault()?.Id ?? "")));
        add.IsEnabled = M.ProjectHosts.Length > 0;
        var header = Ui.Header("Port Forwarding", "Teruskan port lewat SSH — buka database, dashboard, atau proxy SOCKS dengan aman.", add);
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        var forwards = M.ProjectForwards;
        if (forwards.Length == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Forward, "Belum ada aturan",
                M.Hosts.Length == 0 ? "Tambahkan host dulu, lalu buat aturan port forwarding."
                : M.ActiveProject is { } ap ? $"Project “{ap.Name}” belum punya aturan tunnel. Buat satu, atau kembali ke semua host."
                : "Contoh: teruskan 127.0.0.1:5433 ke database PostgreSQL di server.",
                Ui.Row(8,
                    M.Hosts.Length == 0 ? null : Ui.Primary("Aturan Baru", G.Add, () => _ = Editor(HostExt.BlankForward(M.ProjectHosts.FirstOrDefault()?.Id ?? M.Hosts[0].Id))),
                    M.ActiveProject != null ? Ui.Secondary("Semua host", G.Close, () => M.SetActiveProject(null)) : null)));
            return;
        }
        for (int i = 0; i < forwards.Length; i++)
        {
            var row = Row(forwards[i]);
            _list.Children.Add(row);
            Ui.StaggerIn(row, i);
        }
    }

    private FrameworkElement Row(PortForward f)
    {
        var host = M.HostById(f.HostId);
        var st = M.Tunnels.StatusOf(f.Id);
        var running = M.Tunnels.IsRunning(f.Id);
        var color = f.Kind switch { ForwardKind.Local => "#10B981", ForwardKind.Remote => "#5B9CF5", _ => "#9D7CF2" };
        var glyph = f.Kind switch { ForwardKind.Local => G.Next, ForwardKind.Remote => G.Back, _ => G.Globe };
        var icon = Ui.TintIcon(glyph, color, 40, 15, 11);
        var summary = Ui.Text(f.Summary(host?.Title() ?? "host terhapus", M.HideAddresses), 12, fg: Theme.Text2, mono: true);
        UIElement? state = st?.State switch
        {
            TunnelManager.State.Starting => Ui.Row(6, new ProgressRing { Width = 12, Height = 12, IsActive = true }, Ui.Text("Menghubungkan…", 11.5, fg: Theme.Text2)),
            TunnelManager.State.Running => Ui.Row(6, new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Theme.Brush(Theme.Green) }.With(Ui.Pulse),
                Ui.Text("Aktif", 11.5, FontWeights.SemiBold, Theme.Brush(Theme.Green))),
            TunnelManager.State.Failed => Ui.Row(6, Ui.Icon(G.Warning, 12, Theme.Brush(Theme.Orange)), Ui.Text(st.Message ?? "Gagal", 11.5, fg: Theme.Brush(Theme.Orange)).With(t => t.MaxWidth = 420)),
            _ => null,
        };
        var info = Ui.Col(4, Ui.Row(8, Ui.Text(string.IsNullOrEmpty(f.Label) ? f.Kind.Short() : f.Label, 14, FontWeights.SemiBold), Ui.Badge(f.Kind.Short(), color)), summary, state);
        var toggle = new ToggleSwitch { IsOn = running, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => { if (toggle.IsOn != M.Tunnels.IsRunning(f.Id)) M.ToggleTunnel(f); };
        var copy = f.Kind == ForwardKind.Remote ? null : Ui.IconBtn(G.Copy, "Salin alamat lokal", () => { Ui.CopyText($"{f.BindAddress}:{f.BindPort}"); M.Flash("Alamat disalin"); });
        var actions = Ui.Row(4, copy, Ui.IconBtn(G.Edit, "Edit", () => _ = Editor(f)),
            Ui.IconBtn(G.Delete, "Hapus", async () => { if (await Ui.Confirm("Hapus aturan ini?", "Tunnel yang sedang berjalan akan dihentikan.")) M.Delete(f); }, Theme.Brush("#E5484D")),
            toggle);
        return Ui.HoverCard(Ui.Columns("Auto,*,Auto", 14, icon, info, actions), 14);
    }

    private async Task Editor(PortForward original)
    {
        var f = original;
        var label = Ui.Field("Nama (mis. Postgres produksi)", f.Label);
        var host = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Lewat host" };
        foreach (var h in M.Hosts) host.Items.Add(new ComboBoxItem { Content = h.Title(), Tag = h.Id });
        host.SelectedItem = host.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == f.HostId) ?? host.Items.FirstOrDefault();
        var kindBox = new Border();
        var explain = Ui.Secondary("", 12, true);
        var bindAddr = Ui.Field("Alamat", f.BindAddress, mono: true);
        var bindPort = Ui.Number(f.BindPort, 1, 65535, 100);
        var destHost = Ui.Field("Host tujuan", f.DestHost, mono: true);
        var destPort = Ui.Number(f.DestPort, 1, 65535, 100);
        var destRow = Ui.Columns("*,Auto", 8, destHost, destPort);
        var bindTitle = Ui.Text("", 12, FontWeights.SemiBold);
        var destTitle = Ui.Text("", 12, FontWeights.SemiBold);
        void RenderKind()
        {
            kindBox.Child = Ui.Segmented(["Local (-L)", "Remote (-R)", "SOCKS (-D)"], (int)f.Kind, i => { f = f with { Kind = (ForwardKind)i }; RenderKind(); });
            explain.Text = f.Kind.Explanation();
            bindTitle.Text = f.Kind == ForwardKind.Remote ? "Port di server" : "Port di PC ini";
            destTitle.Text = f.Kind == ForwardKind.Remote ? "Tujuan di sisi PC ini" : "Tujuan (dilihat dari server)";
            destRow.Visibility = destTitle.Visibility = f.Kind == ForwardKind.Dynamic ? Visibility.Collapsed : Visibility.Visible;
        }
        RenderKind();
        var content = Ui.Col(10, label, host, kindBox, explain, bindTitle, Ui.Columns("*,Auto", 8, bindAddr, bindPort), destTitle, destRow);
        content.Width = 460;
        var d = Ui.Dialog(string.IsNullOrEmpty(original.Id) ? "Aturan baru" : "Edit aturan", content, "Simpan");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var saved = f with
        {
            Label = label.Text.Trim(), HostId = (host.SelectedItem as ComboBoxItem)?.Tag as string ?? f.HostId,
            BindAddress = bindAddr.Text.Trim().Length == 0 ? "127.0.0.1" : bindAddr.Text.Trim(),
            BindPort = (ushort)(double.IsNaN(bindPort.Value) ? 8080 : bindPort.Value),
            DestHost = destHost.Text.Trim().Length == 0 ? "localhost" : destHost.Text.Trim(),
            DestPort = (ushort)(double.IsNaN(destPort.Value) ? 80 : destPort.Value),
        };
        var wasRunning = M.Tunnels.IsRunning(original.Id);
        if (wasRunning) M.Tunnels.Stop(original.Id);
        M.Save(saved);
        if (wasRunning && M.Forwards.FirstOrDefault(x => x.Id == original.Id) is { } updated) M.ToggleTunnel(updated);
    }
}
