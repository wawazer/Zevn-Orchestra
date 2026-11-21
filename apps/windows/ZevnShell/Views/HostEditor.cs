using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Panel kanan untuk membuat / mengedit host (Address → General → Warna → SSH → Lanjutan → Tema).</summary>
internal sealed class HostEditor : Grid
{
    private readonly AppModel M = App.Model;
    private readonly Host _original;
    private Host _h;
    private string _tags;
    private bool _showAdvanced;
    private readonly Border _sshCard = new();
    private readonly Border _colorCard = new();
    private readonly Border _themeCard = new();
    private readonly Border _advancedCard = new();
    private readonly List<Button> _saveButtons = [];

    private bool IsNew => string.IsNullOrEmpty(_original.Id);

    public HostEditor(Host original)
    {
        _original = original;
        _h = original;
        _tags = string.Join(", ", original.Tags);
        _showAdvanced = original.AgentForwarding || original.JumpHostId != null || original.Proxy != null || !string.IsNullOrEmpty(original.StartupSnippet) || original.Env.Length > 0;

        var header = Ui.Columns("*,Auto", 8,
            Ui.Col(2, Ui.Text(IsNew ? "Host Baru" : "Edit Host", 17, FontWeights.Bold),
                Ui.Secondary(M.AccountEmail == null ? "Vault pribadi" : $"Vault · {M.AccountEmail}", 11.5)),
            Ui.IconBtn(G.Close, "Tutup (Esc)", () => M.Edit(null)));
        header.Padding = new Thickness(16, 12, 12, 12);

        var body = Ui.Col(14, AddressCard(), GeneralCard(), _colorCard, _sshCard, _advancedCard, _themeCard);
        if (!IsNew)
        {
            var del = Ui.Secondary("Hapus host", G.Delete, async () =>
            {
                if (await Ui.Confirm($"Hapus host “{_original.Title()}”?", "Host akan dihapus dari vault di semua perangkat yang tersinkron."))
                { M.Delete(_original); M.Edit(null); }
            }, Theme.Brush("#E5484D"));
            del.HorizontalAlignment = HorizontalAlignment.Stretch;
            body.Children.Add(del);
        }
        body.Padding = new Thickness(16);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
#if DEBUG
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_EDIT_SCROLL") == "1")
            scroll.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => scroll.ChangeView(null, 5000, null, true));
#endif

        var save = Ui.Secondary("Simpan", null, () => Save(false));
        var connect = Ui.Primary("Connect", G.Bolt, () => Save(true));
        ToolTipService.SetToolTip(connect, "Simpan lalu connect (Ctrl+Enter)");
        _saveButtons.AddRange([save, connect]);
        var footer = Ui.Row(8, save, connect);
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        var footerBox = new Border { Child = footer, Padding = new Thickness(14), BorderBrush = Theme.Ink(0.07), BorderThickness = new Thickness(0, 1, 0, 0) };
        var headerBox = new Border { Child = header, BorderBrush = Theme.Ink(0.07), BorderThickness = new Thickness(0, 0, 0, 1) };

