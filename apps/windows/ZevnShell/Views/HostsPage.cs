using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Daftar host: grid / list, grup, cari, multi-pilih (Ctrl+klik), klik dua kali untuk connect.</summary>
internal sealed class HostsPage : PageBase
{
    private string _query = "";
    private readonly StackPanel _groups = new() { Spacing = 26 };
    private readonly Border _selectionBar = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24) };
    private TextBlock? _subtitle;

    private bool IsGrid => (Settings.Str(Settings.HostLayout) ?? "grid") == "grid";

    public HostsPage() : base("data", "selection", "sessions", "logs", "editing")
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = Ui.Col(26);
        body.Padding = new Thickness(24, 22, 24, 100);
        body.Children.Add(Header());
        body.Children.Add(_groups);
        scroll.Content = body;
        Children.Add(scroll);
        Children.Add(_selectionBar);
        Render();
    }

    private UIElement Header()
    {
        _subtitle = Ui.Secondary("");
        var crumb = Ui.Row(6, Ui.Caps("Workspace"), Ui.Text("/", 10.5, fg: Theme.Text3), Ui.Caps("Hosts"));
        var title = Ui.Row(10, new TextBlock { Text = "Hosts", FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center }, _subtitle);
        var left = Ui.Col(6, crumb, title);
        left.MinWidth = 96;
        ToolTipService.SetToolTip(left, "Ctrl+klik untuk memilih beberapa host · klik dua kali untuk connect");
        var layout = Ui.SegmentedIcons([(G.Grid, "Tampilan grid"), (G.List, "Tampilan daftar")], IsGrid ? 0 : 1,
            i => { Settings.Set(Settings.HostLayout, i == 0 ? "grid" : "list"); Render(); RerenderHeader(); });
        layout.Width = 70;
        layout.Name = "layoutSwitch";
        var eye = Ui.IconBtn(M.HideAddresses ? G.EyeOff : G.Eye, M.HideAddresses ? "Tampilkan alamat host" : "Sembunyikan alamat host", () => { M.HideAddresses = !M.HideAddresses; RerenderHeader(); });
        eye.Background = Theme.Ink(0.05);
        eye.Height = 30;
        eye.Width = 30;
        var search = Ui.Search("Cari host, IP, tag…", _query, q => { _query = q; Render(); }, M.EditingHost == null ? 230 : 150);
        var add = Ui.Primary("Host Baru", G.Add, () => M.Edit(HostExt.Blank()));
        add.Height = 32;
        ToolTipService.SetToolTip(add, "Host baru (Ctrl+N)");
        var right = Ui.Row(8, layout, eye, search, add);
        right.VerticalAlignment = VerticalAlignment.Bottom;
        return Ui.Columns("*,Auto", 12, left, right).With(g => g.Name = "header");
    }

    private void RerenderHeader()
    {
        if (Children[0] is ScrollViewer { Content: StackPanel body })
        {
            body.Children[0] = Header();
            Render();
        }
    }

    private Host[] Filtered()
    {
        var q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0) return M.Hosts;
        return M.Hosts.Where(h => new[] { h.Label, h.Hostname, h.Username, h.Group ?? "" }.Concat(h.Tags).Any(s => s.ToLowerInvariant().Contains(q))).ToArray();
    }

    protected override void OnModelChanged(string what)
    {
        if (what == "editing") RerenderHeader(); else Render();
    }

    protected override void Render()
    {
        if (_subtitle != null)
        {
            var online = M.OnlineCount;
            _subtitle.Text = $"{M.Hosts.Length} server" + (online > 0 ? $"  ·  ● {online} terhubung" : "");
        }
        _groups.Children.Clear();
        var hosts = Filtered();
        if (M.Hosts.Length == 0)
        {
            _groups.Children.Add(Ui.EmptyState(G.Server, "Belum ada host", "Tambahkan server pertamamu, lalu connect dengan satu klik.",
                Ui.Primary("Tambah host", G.Add, () => M.Edit(HostExt.Blank()))));
        }
        else if (hosts.Length == 0)
        {
            _groups.Children.Add(Ui.Secondary($"Tidak ada host yang cocok dengan “{_query}”").With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 60, 0, 0); }));
        }
        else
        {
            var groups = hosts.GroupBy(h => string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group!)
                .OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var g in groups)
            {
                var title = Ui.Row(8, Ui.Caps(g.Key == "" ? "Hosts" : g.Key), Ui.Pill(g.Count().ToString()));
                var line = new Border { Height = 1, Background = Theme.Ink(0.07), VerticalAlignment = VerticalAlignment.Center };
                var head = Ui.Columns("Auto,*", 10, title, line);
                UIElement items;
                if (IsGrid)
                {
                    var rep = new ItemsRepeater
                    {
                        Layout = new UniformGridLayout { MinItemWidth = 260, MinItemHeight = 182, MinColumnSpacing = 14, MinRowSpacing = 14, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
                        ItemsSource = g.Select(h => (object)Tile(h, true, n++)).ToList()
                    };
                    items = rep;
                }
                else
                {
                    var col = Ui.Col(6);
                    foreach (var h in g) col.Children.Add(Tile(h, false, n++));
                    items = col;
                }
                _groups.Children.Add(Ui.Col(12, head, items));
            }
        }
        RenderSelectionBar(hosts);
    }

    private static bool CtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private string AuthLabel(Host h) => h.Auth switch { AuthMethod.Password => "Password", AuthMethod.Key => "SSH Key", _ => "Agent" };
    private string AuthGlyph(Host h) => h.Auth switch { AuthMethod.Password => G.Lock, AuthMethod.Key => G.Key, _ => G.Person };

    private FrameworkElement Tile(Host h, bool grid, int index)
    {
        var selected = M.Selection.Contains(h.Id);
        var live = M.Sessions.Any(s => s.Host.Id == h.Id && s.State == TerminalSession.Status.Connected);
        double size = grid ? 46 : 34;

        var iconBox = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
            Background = selected ? Theme.BrandBrush : Theme.TintGradient(h.Color),
            BorderBrush = Theme.Brush("#FFFFFF", 0.25), BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = selected ? G.Check : G.Server, FontSize = size * 0.38, Foreground = Theme.Brush("#000000", 0.72) }
        };
        var icon = new Grid { Width = size, Height = size, Children = { iconBox } };
        if (live)
            icon.Children.Add(new Border
            {
                Width = size * 0.28, Height = size * 0.28, CornerRadius = new CornerRadius(size), Background = Theme.Brush(Theme.Green),
                BorderBrush = Theme.Bg, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0)
            });
        iconBox.Tapped += (_, e) => { M.ToggleSelect(h.Id); e.Handled = true; };

        var title = Ui.Text(h.Title(), grid ? 15 : 13, FontWeights.SemiBold);
        var addr = Ui.Text(h.DisplayAddress(M.HideAddresses), grid ? 12 : 11, fg: Theme.Text2, mono: true);
        var connect = Ui.Primary(grid ? "Connect" : "", G.Bolt, () => M.Connect([h.Id]));
        connect.Padding = new Thickness(10, 4, 10, 5);
        ToolTipService.SetToolTip(connect, "Connect");
        var edit = Ui.IconBtn(G.Edit, "Edit", () => M.Edit(h));

        var tags = Ui.Row(5);
        foreach (var t in h.Tags.Take(grid ? 3 : 4))
            tags.Children.Add(new Border
            {
                Background = Theme.Brush(h.Color, 0.13), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 3),
                Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.Medium, Foreground = Theme.Brush(h.Color) }
            });
        if (h.Tags.Length == 0 && grid) tags.Children.Add(Ui.Row(5, Ui.Icon(AuthGlyph(h), 10, Theme.Text3), Ui.Text(AuthLabel(h), 10, fg: Theme.Text3)));

        UIElement content;
        if (grid)
        {
            var actions = Ui.Row(2, edit, Ui.IconBtn(G.Copy, "Duplikat", () => Duplicate(h)));
            actions.Opacity = 0;
            var chip = StatusChip(h, live);
            var top = Ui.Columns("Auto,*,Auto", 8, icon, new Border(), new Grid { Children = { chip, actions } });
            var bottom = Ui.Columns("*,Auto", 8, tags, connect);
            content = Ui.Col(12, top, Ui.Col(3, title, addr), bottom);
            content = new Border { Padding = new Thickness(16), Child = content };
            var card = MakeCard(content, 16, selected, h);
            card.PointerEntered += (_, _) => { if (M.Selection.Count == 0) { actions.Opacity = 1; chip.Opacity = 0; } };
            card.PointerExited += (_, _) => { actions.Opacity = 0; chip.Opacity = 1; };
            Ui.StaggerIn(card, index);
            return card;
        }
        else
        {
            var authL = Ui.Row(5, Ui.Icon(AuthGlyph(h), 11, Theme.Text3), Ui.Text(AuthLabel(h), 11, fg: Theme.Text3));
            authL.Width = 90;
            var chip = StatusChip(h, live);
            var row = Ui.Columns("Auto,Auto,*,Auto,Auto,Auto", 14, icon, Ui.Col(1, title, addr).With(c => c.MinWidth = 180), tags, authL, chip, Ui.Row(4, edit, connect));
            var card = MakeCard(new Border { Padding = new Thickness(12, 9, 12, 9), Child = row }, 12, selected, h);
            Ui.StaggerIn(card, index);
            return card;
        }
    }

    private Border MakeCard(UIElement content, double radius, bool selected, Host h)
    {
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(radius), Background = Theme.Card,
            BorderBrush = selected ? Theme.AccentBrush : Theme.CardStroke, BorderThickness = new Thickness(selected ? 2 : 1)
        };
        card.PointerEntered += (_, _) => { card.Background = Theme.CardHover; if (!selected) card.BorderBrush = Theme.Brush(h.Color, 0.45); };
        card.PointerExited += (_, _) => { card.Background = Theme.Card; if (!selected) card.BorderBrush = Theme.CardStroke; };
        Ui.HoverLift(card);
        card.Tapped += (_, _) => { if (CtrlDown() || M.Selection.Count > 0) M.ToggleSelect(h.Id); };
        card.DoubleTapped += (_, _) => { if (M.Selection.Count == 0) M.Connect([h.Id]); };
        card.ContextFlyout = Menu(h);
        return card;
    }

    private UIElement StatusChip(Host h, bool live)
    {
        if (live)
        {
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Brush(Theme.Green) };
            Ui.Pulse(dot);
            return new Border
            {
                Background = Theme.Brush(Theme.Green, 0.12), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 8, 4), VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Row(5, dot, Ui.Text("Terhubung", 10, FontWeights.SemiBold, Theme.Brush(Theme.Green)))
            };
        }
        var last = M.Logs.FirstOrDefault(l => l.HostId == h.Id);
        if (last != null)
        {
            var b = new Border
            {
                Background = Theme.Ink(0.05), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 8, 4), VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Text(Ui.Relative(Ui.FromMs(last.StartedAt)), 10, FontWeights.Medium, Theme.Text3)
            };
            ToolTipService.SetToolTip(b, "Terakhir connect");
            return b;
        }
        return Ui.Text("Belum pernah", 10, FontWeights.Medium, Theme.Text3).With(t => t.VerticalAlignment = VerticalAlignment.Top);
    }

    private MenuFlyout Menu(Host h)
    {
        var f = new MenuFlyout();
        void Item(string t, string glyph, Action a)
        {
            var i = new MenuFlyoutItem { Text = t, Icon = new FontIcon { Glyph = glyph } };
            i.Click += (_, _) => a();
            f.Items.Add(i);
        }
        Item("Connect", G.Bolt, () => M.Connect([h.Id]));
        Item("Buka SFTP", G.Folder, () => _ = M.OpenSftp(h));
        Item("Edit", G.Edit, () => M.Edit(h));
        Item("Duplikat", G.Copy, () => Duplicate(h));
        f.Items.Add(new MenuFlyoutSeparator());
        Item("Salin alamat", G.Link, () => { Ui.CopyText(h.Address()); M.Flash("Alamat disalin"); });
        f.Items.Add(new MenuFlyoutSeparator());
        var del = new MenuFlyoutItem { Text = "Hapus", Icon = new FontIcon { Glyph = G.Delete }, Foreground = Theme.Brush("#E5484D") };
        del.Click += async (_, _) => { if (await Ui.Confirm($"Hapus host “{h.Title()}”?", "Host akan dihapus dari vault di semua perangkat yang tersinkron.")) M.Delete(h); };
        f.Items.Add(del);
        return f;
    }

    private void Duplicate(Host h)
    {
        var copy = h with { Id = "", Label = h.Title() + " (salinan)" };
        if (M.Save(copy) is { } saved) M.Flash($"“{saved.Title()}” dibuat");
    }

    private void RenderSelectionBar(Host[] visible)
    {
        if (M.Selection.Count == 0) { _selectionBar.Visibility = Visibility.Collapsed; return; }
        var count = new Border
        {
            Background = Theme.BrandBrush, CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 1, 8, 2),
            Child = Ui.Text(M.Selection.Count.ToString(), 12, FontWeights.Bold, Theme.Brush("#000000", 0.8))
        };
        var row = Ui.Row(10, count, Ui.Text("host dipilih", 13),
            Ui.Secondary("Pilih semua", null, () => M.SetSelection(visible.Select(h => h.Id))),
            Ui.Secondary("Batal", null, () => M.SetSelection([])),
            Ui.Primary("Connect semua", G.Bolt, () => M.Connect(M.Hosts.Where(h => M.Selection.Contains(h.Id)).Select(h => h.Id).ToList())));
        _selectionBar.Child = row;
        _selectionBar.Padding = new Thickness(14, 10, 10, 10);
        _selectionBar.CornerRadius = new CornerRadius(10);
        _selectionBar.Background = Theme.Dark ? Theme.Brush("#17191E", 0.98) : Theme.Brush("#FFFFFF", 0.98);
        _selectionBar.BorderBrush = Theme.Ink(0.1);
        _selectionBar.BorderThickness = new Thickness(1);
        _selectionBar.Shadow = new ThemeShadow();
        _selectionBar.Translation = new System.Numerics.Vector3(0, 0, 24);
        if (_selectionBar.Visibility == Visibility.Collapsed) { _selectionBar.Visibility = Visibility.Visible; Ui.StaggerIn(_selectionBar, 0); }
    }
}
