using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>SFTP dua panel: PC ini ↔ server. Drag & drop antarpanel dan dari File Explorer.</summary>
internal sealed class SftpPage : PageBase
{
    private SftpModel S => M.Sftp;
    private readonly Border _top = new();
    private readonly Border _localPane = new();
    private readonly Border _remotePane = new();
    private readonly Border _transfers = new();
    private readonly Grid _connected;
    private readonly Border _picker = new();
    private ListView? _localList, _remoteList;
    private static readonly CultureInfo Id = new("id-ID");
    private const string DragFormat = "zevn-sftp";

    public SftpPage() : base("sftp", "transfers", "data")
    {
        var panes = Ui.Columns("*,*", 12, _localPane, _remotePane);
        _connected = Ui.Rows("Auto,*,Auto", 12, _top, panes, _transfers);
        _connected.Padding = new Thickness(20, 16, 20, 16);
        Children.Add(_connected);
        Children.Add(_picker);
        Render();
    }

    protected override void OnModelChanged(string what)
    {
        if (what == "transfers") { if (_connected.Visibility == Visibility.Visible) RenderTransfers(); }
        else Render();
    }

    protected override void Render()
    {
        var showPicker = S.State == SftpModel.Status.Idle;
        _picker.Visibility = showPicker ? Visibility.Visible : Visibility.Collapsed;
        _connected.Visibility = showPicker ? Visibility.Collapsed : Visibility.Visible;
        if (showPicker) { RenderPicker(); return; }
        RenderTop();
        RenderLocal();
        RenderRemote();
        RenderTransfers();
    }

    // MARK: Pilih server

    private void RenderPicker()
    {
        var header = Ui.Header("SFTP", "Kelola file di server: upload, download, ganti nama, atur izin.");
        var col = Ui.Col(22, header);
        var hosts = M.ProjectHosts;
        if (hosts.Length == 0)
            col.Children.Add(Ui.EmptyState(G.Folder, M.ActiveProject is { } ap ? $"“{ap.Name}” belum punya host" : "Belum ada host",
                M.ActiveProject != null ? "Tambahkan host ke project ini, atau kembali ke semua host."
                : "Tambahkan host dulu di menu Hosts, lalu buka SFTP-nya di sini.",
                M.ActiveProject != null ? Ui.Secondary("Semua host", G.Close, () => M.SetActiveProject(null)) : null));
        else
        {
            col.Children.Add(Ui.Caps(M.ActiveProject is { } p2 ? $"Pilih server · project {p2.Name}" : "Pilih server"));
            var rep = new ItemsRepeater
            {
                Layout = new UniformGridLayout { MinItemWidth = 240, MinItemHeight = 72, MinColumnSpacing = 12, MinRowSpacing = 12, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
                ItemsSource = hosts.Select((h, i) =>
                {
                    var row = Ui.Columns("Auto,*,Auto", 12, Ui.TintIcon(G.Folder, h.Color, 38, 15, 10),
                        Ui.Col(2, Ui.Text(h.Title(), 13.5, FontWeights.SemiBold), Ui.Text(h.DisplayAddress(M.HideAddresses), 11, fg: Theme.Text2, mono: true)),
                        Ui.Icon(G.ChevronRight, 11, Theme.Text3));
                    var b = Ui.Plain(row, () => _ = M.OpenSftp(h), new Thickness(14, 12, 14, 12));
                    b.Background = Theme.Card; b.BorderBrush = Theme.CardStroke; b.BorderThickness = new Thickness(1); b.CornerRadius = new CornerRadius(12);
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    Ui.StaggerIn(b, i);
                    return (object)b;
                }).ToList()
            };
            col.Children.Add(rep);
        }
        _picker.Child = Ui.Scroll(col);
    }

    // MARK: Bar atas

    private void RenderTop()
    {
        var h = S.Host;
        UIElement status = S.State switch
        {
            SftpModel.Status.Connecting => Ui.Row(6, new ProgressRing { Width = 14, Height = 14, IsActive = true }, Ui.Text("Menyambung…", 12, fg: Theme.Text2)),
            SftpModel.Status.Failed => Ui.Row(6, Ui.Icon(G.Warning, 12, Theme.Brush(Theme.Orange)), Ui.Text(S.FailMessage ?? "Gagal", 12, fg: Theme.Brush(Theme.Orange)).With(t => t.MaxWidth = 520)),
            _ => Ui.Row(6, new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Theme.Brush(Theme.Green) }, Ui.Text("Tersambung", 12, fg: Theme.Brush(Theme.Green))),
        };
        var title = Ui.Row(10, Ui.TintIcon(G.Folder, h?.Color ?? Theme.Accent, 30, 13, 8), Ui.Col(1, Ui.Text(h?.Title() ?? "SFTP", 15, FontWeights.Bold), status));
        var hidden = new ToggleSwitch { IsOn = S.ShowHidden, OnContent = "File tersembunyi", OffContent = "File tersembunyi", MinWidth = 0 };
        hidden.Toggled += (_, _) => { if (hidden.IsOn != S.ShowHidden) S.SetShowHidden(hidden.IsOn); };
        var actions = Ui.Row(8, hidden,
            S.State == SftpModel.Status.Failed && h != null ? Ui.Secondary("Coba lagi", G.Refresh, () => _ = M.OpenSftp(h)) : null,
            Ui.Secondary("Ganti server", G.Server, () => _ = S.Disconnect()),
            h != null ? Ui.Secondary("Terminal", G.Terminal, () => M.Connect([h.Id])) : null);
        var row = Ui.Columns("*,Auto", 12, title, actions);
        var err = S.Error is { } e ? new InfoBar { Severity = InfoBarSeverity.Warning, Message = e, IsOpen = true, IsClosable = false } : null;
        _top.Child = Ui.Col(8, row, err);
    }

