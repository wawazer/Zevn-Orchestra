using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Database: daftar koneksi, atau workspace ala phpMyAdmin untuk koneksi yang terbuka.</summary>
internal sealed class DatabasePage : PageBase
{
    private DbWorkspace? _ws;
    private readonly Action _wsChanged;
    private string _treeFilter = "";
    private string _overviewFilter = "";
    private TextBox? _sqlBox;

    public DatabasePage() : base("data", "db", "nav")
    {
        _wsChanged = () => DispatcherQueue.TryEnqueue(RenderWorkspace);
        Unloaded += (_, _) => { if (_ws != null) _ws.Changed -= _wsChanged; };
        Render();
    }

    protected override void Render()
    {
        if (_ws != M.DbWorkspace)
        {
            if (_ws != null) _ws.Changed -= _wsChanged;
            _ws = M.DbWorkspace;
            if (_ws != null) _ws.Changed += _wsChanged;
        }
        if (_ws == null) RenderList(); else RenderWorkspace();
    }

    // MARK: Daftar koneksi

    private static Border EngineBadge(DbPreset p, double size = 40) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26), Background = Theme.TintGradient(p.Color),
        Child = new TextBlock { Text = p.Abbr, FontSize = size * 0.36, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };

    private void RenderList()
    {
        Children.Clear();
        var header = Ui.Header("Database", "PostgreSQL, MySQL, SQL Server, SQLite, Redis, MongoDB, ClickHouse — langsung atau lewat tunnel SSH.",
            Ui.Primary("Koneksi Baru", G.Add, () => _ = PickEngine()));
        var col = Ui.Col(22, header);
        if (M.Databases.Length == 0)
            col.Children.Add(Ui.EmptyState(G.Database, "Belum ada koneksi database", "Simpan koneksi database (password ikut terenkripsi di vault), lalu jelajahi tabelnya seperti phpMyAdmin.",
                Ui.Primary("Koneksi Baru", G.Add, () => _ = PickEngine())));
        else
        {
            var rep = new ItemsRepeater
            {
                Layout = new UniformGridLayout { MinItemWidth = 280, MinItemHeight = 160, MinColumnSpacing = 14, MinRowSpacing = 14, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
                ItemsSource = M.Databases.Select((c, i) => (object)Card(c, i)).ToList()
            };
            col.Children.Add(rep);
        }
        Children.Add(Ui.Scroll(col));
    }

    private FrameworkElement Card(DbConnection c, int index)
    {
        var p = DbPreset.Find(c);
        var where = c.Engine == DbEngine.Sqlite ? c.FilePath : (M.HideAddresses && c.Host != "127.0.0.1" && c.Host != "localhost" ? "••••••••" : c.Host) + (c.Port > 0 ? $":{c.Port}" : "");
        var tunnel = M.HostById(c.SshHostId);
        var info = Ui.Col(3, Ui.Text(c.Title(), 15, FontWeights.SemiBold), Ui.Text(p.Name, 11.5, fg: Theme.Text2));
        var top = Ui.Columns("Auto,*,Auto", 12, EngineBadge(p), info, Ui.Row(2,
            Ui.IconBtn(G.Edit, "Edit", () => _ = Editor(c)),
            Ui.IconBtn(G.Delete, "Hapus", async () => { if (await Ui.Confirm($"Hapus koneksi “{c.Title()}”?", "Koneksi dan password-nya dihapus dari vault.")) M.Delete(c); }, Theme.Brush("#E5484D"))));
        var meta = Ui.Col(4,
            Ui.Row(6, Ui.Icon(c.Engine == DbEngine.Sqlite ? G.Document : G.Globe, 11, Theme.Text3), Ui.Text(where, 11.5, fg: Theme.Text2, mono: true)),
            tunnel != null ? Ui.Row(6, Ui.Icon(G.Forward, 11, Theme.Text3), Ui.Text($"Tunnel SSH lewat {tunnel.Title()}", 11.5, fg: Theme.Text2)) : null,
            string.IsNullOrEmpty(c.Database) ? null : Ui.Row(6, Ui.Icon(G.Database, 11, Theme.Text3), Ui.Text(c.Database, 11.5, fg: Theme.Text2)));
        var open = Ui.Primary("Buka", G.Bolt, () => _ = M.OpenDatabase(c)).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch);
        var card = Ui.HoverCard(Ui.Col(12, top, meta, open), 16, 16);
        card.DoubleTapped += (_, _) => _ = M.OpenDatabase(c);
        Ui.HoverLift(card);
        Ui.StaggerIn(card, index);
        return card;
    }

    private async Task PickEngine()
    {
        ContentDialog? d = null;
        var grid = new ItemsRepeater
        {
            Layout = new UniformGridLayout { MinItemWidth = 168, MinItemHeight = 64, MinColumnSpacing = 8, MinRowSpacing = 8, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
            ItemsSource = DbPreset.All.Select(p =>
            {
                var row = Ui.Columns("Auto,*", 10, EngineBadge(p, 34), Ui.Col(1, Ui.Text(p.Name, 12.5, FontWeights.SemiBold), Ui.Text(p.Hint, 10.5, fg: Theme.Text2)));
                var b = Ui.Plain(row, () => { d?.Hide(); _ = Editor(DbExt.Blank(p)); }, new Thickness(10));
                b.Background = Theme.Card; b.BorderBrush = Theme.CardStroke; b.BorderThickness = new Thickness(1); b.CornerRadius = new CornerRadius(10);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                return (object)b;
            }).ToList()
        };
        var content = new ScrollViewer { Content = grid, Height = 420, Width = 560 };
        d = Ui.Dialog("Pilih database", content, null);
        d.Resources["ContentDialogMaxWidth"] = 640.0;
        await d.ShowAsync();
    }

    private async Task Editor(DbConnection original)
    {
        var c = original;
        var p = DbPreset.Find(c);
        var label = Ui.Field("Nama (mis. DB produksi)", c.Label);
        var file = Ui.Field("Path file .sqlite / .db", c.FilePath, mono: true);
        var pickFile = Ui.Secondary("Pilih…", null, async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Hwnd);
            picker.FileTypeFilter.Add("*");
            var f = await picker.PickSingleFileAsync();
            if (f != null) { file.Text = f.Path; if (label.Text.Length == 0) label.Text = Path.GetFileNameWithoutExtension(f.Name); }
        });
        var host = Ui.Field(c.Engine == DbEngine.Mongodb ? "Host atau URI mongodb+srv://" : "Host", c.Host, mono: true);
        var port = Ui.Number(c.Port, 0, 65535, 100);
        var user = Ui.Field("Username", c.Username);
        var pass = new PasswordBox { PlaceholderText = "Password", Password = c.Password ?? "", PasswordRevealMode = PasswordRevealMode.Peek };
        var db = Ui.Field(c.Engine == DbEngine.Redis ? "Nomor database (0–15)" : "Database (kosong = semua)", c.Database);
        var tls = new ToggleSwitch { IsOn = c.Tls, OnContent = "TLS / SSL", OffContent = "TLS / SSL" };
        var tunnel = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        tunnel.Items.Add(new ComboBoxItem { Content = "Tanpa tunnel — koneksi langsung", Tag = "" });
        foreach (var h in M.Hosts) tunnel.Items.Add(new ComboBoxItem { Content = $"Lewat {h.Title()}", Tag = h.Id });
        tunnel.SelectedItem = tunnel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (c.SshHostId ?? "")) ?? tunnel.Items[0];
        var tunnelNote = Ui.Secondary("Host & port di atas dilihat dari sisi server SSH, biasanya 127.0.0.1.", 11.5, true);
        void UpdateNote() => tunnelNote.Visibility = string.IsNullOrEmpty((tunnel.SelectedItem as ComboBoxItem)?.Tag as string) ? Visibility.Collapsed : Visibility.Visible;
        tunnel.SelectionChanged += (_, _) => UpdateNote();
        UpdateNote();
        var result = new InfoBar { IsOpen = false, IsClosable = false };
        var testing = new ProgressRing { Width = 14, Height = 14, IsActive = false };

        DbConnection Current() => c with
        {
            Label = label.Text.Trim(), FilePath = file.Text.Trim(), Host = host.Text.Trim(),
            Port = (ushort)(double.IsNaN(port.Value) ? 0 : port.Value), Username = user.Text.Trim(),
            Password = pass.Password.Length == 0 ? null : pass.Password, Database = db.Text.Trim(), Tls = tls.IsOn,
            SshHostId = (tunnel.SelectedItem as ComboBoxItem)?.Tag is string s && s.Length > 0 ? s : null,
        };

        var test = Ui.Secondary("Tes koneksi", G.Forward, async () =>
        {
            testing.IsActive = true;
            try
            {
                var x = Current();
                var s = await DbSession.Connect(x, M.SshTarget(x.SshHostId));
                int n = 0;
                try { n = (await s.Tables()).Length; } catch { }
                result.Severity = InfoBarSeverity.Success;
                result.Message = $"Berhasil tersambung — {n} tabel/koleksi/key ditemukan";
            }
            catch (Exception e)
            {
                result.Severity = InfoBarSeverity.Warning;
                result.Message = Errors.Text(e);
            }
            result.IsOpen = true;
            testing.IsActive = false;
        });

        var col = Ui.Col(10, Ui.Row(10, EngineBadge(p, 30), Ui.Text(p.Name, 14, FontWeights.SemiBold)), label);
        if (c.Engine == DbEngine.Sqlite) col.Children.Add(Ui.Columns("*,Auto", 8, file, pickFile));
        else
        {
            col.Children.Add(Ui.Columns("*,Auto", 8, host, port));
            if (c.Engine != DbEngine.Redis || true) col.Children.Add(Ui.Columns("*,*", 8, user, pass));
            col.Children.Add(db);
            col.Children.Add(tls);
            col.Children.Add(Ui.Card(Ui.Col(6, Ui.Text("Tunnel SSH", 12, FontWeights.SemiBold), tunnel, tunnelNote), 12, 10));
        }
        col.Children.Add(result);
        col.Children.Add(Ui.Row(8, test, testing));
        col.Width = 480;
        var d = Ui.Dialog(string.IsNullOrEmpty(original.Id) ? "Koneksi baru" : "Edit koneksi", new ScrollViewer { Content = col, MaxHeight = 560 }, "Simpan & Buka");
        d.SecondaryButtonText = "Simpan";
        var r = await d.ShowAsync();
        if (r == ContentDialogResult.None) return;
        var saved = Current();
        if (saved.Label.Length == 0) saved = saved with { Label = saved.Title() };
        M.Save(saved);
        if (r == ContentDialogResult.Primary)
        {
            var conn = string.IsNullOrEmpty(original.Id)
                ? M.Databases.LastOrDefault(x => x.Label == saved.Label && x.Engine == saved.Engine)
                : M.Databases.FirstOrDefault(x => x.Id == original.Id);
            if (conn != null) await M.OpenDatabase(conn);
        }
    }

    // MARK: Workspace

    private void RenderWorkspace()
    {
        var ws = _ws;
        if (ws == null) { RenderList(); return; }
        Children.Clear();
        if (ws.State == DbWorkspace.Status.Connecting)
        {
            Children.Add(Ui.Col(12, new ProgressRing { IsActive = true, Width = 34, Height = 34 }, Ui.Secondary($"Menyambung ke {ws.Conn.Title()}…"))
                .With(c => { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; }));
            return;
        }
        if (ws.State == DbWorkspace.Status.Failed)
        {
            var col = Ui.Col(12,
                Ui.Icon(G.Warning, 34, Theme.Brush(Theme.Orange)).With(i => i.HorizontalAlignment = HorizontalAlignment.Center),
                Ui.Text("Gagal tersambung", 18, FontWeights.Bold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                Ui.Secondary(ws.FailMessage ?? "", 13, true).With(t => { t.MaxWidth = 480; t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center; }),
                Ui.Row(8, Ui.Primary("Coba lagi", G.Refresh, () => _ = ws.Connect(M.SshTarget(ws.Conn.SshHostId))), Ui.Secondary("Kembali", null, M.CloseDatabase))
                    .With(r => r.HorizontalAlignment = HorizontalAlignment.Center));
            col.HorizontalAlignment = HorizontalAlignment.Center;
            col.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(col);
            return;
        }
        var main = Main(ws);
        // Daftar database & tabel di kiri bisa disembunyikan (diingat antar sesi)
        var g = Settings.Bool(Settings.DbShowTree, true) ? Ui.Columns("268,*", 0, Tree(ws), main) : Ui.Columns("*", 0, main);
        Children.Add(g);
    }

    private UIElement Tree(DbWorkspace ws)
    {
        var p = DbPreset.Find(ws.Conn);
        var head = Ui.Columns("Auto,*,Auto", 10, EngineBadge(p, 30), Ui.Col(1, Ui.Text(ws.Conn.Title(), 13, FontWeights.SemiBold), Ui.Text(p.Name, 10.5, fg: Theme.Text2)),
            Ui.IconBtn(G.Close, "Tutup koneksi", M.CloseDatabase, size: 10));
        var list = Ui.Col(1);
        var search = new TextBox { PlaceholderText = "Cari tabel…", Text = _treeFilter };
        search.TextChanged += (_, _) => { _treeFilter = search.Text; RefreshTreeList(ws, list); };
        RefreshTreeList(ws, list);
        var root = Ui.Rows("Auto,Auto,*", 10, head, search, new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Padding = new Thickness(12);
        return new Border { Child = root, Background = Theme.Panel, BorderBrush = Theme.Ink(0.07), BorderThickness = new Thickness(0, 0, 1, 0) };
    }

    private void RefreshTreeList(DbWorkspace ws, StackPanel list)
    {
        list.Children.Clear();
        list.Children.Add(Ui.Caps("Database").With(t => t.Margin = new Thickness(8, 4, 0, 4)));
        foreach (var db in ws.UserDatabases) AddDbNode(ws, list, db);
        var sys = ws.SystemDbs;
        if (sys.Count > 0)
        {
            var sysHead = Ui.Plain(Ui.Row(6, Ui.Icon(ws.ShowSystem ? G.ChevronDown : G.ChevronRight, 9, Theme.Text3), Ui.Caps("Sistem"), Ui.Pill(sys.Count.ToString())),
                () => { ws.ShowSystem = !ws.ShowSystem; RefreshTreeList(ws, list); }, new Thickness(8, 8, 8, 4));
            sysHead.HorizontalAlignment = HorizontalAlignment.Stretch;
            list.Children.Add(sysHead);
            if (ws.ShowSystem) foreach (var db in sys) AddDbNode(ws, list, db);
        }
    }

    private void AddDbNode(DbWorkspace ws, StackPanel list, string db)
    {
        var expanded = ws.Expanded.Contains(db);
        var tables = ws.DbTables.TryGetValue(db, out var t) ? t : null;
        var filter = _treeFilter.Trim();
        var shownTables = tables?.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filter.Length > 0 && shownTables is { Count: 0 } && !db.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        var selected = ws.SelectedDb == db && ws.SelectedTable == null;
        var chevron = Ui.IconBtn(expanded ? G.ChevronDown : G.ChevronRight, expanded ? "Tutup" : "Buka", () => _ = ws.Toggle(db), size: 9);
        chevron.Padding = new Thickness(4);
        var count = tables != null ? Ui.Text(tables.Length.ToString(), 10, FontWeights.SemiBold, Theme.Text2) : ws.LoadingDbs.Contains(db) ? (UIElement)new ProgressRing { Width = 10, Height = 10, IsActive = true } : null;
        var label = Ui.Plain(Ui.Columns("Auto,*,Auto", 7, Ui.Icon(G.Database, 12, Theme.Brush(selected ? Theme.Accent : "#F97316")),
            Ui.Text(db, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal), count), () => _ = ws.SelectDatabase(db), new Thickness(6, 5, 8, 5));
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.Background = selected ? Theme.Brush(Theme.Accent, 0.12) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (ws.ActiveDatabase == db) ToolTipService.SetToolTip(label, "Database aktif");
        list.Children.Add(Ui.Columns("Auto,*", 0, chevron, label));
        if (!expanded || shownTables == null) return;
        if (shownTables.Count == 0)
        {
            list.Children.Add(Ui.Text("Tidak ada tabel", 11, fg: Theme.Text3).With(x => x.Margin = new Thickness(40, 2, 0, 4)));
            return;
        }
        foreach (var table in shownTables.Take(800))
        {
            var sel = ws.SelectedDb == db && ws.SelectedTable?.Name == table.Name && ws.SelectedTable?.Schema == table.Schema;
            var row = Ui.Columns("Auto,*,Auto", 7, Ui.Icon(table.IsView ? G.Eye : G.Table, 11, sel ? Theme.AccentBrush : Theme.Text2),
                Ui.Text(table.Name, 12, sel ? FontWeights.SemiBold : FontWeights.Normal, sel ? Theme.Text : Theme.Text2),
                table.RowsText() is { } rt ? Ui.Text(rt, 10, fg: Theme.Text3) : null);
            var b = Ui.Plain(row, () => _ = ws.OpenTable(table, db), new Thickness(30, 4, 8, 4));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Background = sel ? Theme.Brush(Theme.Accent, 0.12) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            list.Children.Add(b);
        }
    }

    private UIElement Main(DbWorkspace ws)
    {
        // Breadcrumb
        var crumbs = Ui.Row(6, Ui.Plain(Ui.Row(6, Ui.Icon(G.Server, 11, Theme.Text2), Ui.Text("Server", 12.5, fg: Theme.Text2)), ws.ShowServer, new Thickness(4)));
        if (ws.SelectedDb is { } sdb)
        {
            crumbs.Children.Add(Ui.Icon(G.ChevronRight, 9, Theme.Text3));
            crumbs.Children.Add(Ui.Plain(Ui.Row(6, Ui.Icon(G.Database, 11, Theme.Brush("#F97316")), Ui.Text(sdb, 12.5, ws.SelectedTable == null ? FontWeights.SemiBold : FontWeights.Normal)),
                () => _ = ws.SelectDatabase(sdb), new Thickness(4)));
        }
        if (ws.SelectedTable is { } st)
        {
            crumbs.Children.Add(Ui.Icon(G.ChevronRight, 9, Theme.Text3));
            crumbs.Children.Add(Ui.Row(6, Ui.Icon(st.IsView ? G.Eye : G.Table, 11, Theme.AccentBrush), Ui.Text(st.Name, 12.5, FontWeights.SemiBold)));
        }
        var showTree = Settings.Bool(Settings.DbShowTree, true);
        var treeBtn = Ui.IconBtn(showTree ? "\uE89F" : "\uE8A0", showTree ? "Sembunyikan daftar tabel (Ctrl+Alt+S)" : "Tampilkan daftar tabel (Ctrl+Alt+S)", ToggleTree);
        var treeKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu };
        treeKey.Invoked += (_, e) => { e.Handled = true; ToggleTree(); };
        treeBtn.KeyboardAccelerators.Add(treeKey);
        treeBtn.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        crumbs.Children.Insert(0, treeBtn);
        // Tombol tutup koneksi ada di kepala daftar tabel; saat daftar disembunyikan, tampilkan di sini
        if (!showTree) crumbs.Children.Insert(0, Ui.IconBtn(G.Back, "Tutup koneksi", M.CloseDatabase));
        var actions = Ui.Row(6,
            ws.Running ? new ProgressRing { Width = 16, Height = 16, IsActive = true } : null,
            Ui.IconBtn(G.Refresh, "Muat ulang", () => _ = ws.Refresh()),
            ws.ShownResult is { Columns.Length: > 0 } ? Ui.Secondary("Ekspor CSV", G.Export, () => _ = ExportCsv(ws)) : null);
        var top = Ui.Columns("*,Auto", 8, crumbs, actions);

        // Tab
        var tabs = Ui.Row(4);
        foreach (var t in ws.AvailableTabs)
        {
            var on = ws.CurrentTab == t;
            var b = Ui.Plain(Ui.Row(7, Ui.Icon(DbWorkspace.TabGlyph(t), 12, on ? Theme.AccentBrush : Theme.Text2), Ui.Text(DbWorkspace.TabTitle(t), 13, on ? FontWeights.SemiBold : FontWeights.Normal, on ? Theme.Text : Theme.Text2)),
                () => _ = ws.SetTab(t), new Thickness(12, 7, 12, 7));
            if (on) { b.Background = Theme.Ink(0.08); }
            tabs.Children.Add(b);
        }

        UIElement content = ws.CurrentTab switch
        {
            DbWorkspace.Tab.Browse => Browse(ws),
            DbWorkspace.Tab.Structure => ws.Structure is { } s ? new ResultGrid(s, 0, "Kunci") : Loading(ws),
            DbWorkspace.Tab.Search => SearchForm(ws),
            DbWorkspace.Tab.Sql => Sql(ws),
            _ => ws.SelectedDb == null ? ServerOverview(ws) : DbOverview(ws),
        };
        var error = ws.Error is { } e ? new InfoBar { Severity = InfoBarSeverity.Error, Message = e, IsOpen = true, IsClosable = true }.With(i => i.Closed += (_, _) => ws.Error = null) : null;
        var root = Ui.Rows("Auto,Auto,Auto,*", 10, top, tabs, error, content);
        root.Padding = new Thickness(20, 14, 20, 16);
        return root;
    }

    private static UIElement Loading(DbWorkspace ws) => ws.Running
        ? new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        : new Border();

    private UIElement Browse(DbWorkspace ws)
    {
        if (ws.Page is not { } page) return Loading(ws);
        var shown = (ulong)page.Rows.Length;
        var size = ws.PageSize;
        var total = ws.TotalRows;
        var id = new CultureInfo("id-ID");
        var range = shown == 0 ? "0 baris" : $"Baris {(ws.Offset + 1).ToString("N0", id)}–{(ws.Offset + shown).ToString("N0", id)}";
        var of = total is { } t ? $" dari {(ws.TotalIsEstimate ? "±" : "")}{t.ToString("N0", id)}" : "";
        var info = Ui.Row(6, Ui.Text(range + of, 12.5, FontWeights.Medium), Ui.Text($"· {page.ElapsedMs} ms", 12, fg: Theme.Text2));
        var sizeBox = new ComboBox { MinWidth = 140 };
        foreach (var n in new uint[] { 25, 50, 100, 250, 500 }) sizeBox.Items.Add(new ComboBoxItem { Content = $"{n} / halaman", Tag = n });
        sizeBox.SelectedItem = sizeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (uint)i.Tag == size);
        sizeBox.SelectionChanged += (_, _) => { if (sizeBox.SelectedItem is ComboBoxItem { Tag: uint n } && n != ws.PageSize) _ = ws.SetPageSize(n); };
        var hasPrev = ws.Offset > 0;
        var hasNext = total is { } tt ? ws.Offset + shown < tt : shown == size;
        ulong lastOffset = total is { } t2 && t2 > 0 ? (t2 - 1) / size * size : ws.Offset;
        var pageNo = total is { } t3 ? $"{ws.Offset / size + 1} / {Math.Max(1, (t3 + size - 1) / size)}" : $"{ws.Offset / size + 1}";
        Button Nav(string g, string tip, bool en, Func<Task> a) => Ui.IconBtn(g, tip, () => _ = a()).With(b => b.IsEnabled = en);
        var pager = Ui.Row(2,
            Nav(G.First, "Halaman pertama", hasPrev, () => ws.GoToPage(0)),
            Nav(G.Prev, "Sebelumnya", hasPrev, () => ws.GoToPage(ws.Offset >= size ? ws.Offset - size : 0)),
            Ui.Text(pageNo, 12, fg: Theme.Text2).With(x => x.Margin = new Thickness(8, 0, 8, 0)),
            Nav(G.NextPage, "Berikutnya", hasNext, () => ws.GoToPage(ws.Offset + size)),
            Nav(G.Last, "Halaman terakhir", hasNext && total != null, () => ws.GoToPage(lastOffset)));
        var bar = Ui.Columns("*,Auto,Auto", 10, info, sizeBox, pager);
        UIElement grid = page.Rows.Length == 0 && page.Columns.Length > 0 && ws.IsFiltered
            ? Ui.Secondary("Tidak ada baris yang cocok", 13).With(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.VerticalAlignment = VerticalAlignment.Center; })
            : new ResultGrid(page, ws.Offset);
        return ws.Conn.Engine.IsSql()
            ? Ui.Rows("Auto,Auto,*", 10, BrowseBar(ws), bar, grid)
            : Ui.Rows("Auto,*", 10, bar, grid);
    }

    private void ToggleTree()
    {
        Settings.Set(Settings.DbShowTree, !Settings.Bool(Settings.DbShowTree, true));
        RenderWorkspace();
    }

    /// <summary>Cari cepat di semua kolom + keterangan filter aktif.</summary>
    private UIElement BrowseBar(DbWorkspace ws)
    {
        var box = new TextBox { PlaceholderText = "Cari di tabel ini (semua kolom) — Enter", Text = ws.QuickSearch, Width = 320 };
        box.TextChanged += (_, _) => ws.QuickSearch = box.Text;
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = ws.SubmitQuickSearch(); } };
        var left = Ui.Row(10, box);
        if (ws.IsFiltered)
        {
            var summary = Ui.Text(ws.FilterSummary, 12.5, fg: Theme.Text2).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = 420; });
            left.Children.Add(Ui.Row(6, Ui.Icon(G.Filter, 12, Theme.AccentBrush), summary,
                Ui.Plain(Ui.Text("Hapus filter", 12.5, fg: Theme.AccentBrush), () => _ = ws.ClearFilters(), new Thickness(4))));
        }
        var perColumn = Ui.Plain(Ui.Row(6, Ui.Icon(G.Search, 12, Theme.Text2), Ui.Text("Cari per kolom", 12.5, fg: Theme.Text2)),
            () => _ = ws.SetTab(DbWorkspace.Tab.Search), new Thickness(6, 4, 6, 4));
        return Ui.Columns("*,Auto", 10, left, perColumn);
    }

    /// <summary>Tab "Cari" ala phpMyAdmin: satu baris per kolom — operator & nilai — lalu Kirim.</summary>
    private UIElement SearchForm(DbWorkspace ws)
    {
        if (ws.SearchForm.Count == 0) return Loading(ws);
        const string cols = "170,120,170,*";
        var list = Ui.Col(0);
        list.Children.Add(Ui.Columns(cols, 12, Ui.Caps("Kolom"), Ui.Caps("Jenis"), Ui.Caps("Operator"), Ui.Caps("Nilai"))
            .With(g => g.Padding = new Thickness(14, 10, 14, 8)));
        var count = Ui.Text("", 12.5, fg: Theme.Text2);
        void UpdateCount()
        {
            var n = ws.SearchForm.Count(f => f.IsActive);
            count.Text = n == 0 ? "Belum ada syarat — Kirim menampilkan semua baris" : $"{n} syarat";
        }
        foreach (var f in ws.SearchForm)
        {
            var op = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var o in f.Ops) op.Items.Add(o);
            op.SelectedItem = f.Op;
            var value = Ui.Field(Placeholder(f), f.Value, mono: true);
            value.IsEnabled = f.NeedsValue;
            op.SelectionChanged += (_, _) =>
            {
                f.Op = op.SelectedItem as string ?? "=";
                value.IsEnabled = f.NeedsValue;
                value.PlaceholderText = Placeholder(f);
                UpdateCount();
            };
            value.TextChanged += (_, _) => { f.Value = value.Text; UpdateCount(); };
            value.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = ws.SubmitSearch(); } };
            var row = Ui.Columns(cols, 12,
                Ui.Text(f.Column, 13, FontWeights.SemiBold).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis),
                Ui.Text(f.Type, 12, fg: Theme.Text2).With(t => { t.FontFamily = Theme.MonoFont; t.TextTrimming = TextTrimming.CharacterEllipsis; }),
                op, value);
            row.Padding = new Thickness(14, 5, 14, 5);
            list.Children.Add(new Border { Height = 1, Background = Theme.Ink(0.06) });
            list.Children.Add(row);
        }
        UpdateCount();
        var card = new Border { Child = list, Background = Theme.Card, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        var hint = Ui.Row(8, Ui.Icon(G.Search, 12, Theme.Text2), Ui.Secondary("Kueri dengan contoh — isi kolom yang ingin dicari, kosongkan sisanya. Wildcard: %", 12.5));
        var reset = Ui.Secondary("Reset", null, () => { foreach (var f in ws.SearchForm) f.Value = ""; RenderWorkspace(); });
        var send = Ui.Primary("Kirim", G.Search, () => _ = ws.SubmitSearch());
        var footer = Ui.Columns("*,Auto,Auto", 8, count, reset, send);
        return Ui.Rows("Auto,*,Auto", 12, hint, Ui.Scroll(card, 0), footer);
    }

    private static string Placeholder(DbWorkspace.SearchField f) => f.Op switch
    {
        "IN (...)" or "NOT IN (...)" => "nilai1, nilai2, …",
        "BETWEEN" or "NOT BETWEEN" => "awal, akhir",
        "LIKE" or "NOT LIKE" => "mis. abc%",
        _ => f.NeedsValue ? "" : "—",
    };

    private UIElement Sql(DbWorkspace ws)
    {
        var box = new TextBox
        {
            Text = ws.Query, PlaceholderText = ws.Conn.Engine.Placeholder(), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.MonoFont, FontSize = 13, Height = 130, IsSpellCheckEnabled = false
        };
        box.TextChanged += (_, _) => ws.Query = box.Text;
        var run = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        run.Invoked += (s, e) => { e.Handled = true; _ = ws.Run(); };
        box.KeyboardAccelerators.Add(run);
        _sqlBox = box;
        var hist = new DropDownButton { Content = Ui.Row(6, Ui.Icon(G.History, 12), Ui.Text("Riwayat", 12.5)), IsEnabled = ws.History.Count > 0 };
        var hf = new MenuFlyout();
        foreach (var h in ws.History)
        {
            var i = new MenuFlyoutItem { Text = h.Length > 90 ? h[..90] + "…" : h };
            i.Click += (_, _) => { ws.Query = h; box.Text = h; };
            hf.Items.Add(i);
        }
        hist.Flyout = hf;
        var runBtn = Ui.Primary("Jalankan", G.Play, () => _ = ws.Run());
        ToolTipService.SetToolTip(runBtn, "Ctrl+Enter");
        runBtn.IsEnabled = !ws.Running;
        UIElement status = ws.Result is { } r
            ? Ui.Row(6, Ui.Text(r.Columns.Length == 0 ? (r.Affected is { } a ? $"{a} baris terpengaruh" : "Selesai") : $"{r.Rows.Length:N0} baris" + (r.Truncated ? " (dipotong)" : ""), 12.5, FontWeights.Medium),
                Ui.Text($"· {r.ElapsedMs} ms", 12, fg: Theme.Text2))
            : Ui.Text("Tulis query lalu tekan Jalankan", 12, fg: Theme.Text2);
        var bar = Ui.Columns("*,Auto,Auto", 8, status, hist, runBtn);
        UIElement result = ws.Result is { } res ? new ResultGrid(res) : new Border();
        return Ui.Rows("Auto,Auto,*", 10, box, bar, result);
    }

    private UIElement ServerOverview(DbWorkspace ws)
    {
        var head = Ui.Col(3, Ui.Text("Pilih database", 18, FontWeights.Bold), Ui.Secondary($"{ws.UserDatabases.Count} database pengguna · {ws.SystemDbs.Count} database sistem"));
        var rep = new ItemsRepeater
        {
            Layout = new UniformGridLayout { MinItemWidth = 200, MinItemHeight = 64, MinColumnSpacing = 10, MinRowSpacing = 10, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
            ItemsSource = ws.UserDatabases.Concat(ws.ShowSystem ? ws.SystemDbs : []).Select((db, i) =>
            {
                var n = ws.DbTables.TryGetValue(db, out var t) ? $"{t.Length} tabel" : "";
                var b = Ui.Plain(Ui.Columns("Auto,*", 10, Ui.TintIcon(G.Database, "#F97316", 32, 13, 9), Ui.Col(1, Ui.Text(db, 13, FontWeights.SemiBold), Ui.Text(n, 11, fg: Theme.Text2))),
                    () => _ = ws.SelectDatabase(db), new Thickness(12));
                b.Background = Theme.Card; b.BorderBrush = Theme.CardStroke; b.BorderThickness = new Thickness(1); b.CornerRadius = new CornerRadius(12);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                Ui.StaggerIn(b, i);
                return (object)b;
            }).ToList()
        };
        return new ScrollViewer { Content = Ui.Col(16, head, rep) };
    }

    private UIElement DbOverview(DbWorkspace ws)
    {
        var db = ws.SelectedDb!;
        var tables = ws.DbTables.TryGetValue(db, out var t) ? t : null;
        if (tables == null) return Loading(ws);
        var totalRows = tables.Sum(x => (decimal)(x.Rows ?? 0));
        var totalSize = tables.Aggregate(0UL, (a, x) => a + (x.SizeBytes ?? 0));
        var id = new CultureInfo("id-ID");
        UIElement Stat(string label, string value, string glyph) => Ui.Card(Ui.Row(10, Ui.Icon(glyph, 16, Theme.AccentBrush), Ui.Col(1, Ui.Text(value, 16, FontWeights.Bold), Ui.Text(label, 11, fg: Theme.Text2))), 12, 12);
        var stats = Ui.Row(10, Stat("Tabel", tables.Count(x => !x.IsView).ToString(), G.Table), Stat("View", tables.Count(x => x.IsView).ToString(), G.Eye),
            Stat("Perkiraan baris", totalRows.ToString("N0", id), G.List), Stat("Ukuran", totalSize > 0 ? Ui.Bytes(totalSize) : "—", G.Database));
        var filter = new TextBox { PlaceholderText = "Filter tabel", Text = _overviewFilter, Width = 220 };
        var listHost = new Border();
        void RenderRows()
        {
            var f = _overviewFilter.Trim();
            var shown = tables.Where(x => f.Length == 0 || x.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            var header = Ui.Columns("*,80,110,100", 10, Ui.Caps("Tabel"), Ui.Caps("Tipe"), Ui.Caps("Baris").With(x => x.HorizontalAlignment = HorizontalAlignment.Right), Ui.Caps("Ukuran").With(x => x.HorizontalAlignment = HorizontalAlignment.Right));
            header.Padding = new Thickness(12, 0, 12, 4);
            var rows = new ItemsRepeater
            {
                ItemsSource = Enumerable.Range(0, shown.Count).ToList(),
                ItemTemplate = new RowFactory(i =>
                {
                    var x = shown[i];
                    var row = Ui.Columns("*,80,110,100", 10,
                        Ui.Row(8, Ui.Icon(x.IsView ? G.Eye : G.Table, 12, Theme.Text2), Ui.Text(x.Name, 12.5, FontWeights.Medium)),
                        Ui.Text(x.IsView ? "View" : "Tabel", 11.5, fg: Theme.Text2),
                        Ui.Text(x.RowsText() ?? "—", 11.5, fg: Theme.Text2, mono: true).With(y => y.HorizontalAlignment = HorizontalAlignment.Right),
                        Ui.Text(x.SizeText() ?? "—", 11.5, fg: Theme.Text2, mono: true).With(y => y.HorizontalAlignment = HorizontalAlignment.Right));
                    var b = Ui.Plain(row, () => _ = ws.OpenTable(x, db), new Thickness(12, 7, 12, 7));
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    ToolTipService.SetToolTip(b, "Telusuri →");
                    return b;
                })
            };
            listHost.Child = shown.Count == 0
                ? Ui.Secondary(tables.Length == 0 ? "Database ini belum punya tabel" : "Tidak ada tabel yang cocok").With(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.Margin = new Thickness(0, 40, 0, 0); })
                : Ui.Rows("Auto,*", 0, header, new ScrollViewer { Content = rows });
        }
        filter.TextChanged += (_, _) => { _overviewFilter = filter.Text; RenderRows(); };
        RenderRows();
        var card = Ui.Card(Ui.Rows("Auto,*", 10, Ui.Columns("*,Auto", 8, Ui.Text(db, 15, FontWeights.SemiBold), filter), listHost), 12, 12);
        return Ui.Rows("Auto,*", 12, stats, card);
    }

    private async Task ExportCsv(DbWorkspace ws)
    {
        if (ws.ShownResult is not { } r || r.Columns.Length == 0) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = (ws.SelectedTable?.Name ?? "hasil") };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Hwnd);
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        var f = await picker.PickSaveFileAsync();
        if (f == null) return;
        await File.WriteAllTextAsync(f.Path, DbWorkspace.ToCsv(r), new System.Text.UTF8Encoding(true));
        M.Flash($"Disimpan ke {f.Name}");
    }
}
