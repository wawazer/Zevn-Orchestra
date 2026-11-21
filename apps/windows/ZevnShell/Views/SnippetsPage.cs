using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Perintah tersimpan: jalankan di tab aktif atau di banyak host sekaligus.</summary>
internal sealed class SnippetsPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(10);
    private string _query = "";

    public SnippetsPage() : base("data", "sessions")
    {
        var header = Ui.Header("Snippets", "Perintah yang sering dipakai — jalankan di tab aktif atau di banyak server sekaligus.",
            Ui.Search("Cari snippet…", "", q => { _query = q; Render(); }, 190),
            Ui.Primary("Snippet Baru", G.Add, () => _ = Editor(HostExt.BlankSnippet())));
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        if (M.Snippets.Length == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Code, "Belum ada snippet", "Simpan perintah yang sering dipakai, lalu jalankan dengan satu klik.",
                Ui.Primary("Snippet Baru", G.Add, () => _ = Editor(HostExt.BlankSnippet()))));
            return;
        }
        var q = _query.Trim();
        var items = M.Snippets.Where(s => q.Length == 0 || s.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Command.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var row = Row(items[i]);
            _list.Children.Add(row);
            Ui.StaggerIn(row, i);
        }
    }

    private FrameworkElement Row(Snippet s)
    {
        var icon = Ui.TintIcon(G.Code, "#8B5CF6", 40, 15, 11);
        var cmd = new Border
        {
            Background = Theme.Field, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 6), HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Text(s.Command, 12, fg: Theme.Text2, mono: true)
        };
        var info = Ui.Col(5, Ui.Text(s.Label, 14, FontWeights.SemiBold), cmd);
        var active = M.ActiveSession;
        var inTab = Ui.Secondary("Tab aktif", G.Terminal, () => { M.RunInActiveSession(s); M.FocusTab(M.Sessions.IndexOf(active!) + 1); });
        inTab.IsEnabled = active != null;
        ToolTipService.SetToolTip(inTab, active == null ? "Tidak ada tab terminal yang terbuka" : $"Jalankan di {active.Title}");
        var run = Ui.Primary("Jalankan", G.Play, () => _ = RunDialog(s));
        var actions = Ui.Row(4, inTab, run,
            Ui.IconBtn(G.Copy, "Salin perintah", () => { Ui.CopyText(s.Command); M.Flash("Perintah disalin"); }),
            Ui.IconBtn(G.Edit, "Edit", () => _ = Editor(s)),
            Ui.IconBtn(G.Delete, "Hapus", async () => { if (await Ui.Confirm($"Hapus “{s.Label}”?", "Snippet akan dihapus dari semua perangkat.")) M.Delete(s); }, Theme.Brush("#E5484D")));
        return Ui.HoverCard(Ui.Columns("Auto,*,Auto", 14, icon, info, actions), 14);
    }

    private async Task Editor(Snippet s)
    {
        var label = Ui.Field("Nama (mis. Cek disk)", s.Label);
        var cmd = new TextBox { Text = s.Command, PlaceholderText = "df -h && free -m", AcceptsReturn = true, FontFamily = Theme.MonoFont, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        var content = Ui.Col(10, label, cmd);
        content.Width = 440;
        var d = Ui.Dialog(string.IsNullOrEmpty(s.Id) ? "Snippet baru" : "Edit snippet", content, "Simpan");
        label.Loaded += (_, _) => label.Focus(FocusState.Programmatic);
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        if (cmd.Text.Trim().Length == 0) return;
        M.Save(s with { Label = label.Text.Trim().Length == 0 ? cmd.Text.Trim().Split('\n')[0] : label.Text.Trim(), Command = cmd.Text.TrimEnd().Replace("\r\n", "\n").Replace("\r", "\n") });
    }

    /// <summary>Pilih host, lalu buka satu tab per host yang langsung menjalankan perintah.</summary>
    private async Task RunDialog(Snippet s)
    {
        if (M.Hosts.Length == 0) { M.Flash("Tambahkan host dulu"); return; }
        var picked = new HashSet<string>();
        var list = Ui.Col(6);
        Button? runButton = null;
        ContentDialog? d = null;
        void RenderList()
        {
            list.Children.Clear();
            foreach (var h in M.Hosts)
            {
                var on = picked.Contains(h.Id);
                var row = Ui.Columns("Auto,Auto,*,Auto", 10,
                    Ui.Icon(on ? "" : "", 14, on ? Theme.AccentBrush : Theme.Text2),
                    new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Brush(h.Color) },
                    Ui.Text(h.Title(), 13),
                    h.Group is { Length: > 0 } g ? Ui.Text(g, 11, fg: Theme.Text2) : null);
                var b = Ui.Plain(row, () => { if (!picked.Remove(h.Id)) picked.Add(h.Id); RenderList(); }, new Thickness(12, 8, 12, 8));
                b.Background = on ? Theme.Brush(Theme.Accent, 0.1) : Theme.Card;
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                list.Children.Add(b);
            }
            if (d != null)
            {
                d.PrimaryButtonText = $"Jalankan di {picked.Count} host";
                d.IsPrimaryButtonEnabled = picked.Count > 0;
            }
        }
        var all = Ui.Plain(Ui.Text("Pilih semua / batal", 12.5, fg: Theme.AccentBrush), () =>
        {
            if (picked.Count == M.Hosts.Length) picked.Clear(); else foreach (var h in M.Hosts) picked.Add(h.Id);
            RenderList();
        });
        all.HorizontalAlignment = HorizontalAlignment.Left;
        var content = Ui.Col(10, Ui.Text(s.Command, 12, fg: Theme.Text2, mono: true),
            new ScrollViewer { Content = list, Height = 260 }, all);
        content.Width = 460;
        d = Ui.Dialog($"Jalankan “{s.Label}”", content, "Jalankan di 0 host");
        d.IsPrimaryButtonEnabled = false;
        RenderList();
        _ = runButton;
        if (await d.ShowAsync() == ContentDialogResult.Primary && picked.Count > 0)
            M.Connect(M.Hosts.Where(h => picked.Contains(h.Id)).Select(h => h.Id).ToList(), s.Command);
    }
}