    // MARK: Panel

    private Border Pane(string title, string glyph, string path, Action up, Action home, Action refresh, Action<string> go, Action newFolder, ListView list, UIElement? extra)
    {
        var pathBox = new TextBox { Text = path, FontFamily = Theme.MonoFont, FontSize = 12 };
        pathBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) go(pathBox.Text.Trim()); };
        var tools = Ui.Row(2, Ui.IconBtn(G.Up, "Folder induk", up), Ui.IconBtn(G.Home, "Folder rumah", home),
            Ui.IconBtn(G.Refresh, "Muat ulang", refresh), Ui.IconBtn(G.NewFolder, "Folder baru", newFolder));
        var head = Ui.Columns("Auto,*,Auto", 8, Ui.Row(7, Ui.Icon(glyph, 13, Theme.AccentBrush), Ui.Text(title, 13, FontWeights.SemiBold)), new Border(), extra);
        var bar = Ui.Columns("*,Auto", 6, pathBox, tools);
        var cols = Ui.Columns("26,*,90,130,90", 8, new Border(), Ui.Caps("Nama"), Ui.Caps("Ukuran").With(t => t.HorizontalAlignment = HorizontalAlignment.Right), Ui.Caps("Diubah"), Ui.Caps(title == "Server" ? "Izin" : ""));
        cols.Padding = new Thickness(12, 0, 12, 0);
        var root = Ui.Rows("Auto,Auto,Auto,*", 8, head, bar, cols, list);
        return new Border { Child = root, Padding = new Thickness(12), CornerRadius = new CornerRadius(14), Background = Theme.Card, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1) };
    }

    private ListView MakeList(List<FileItem> items, bool remote)
    {
        var lv = new ListView { SelectionMode = ListViewSelectionMode.Extended, CanDragItems = true, AllowDrop = true, IsDoubleTapEnabled = true };
        foreach (var it in items) lv.Items.Add(new ListViewItem { Content = Row(it, remote), Tag = it, Padding = new Thickness(4, 0, 4, 0), MinHeight = 32 });
        if (items.Count == 0)
            lv.Header = Ui.Secondary(remote && S.RemoteLoading ? "Memuat…" : "Folder kosong", 12).With(t => { t.Margin = new Thickness(14, 10, 0, 0); });
        lv.DoubleTapped += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not null) { }
            if (lv.SelectedItem is ListViewItem { Tag: FileItem f })
            {
                if (f.IsDir) { if (remote) _ = S.OpenRemote(f.Path); else S.OpenLocal(f.Path); }
                else if (remote) S.Download([f]);
                else if (S.Connected) S.Upload([f.Path]);
            }
        };
        lv.DragItemsStarting += (_, e) =>
        {
            var paths = e.Items.OfType<ListViewItem>().Select(i => ((FileItem)i.Tag).Path).ToList();
            e.Data.SetData(DragFormat, (remote ? "remote\n" : "local\n") + string.Join("\n", paths));
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };
        lv.DragOver += (_, e) =>
        {
            var ok = e.DataView.Contains(DragFormat) || (remote && e.DataView.Contains(StandardDataFormats.StorageItems));
            e.AcceptedOperation = ok ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (ok) e.DragUIOverride.Caption = remote ? "Upload ke server" : "Download ke sini";
        };
        lv.Drop += async (_, e) =>
        {
            if (e.DataView.Contains(DragFormat))
            {
                var text = (string)await e.DataView.GetDataAsync(DragFormat);
                var lines = text.Split('\n');
                var fromRemote = lines[0] == "remote";
                var paths = lines.Skip(1).Where(p => p.Length > 0).ToList();
                if (remote && !fromRemote) S.Upload(paths);
                else if (!remote && fromRemote) S.Download(S.RemoteItems.Where(i => paths.Contains(i.Path)).ToList());
            }
            else if (remote && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                S.Upload(items.Select(i => i.Path).ToList());
            }
        };
        lv.ContextFlyout = remote ? RemoteMenu(lv) : LocalMenu(lv);
        return lv;
    }

    private static List<FileItem> Selected(ListView lv) => lv.SelectedItems.OfType<ListViewItem>().Select(i => (FileItem)i.Tag).ToList();

    private UIElement Row(FileItem f, bool remote)
    {
        var icon = Ui.Icon(f.IsDir ? G.Folder : G.Document, 14, f.IsDir ? Theme.Brush("#F2C55A") : Theme.Text2);
        var size = Ui.Text(f.IsDir ? "—" : Ui.Bytes(f.Size), 11.5, fg: Theme.Text2).With(t => t.HorizontalAlignment = HorizontalAlignment.Right);
        var mod = Ui.Text(f.Modified?.LocalDateTime.ToString("d MMM yy HH.mm", Id) ?? "", 11.5, fg: Theme.Text2);
        var perm = Ui.Text(remote ? SftpModel.PermissionString(f.Permissions, f.IsDir) : "", 11, fg: Theme.Text3, mono: true);
        return Ui.Columns("26,*,90,130,90", 8, icon, Ui.Text(f.Name, 12.5), size, mod, perm);
    }

    private void RenderLocal()
    {
        _localList = MakeList(S.LocalItems, false);
        _localPane.Child = Pane("PC ini", G.Home, S.LocalPath, S.LocalUp,
            () => S.OpenLocal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), S.ReloadLocal,
            p => { if (System.IO.Directory.Exists(p)) S.OpenLocal(p); }, () => _ = NewFolder(false), _localList,
            Ui.Primary("Upload", G.Upload, () => { var sel = Selected(_localList); if (sel.Count > 0) S.Upload(sel.Select(f => f.Path).ToList()); }).With(b => b.IsEnabled = S.Connected));
    }

    private void RenderRemote()
    {
        _remoteList = MakeList(S.RemoteItems, true);
        _remotePane.Child = Pane("Server", G.Server, S.RemotePath, () => _ = S.RemoteUp(), () => _ = S.RemoteHome(), () => _ = S.RefreshRemote(),
            p => _ = S.OpenRemote(p), () => _ = NewFolder(true), _remoteList,
            Ui.Primary("Download", G.Download, () => { var sel = Selected(_remoteList); if (sel.Count > 0) S.Download(sel); }).With(b => b.IsEnabled = S.Connected));
    }

    private MenuFlyout RemoteMenu(ListView lv)
    {
        var f = new MenuFlyout();
        void Item(string t, string g, Action<List<FileItem>> a, bool single = false)
        {
            var i = new MenuFlyoutItem { Text = t, Icon = new FontIcon { Glyph = g } };
            i.Click += (_, _) => { var sel = Selected(lv); if (sel.Count > 0 && (!single || sel.Count == 1)) a(sel); };
            f.Items.Add(i);
        }
        Item("Download", G.Download, sel => S.Download(sel));
        Item("Buka", G.Folder, sel => { if (sel[0].IsDir) _ = S.OpenRemote(sel[0].Path); }, true);
        Item("Ganti nama…", G.Rename, sel => _ = Rename(sel[0], true), true);
        Item("Izin (chmod)…", G.Permissions, sel => _ = Chmod(sel[0]), true);
        Item("Salin path", G.Copy, sel => Ui.CopyText(string.Join("\n", sel.Select(x => x.Path))));
        f.Items.Add(new MenuFlyoutSeparator());
        Item("Hapus", G.Delete, async sel =>
        {
            if (await Ui.Confirm(sel.Count == 1 ? $"Hapus “{sel[0].Name}”?" : $"Hapus {sel.Count} item?", "File di server akan dihapus permanen (folder beserta isinya)."))
                await S.DeleteRemote(sel);
        });
        return f;
    }

    private MenuFlyout LocalMenu(ListView lv)
    {
        var f = new MenuFlyout();
        void Item(string t, string g, Action<List<FileItem>> a, bool single = false)
        {
            var i = new MenuFlyoutItem { Text = t, Icon = new FontIcon { Glyph = g } };
            i.Click += (_, _) => { var sel = Selected(lv); if (sel.Count > 0 && (!single || sel.Count == 1)) a(sel); };
            f.Items.Add(i);
        }
        Item("Upload ke server", G.Upload, sel => S.Upload(sel.Select(x => x.Path).ToList()));
        Item("Ganti nama…", G.Rename, sel => _ = Rename(sel[0], false), true);
        Item("Tampilkan di File Explorer", G.Folder, sel =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{sel[0].Path}\""); } catch { }
        }, true);
        f.Items.Add(new MenuFlyoutSeparator());
        Item("Hapus", G.Delete, async sel =>
        {
            if (await Ui.Confirm(sel.Count == 1 ? $"Hapus “{sel[0].Name}”?" : $"Hapus {sel.Count} item?", "File di PC ini akan dihapus permanen."))
                S.DeleteLocal(sel);
        });
        return f;
    }

    private async Task NewFolder(bool remote)
    {
        var name = Ui.Field("Nama folder");
        var d = Ui.Dialog("Folder baru", name, "Buat");
        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        if (await d.ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
        if (remote) await S.MkdirRemote(name.Text.Trim()); else S.MkdirLocal(name.Text.Trim());
    }

    private async Task Rename(FileItem item, bool remote)
    {
        var name = Ui.Field("Nama baru", item.Name);
        var d = Ui.Dialog("Ganti nama", name, "Simpan");
        name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        if (remote) await S.RenameRemote(item, name.Text.Trim()); else S.RenameLocal(item, name.Text.Trim());
    }

    private async Task Chmod(FileItem item)
    {
        var mode = item.Permissions & 0x1FF;
        var boxes = new List<CheckBox>();
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        string[] who = ["Pemilik", "Grup", "Lainnya"];
        string[] what = ["Baca", "Tulis", "Jalankan"];
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(80) : GridLength.Auto });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition());
        var octal = Ui.Field("", Convert.ToString(mode, 8).PadLeft(3, '0'), mono: true, width: 90);
        void UpdateOctal()
        {
            uint m = 0;
            for (int i = 0; i < 9; i++) if (boxes[i].IsChecked == true) m |= 1u << (8 - i);
            octal.Text = Convert.ToString(m, 8).PadLeft(3, '0');
        }
        for (int c = 0; c < 3; c++) { var t = Ui.Caps(what[c]); Grid.SetColumn(t, c + 1); grid.Children.Add(t); }
        for (int r = 0; r < 3; r++)
        {
            var l = Ui.Text(who[r], 12.5); Grid.SetRow(l, r + 1); grid.Children.Add(l);
            for (int c = 0; c < 3; c++)
            {
                var bit = 8 - (r * 3 + c);
                var cb = new CheckBox { IsChecked = (mode & (1u << bit)) != 0, MinWidth = 0 };
                cb.Click += (_, _) => UpdateOctal();
                Grid.SetRow(cb, r + 1); Grid.SetColumn(cb, c + 1);
                grid.Children.Add(cb);
                boxes.Add(cb);
            }
        }
        var content = Ui.Col(12, Ui.Text(item.Name, 13, FontWeights.SemiBold, mono: true), grid, Ui.Row(8, Ui.Text("Oktal", 12.5), octal));
        var d = Ui.Dialog("Izin file", content, "Terapkan");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try { await S.ChmodRemote(item, Convert.ToUInt32(octal.Text.Trim(), 8)); }
        catch { M.Flash("Format oktal tidak valid (mis. 755)"); }
    }

    // MARK: Transfer

    private void RenderTransfers()
    {
        if (S.Transfers.Count == 0) { _transfers.Child = null; _transfers.Visibility = Visibility.Collapsed; return; }
        _transfers.Visibility = Visibility.Visible;
        var head = Ui.Columns("*,Auto", 8, Ui.Row(8, Ui.Text("Transfer", 13, FontWeights.SemiBold), Ui.Pill(S.Transfers.Count(t => t.Status == Transfer.State.Running) + " berjalan")),
            Ui.Secondary("Bersihkan", null, S.ClearFinished));
        var list = Ui.Col(6);
        foreach (var t in S.Transfers.Take(30))
        {
            var bar = new ProgressBar { Maximum = 1, Value = t.Fraction, Height = 4 };
            var (text, color) = t.Status switch
            {
                Transfer.State.Running => (t.Total > 0 ? $"{Ui.Bytes(t.Done)} / {Ui.Bytes(t.Total)}" : "Menyiapkan…", Theme.Accent2),
                Transfer.State.Done => ("Selesai", Theme.Green),
                Transfer.State.Cancelled => ("Dibatalkan", "#94A3B8"),
                _ => (t.Error ?? "Gagal", Theme.Orange),
            };
            if (t.Status != Transfer.State.Running) bar.Foreground = Theme.Brush(color);
            var cancel = t.Status == Transfer.State.Running ? Ui.IconBtn(G.Close, "Batalkan", () => S.Cancel(t), size: 10) : null;
            var row = Ui.Columns("Auto,180,*,220,Auto", 10,
                Ui.Icon(t.Direction == Transfer.Kind.Upload ? G.Upload : G.Download, 13, Theme.AccentBrush),
                Ui.Text(t.Name, 12.5, FontWeights.Medium), bar, Ui.Text(text, 11.5, fg: Theme.Brush(color)).With(x => x.HorizontalAlignment = HorizontalAlignment.Right), cancel);
            if (t.Status == Transfer.State.Running && t.CurrentFile.Length > 0) ToolTipService.SetToolTip(row, t.CurrentFile);
            list.Children.Add(row);
        }
        _transfers.Child = Ui.Card(Ui.Col(8, head, new ScrollViewer { Content = list, MaxHeight = 150 }), 12, 12);
    }
}