        var root = Ui.Rows("Auto,*,Auto", 0, headerBox, scroll, footerBox);
        Children.Add(root);

        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { M.Edit(null); e.Handled = true; };
        var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        enter.Invoked += (_, e) => { Save(true); e.Handled = true; };
        var ctrlS = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        ctrlS.Invoked += (_, e) => { Save(false); e.Handled = true; };
        KeyboardAccelerators.Add(esc); KeyboardAccelerators.Add(enter); KeyboardAccelerators.Add(ctrlS);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        RenderColor(); RenderSsh(); RenderAdvanced(); RenderTheme();
        UpdateCanSave();
    }

    private void UpdateCanSave()
    {
        var ok = !string.IsNullOrWhiteSpace(_h.Hostname) && (_h.Proxy is not { } p || !string.IsNullOrWhiteSpace(p.Host));
        foreach (var b in _saveButtons) b.IsEnabled = ok;
    }

    private void Save(bool connect)
    {
        if (string.IsNullOrWhiteSpace(_h.Hostname) || (_h.Proxy is { } px && string.IsNullOrWhiteSpace(px.Host))) return;
        var h = _h with
        {
            Hostname = _h.Hostname.Trim(),
            Tags = _tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray(),
            Group = string.IsNullOrWhiteSpace(_h.Group) ? null : _h.Group!.Trim(),
            Env = _h.Env.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToArray(),
        };
        if (M.Save(h) is not { } saved) return;
        M.Edit(null);
        M.Flash(IsNew ? $"Host “{saved.Title()}” ditambahkan" : "Perubahan disimpan");
        if (connect) M.Connect([saved.Id]);
    }

    // MARK: Kartu

    private static Border EditorCard(string? title, params UIElement[] content)
    {
        var col = Ui.Col(10);
        if (title != null) col.Children.Add(Ui.Text(title, 14, FontWeights.SemiBold));
        foreach (var c in content) col.Children.Add(c);
        return Ui.Card(col, 14, 12);
    }

    private static Grid FieldRow(string glyph, UIElement field) =>
        Ui.Columns("Auto,*", 10, Ui.Icon(glyph, 13, Theme.Text3).With(i => i.Width = 18), field);

    private UIElement AddressCard()
    {
        var icon = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = Theme.TintGradient(_h.Color),
            Child = new FontIcon { Glyph = G.Server, FontSize = 17, Foreground = Theme.Brush("#000000", 0.7) }
        };
        var host = Ui.Field("IP atau hostname", _h.Hostname, mono: true);
        host.TextChanged += (_, _) => { _h = _h with { Hostname = host.Text }; UpdateCanSave(); };
        if (IsNew) host.Loaded += (_, _) => host.Focus(FocusState.Programmatic);
        var card = EditorCard("Address", Ui.Columns("Auto,*", 10, icon, host));
        _colorChanged += () => icon.Background = Theme.TintGradient(_h.Color);
        return card;
    }

    private Action? _colorChanged;

    private UIElement GeneralCard()
    {
        var label = Ui.Field("Label", _h.Label);
        label.TextChanged += (_, _) => _h = _h with { Label = label.Text };
        var groups = M.Hosts.Select(x => x.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().Cast<string>().ToList();
        var group = new AutoSuggestBox { PlaceholderText = "Grup", Text = _h.Group ?? "" };
        group.TextChanged += (s, a) =>
        {
            _h = _h with { Group = group.Text };
            if (a.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                group.ItemsSource = groups.Where(g => g.Contains(group.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        group.GotFocus += (_, _) => { if (group.Text.Length == 0) { group.ItemsSource = groups; group.IsSuggestionListOpen = groups.Count > 0; } };
        group.SuggestionChosen += (_, a) => { group.Text = a.SelectedItem as string ?? ""; _h = _h with { Group = group.Text }; };
        var tags = Ui.Field("Tag (pisahkan dengan koma)", _tags);
        tags.TextChanged += (_, _) => _tags = tags.Text;
        return EditorCard("General", FieldRow(G.Text, label), FieldRow(G.Folder, group), FieldRow(G.Tag, tags));
    }

    private void RenderColor()
    {
        var preview = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Background = Theme.Card, BorderBrush = Theme.Brush(_h.Color, 0.5), BorderThickness = new Thickness(1),
            Child = Ui.Columns("Auto,*", 10,
                new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Theme.TintGradient(_h.Color), Child = new FontIcon { Glyph = G.Server, FontSize = 13, Foreground = Theme.Brush("#000000", 0.7) } },
                Ui.Col(1, Ui.Text(string.IsNullOrEmpty(_h.Label) ? (string.IsNullOrEmpty(_h.Hostname) ? "Host baru" : _h.Hostname) : _h.Label, 13, FontWeights.SemiBold),
                    Ui.Text($"{_h.Username}@••••••••", 11, fg: Theme.Text2, mono: true)))
        };
        var swatches = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 34, ItemHeight = 34 };
        foreach (var c in Theme.CardPalette)
        {
            var active = string.Equals(_h.Color, c, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Theme.TintGradient(c),
                BorderBrush = Theme.Brush("#FFFFFF", active ? 0.9 : 0), BorderThickness = new Thickness(2),
                Child = active ? new FontIcon { Glyph = G.Check, FontSize = 10, Foreground = Theme.Brush("#000000", 0.7) } : null
            };
            var col = c;
            var b = Ui.Plain(dot, () => SetColor(col), new Thickness(2));
            ToolTipService.SetToolTip(b, c);
            swatches.Children.Add(b);
        }
        var picker = new ColorPicker { Color = Theme.Hex(_h.Color), IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring };
        var pickBtn = Ui.Secondary("Warna lain", null, () => { });
        var fly = new Flyout { Content = picker };
        fly.Closed += (_, _) => SetColor(Theme.ToHex(picker.Color));
        pickBtn.Flyout = fly;
        var custom = Ui.Columns("*,Auto", 8, pickBtn.With(b => b.HorizontalAlignment = HorizontalAlignment.Left), Ui.Text(_h.Color.ToUpperInvariant(), 11, fg: Theme.Text2, mono: true));
        _colorCard.Child = EditorCard("Warna kartu", preview, swatches, custom);
    }

    private void SetColor(string c)
    {
        _h = _h with { Color = c };
        RenderColor();
        _colorChanged?.Invoke();
    }

    private void RenderSsh()
    {
        var port = Ui.Number(_h.Port, 1, 65535, 90);
        port.ValueChanged += (_, _) => { if (!double.IsNaN(port.Value)) _h = _h with { Port = (ushort)port.Value }; };
        var portRow = Ui.Columns("*,Auto", 8, Ui.Text("SSH di port", 14, FontWeights.SemiBold), port);

        var user = Ui.Field("Username", _h.Username);
        user.TextChanged += (_, _) => _h = _h with { Username = user.Text };
        var auth = Ui.Segmented(["Password", "SSH Key", "Agent"], (int)_h.Auth, i => { _h = _h with { Auth = (AuthMethod)i }; RenderSsh(); });

        UIElement detail;
        switch (_h.Auth)
        {
            case AuthMethod.Password:
                var pw = new PasswordBox { PlaceholderText = "Password", Password = _h.Password ?? "", PasswordRevealMode = PasswordRevealMode.Peek };
                pw.PasswordChanged += (_, _) => _h = _h with { Password = pw.Password.Length == 0 ? null : pw.Password };
                detail = Ui.Col(6, FieldRow(G.Lock, pw), Ui.Secondary("Kosongkan jika ingin mengetik password saat connect.", 11.5, true));
                break;
            case AuthMethod.Key:
                detail = KeyPicker();
                break;
            default:
                detail = Ui.Row(8, Ui.Icon(G.Person, 13, Theme.Text2),
                    Ui.Secondary("Memakai SSH agent Windows (OpenSSH Authentication Agent / Pageant).", 12, true).With(t => t.MaxWidth = 300));
                break;
        }
        _sshCard.Child = Ui.Col(14, EditorCard(null, portRow), EditorCard("Credentials", FieldRow(G.Person, user), auth, detail));
    }

    private UIElement KeyPicker()
    {
        var selected = M.KeyById(_h.KeyId);
        var content = Ui.Columns("Auto,*,Auto", 10, Ui.Icon(G.Key, 14, Theme.AccentBrush),
            Ui.Col(1, Ui.Text(selected?.Label ?? "Pilih SSH key", 13, FontWeights.Medium),
                selected == null ? null : Ui.Text(selected.Fingerprint, 10, fg: Theme.Text2, mono: true)),
            Ui.Icon(G.ChevronDown, 10, Theme.Text2));
        var b = new Button
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(10), Background = Theme.Field, BorderBrush = Theme.Ink(0.1)
        };
        var f = new MenuFlyout();
        foreach (var k in M.Keys)
        {
            var item = new ToggleMenuFlyoutItem { Text = $"{k.Label} · {k.KeyType}", IsChecked = _h.KeyId == k.Id };
            item.Click += (_, _) => { _h = _h with { KeyId = k.Id }; RenderSsh(); };
            f.Items.Add(item);
        }
        if (M.Keys.Length > 0) f.Items.Add(new MenuFlyoutSeparator());
        var gen = new MenuFlyoutItem { Text = "Generate key Ed25519 baru", Icon = new FontIcon { Glyph = G.Sparkle } };
        gen.Click += (_, _) =>
        {
            try
            {
                var k = M.GenerateKey(string.IsNullOrEmpty(_h.Title()) ? "host-key" : _h.Title());
                _h = _h with { KeyId = k.Id };
                Ui.CopyText(k.PublicKey);
                M.Flash("Key dibuat · public key disalin ke clipboard");
                RenderSsh();
            }
            catch (Exception e) { M.Flash(Errors.Text(e)); }
        };
        var open = new MenuFlyoutItem { Text = "Buka Keychain…" };
        open.Click += (_, _) => { M.Edit(null); M.Navigate(Section.Keychain); };
        f.Items.Add(gen); f.Items.Add(open);
        b.Flyout = f;
        return b;
    }

    private void RenderAdvanced()
    {
        var chevron = Ui.Icon(_showAdvanced ? G.ChevronDown : G.ChevronRight, 11, Theme.Text2);
        var head = Ui.Plain(Ui.Columns("*,Auto", 8, Ui.Text("Lanjutan", 14, FontWeights.SemiBold), chevron), () => { _showAdvanced = !_showAdvanced; RenderAdvanced(); }, new Thickness(0));
        if (!_showAdvanced) { _advancedCard.Child = EditorCard(null, head); return; }

        var fwd = new ToggleSwitch { IsOn = _h.AgentForwarding, OnContent = "", OffContent = "", MinWidth = 0 };
        fwd.Toggled += (_, _) => _h = _h with { AgentForwarding = fwd.IsOn };
        var fwdRow = Ui.Columns("Auto,*,Auto", 10, Ui.Icon(G.Forward, 13, Theme.Text3), Ui.Text("Agent Forwarding", 13), fwd);

        var startup = Ui.Field("Startup snippet (dijalankan setelah login)", _h.StartupSnippet ?? "", mono: true);
        startup.TextChanged += (_, _) => _h = _h with { StartupSnippet = startup.Text.Length == 0 ? null : startup.Text };

        var jump = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Tanpa jump host" };
        jump.Items.Add(new ComboBoxItem { Content = "Tanpa jump host", Tag = "" });
        foreach (var x in M.Hosts.Where(x => x.Id != _h.Id)) jump.Items.Add(new ComboBoxItem { Content = x.Title(), Tag = x.Id });
        jump.SelectedItem = jump.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (_h.JumpHostId ?? "")) ?? jump.Items[0];
        jump.SelectionChanged += (_, _) => { var id = (jump.SelectedItem as ComboBoxItem)?.Tag as string; _h = _h with { JumpHostId = string.IsNullOrEmpty(id) ? null : id }; };

        var charset = new ComboBox { Items = { "UTF-8", "ASCII" }, SelectedItem = _h.Charset == "ASCII" ? "ASCII" : "UTF-8", HorizontalAlignment = HorizontalAlignment.Stretch };
        charset.SelectionChanged += (_, _) => _h = _h with { Charset = charset.SelectedItem as string ?? "UTF-8" };

        var pickers = Ui.Columns("*,110", 10,
            Ui.Col(4, Ui.Secondary("Jump host (Host Chaining)", 11.5), jump),
            Ui.Col(4, Ui.Secondary("Charset", 11.5), charset));

        var env = Ui.Col(6);
        env.Children.Add(ProxyEditor());
        for (int i = 0; i < _h.Env.Length; i++)
        {
            var idx = i;
            var name = Ui.Field("NAMA", _h.Env[i].Name, mono: true);
            var value = Ui.Field("nilai", _h.Env[i].Value, mono: true);
            name.TextChanged += (_, _) => { var e = _h.Env.ToArray(); e[idx] = e[idx] with { Name = name.Text }; _h = _h with { Env = e }; };
            value.TextChanged += (_, _) => { var e = _h.Env.ToArray(); e[idx] = e[idx] with { Value = value.Text }; _h = _h with { Env = e }; };
            var rm = Ui.IconBtn(G.Close, "Hapus", () => { _h = _h with { Env = _h.Env.Where((_, j) => j != idx).ToArray() }; RenderAdvanced(); }, size: 10);
            env.Children.Add(Ui.Columns("*,*,Auto", 6, name, value, rm));
        }
        var addEnv = Ui.Plain(Ui.Row(6, Ui.Icon(G.Add, 11, Theme.AccentBrush), Ui.Text("Environment variable", 12.5, fg: Theme.AccentBrush)),
            () => { _h = _h with { Env = [.. _h.Env, new EnvVar("", "")] }; RenderAdvanced(); });
        addEnv.HorizontalAlignment = HorizontalAlignment.Left;

        _advancedCard.Child = EditorCard(null, head, fwdRow, FieldRow(G.Terminal, startup), pickers, env, addEnv);
    }

    /// <summary>Proxy SOCKS5 / HTTP untuk mencapai server (mis. jaringan kantor).</summary>
    private FrameworkElement ProxyEditor()
    {
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, tag) in new[] { ("Tanpa proxy", ""), ("SOCKS5", "socks5"), ("HTTP", "http") })
            kind.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        var cur = _h.Proxy is null ? "" : _h.Proxy.Kind == ProxyKind.Http ? "http" : "socks5";
        kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == cur);
        kind.SelectionChanged += (_, _) =>
        {
            var tag = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (tag == "") _h = _h with { Proxy = null };
            else
            {
                var k = tag == "http" ? ProxyKind.Http : ProxyKind.Socks5;
                ushort port = k == ProxyKind.Http ? (ushort)8080 : (ushort)1080;
                _h = _h with { Proxy = _h.Proxy is { } p ? p with { Kind = k, Port = p.Port is 1080 or 8080 ? port : p.Port } : new HostProxy(k, "", port, null, null) };
            }
            UpdateCanSave();
            RenderAdvanced();
        };
        var col = Ui.Col(6, Ui.Col(4, Ui.Secondary("Proxy", 11.5), kind));
        if (_h.Proxy is not { } px) return col;

        var host = Ui.Field("Alamat proxy", px.Host, mono: true);
        host.TextChanged += (_, _) => { if (_h.Proxy is { } p) _h = _h with { Proxy = p with { Host = host.Text.Trim() } }; UpdateCanSave(); };
        var port = Ui.Number(px.Port, 1, 65535, 96);
        port.ValueChanged += (_, _) => { if (_h.Proxy is { } p && !double.IsNaN(port.Value)) _h = _h with { Proxy = p with { Port = (ushort)port.Value } }; };
        var user = Ui.Field("Username (opsional)", px.Username ?? "");
        user.TextChanged += (_, _) => { if (_h.Proxy is { } p) _h = _h with { Proxy = p with { Username = user.Text.Length == 0 ? null : user.Text } }; };
        var pw = new PasswordBox { PlaceholderText = "Password", Password = px.Password ?? "", PasswordRevealMode = PasswordRevealMode.Peek };
        pw.PasswordChanged += (_, _) => { if (_h.Proxy is { } p) _h = _h with { Proxy = p with { Password = pw.Password.Length == 0 ? null : pw.Password } }; };
        var note = _h.JumpHostId != null
            ? "Proxy tidak dipakai di terminal selama jump host aktif."
            : "Dipakai untuk terminal, SFTP, port forwarding, dan tunnel database host ini.";
        col.Children.Add(Ui.Columns("*,Auto", 6, host, port));
        col.Children.Add(Ui.Columns("*,*", 6, user, pw));
        col.Children.Add(Ui.Secondary(note, 11.5, true));
        return col;
    }

    private void RenderTheme()
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 108, ItemHeight = 92 };
        grid.Children.Add(ThemeOption(null, "Otomatis", TerminalThemes.Get(null)));
        foreach (var t in TerminalThemes.All) grid.Children.Add(ThemeOption(t.Id, t.Name, t));
        _themeCard.Child = EditorCard("Tema terminal", grid);
    }

    private UIElement ThemeOption(string? id, string name, TerminalThemes.Def t)
    {
        var active = _h.TerminalTheme == id;
        var lines = Ui.Col(3,
            Ui.Row(4, Ui.Text("$", 10, fg: Theme.Brush(t.Accent), mono: true), Ui.Text(id == null ? "ikut app" : "ls -la", 10, fg: Theme.Brush(t.Fg), mono: true)),
            Ui.Row(4, new Border { Width = 18, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(t.Ansi[2]) },
                new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(t.Ansi[4]) }),
            Ui.Row(4, new Border { Width = 12, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(t.Ansi[1]) },
                new Border { Width = 20, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(t.Ansi[5]) }));
        var preview = new Border
        {
            Height = 56, Width = 98, CornerRadius = new CornerRadius(9), Background = Theme.Brush(t.Bg), Padding = new Thickness(8),
            BorderBrush = active ? Theme.AccentBrush : Theme.Ink(0.1), BorderThickness = new Thickness(active ? 2 : 1), Child = lines
        };
        var label = Ui.Text(name, 11, fg: active ? Theme.Text : Theme.Text2);
        var b = Ui.Plain(Ui.Col(6, preview, label), () => { _h = _h with { TerminalTheme = id }; RenderTheme(); }, new Thickness(0));
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.VerticalAlignment = VerticalAlignment.Top;
        return b;
    }
}
