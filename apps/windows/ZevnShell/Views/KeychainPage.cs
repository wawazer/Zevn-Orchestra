using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>SSH key tersimpan terenkripsi di vault: generate Ed25519, import, ganti nama, salin public key.</summary>
internal sealed class KeychainPage : PageBase
{
    private readonly StackPanel _list = Ui.Col(10);

    public KeychainPage() : base("data")
    {
        var header = Ui.Header("Keychain", "SSH key tersimpan terenkripsi di vault. Pakai di host lewat Credentials → SSH Key.",
            Ui.Secondary("Import", G.Download, () => _ = ImportDialog()),
            Ui.Primary("Generate Key", G.Sparkle, () => _ = GenerateDialog()));
        Children.Add(Ui.Scroll(Ui.Col(22, header, _list)));
        Render();
    }

    protected override void Render()
    {
        _list.Children.Clear();
        if (M.Keys.Length == 0)
        {
            _list.Children.Add(Ui.EmptyState(G.Key, "Belum ada SSH key", "Generate key Ed25519 baru atau import dari %USERPROFILE%\\.ssh.",
                Ui.Primary("Generate Key", G.Sparkle, () => _ = GenerateDialog())));
            return;
        }
        for (int i = 0; i < M.Keys.Length; i++)
        {
            var row = KeyRow(M.Keys[i]);
            _list.Children.Add(row);
            Ui.StaggerIn(row, i);
        }
    }

    private FrameworkElement KeyRow(SshKey k)
    {
        var icon = Ui.TintIcon(G.Key, "#F59E0B", 40, 16, 11);
        var used = M.Hosts.Count(h => h.KeyId == k.Id);
        var title = Ui.Text(k.Label, 14, FontWeights.SemiBold);
        var titleBox = new Grid { Children = { title } };
        title.DoubleTapped += (_, _) => StartRename(titleBox, k);
        ToolTipService.SetToolTip(title, "Klik dua kali untuk ganti nama");
        var info = Ui.Col(3,
            Ui.Row(8, titleBox, Ui.Badge(k.KeyType.Replace("ssh-", "").ToUpperInvariant(), Theme.Accent2), used > 0 ? Ui.Pill($"{used} host") : null),
            Ui.Text(k.Fingerprint, 11, fg: Theme.Text2, mono: true).With(t => t.IsTextSelectionEnabled = true));
        var copy = Ui.Secondary("Salin public key", G.Copy, () => { Ui.CopyText(k.PublicKey); M.Flash("Public key disalin"); });
        var rename = Ui.IconBtn(G.Rename, "Ganti nama", () => StartRename(titleBox, k));
        var del = Ui.IconBtn(G.Delete, "Hapus key", async () =>
        {
            var msg = used > 0 ? $"Key ini dipakai {used} host. Host tersebut tidak bisa login dengan key sampai kamu memilih key lain." : "Key akan dihapus dari vault di semua perangkat.";
            if (await Ui.Confirm($"Hapus “{k.Label}”?", msg)) M.DeleteKey(k);
        }, Theme.Brush("#E5484D"));
        var row = Ui.Columns("Auto,*,Auto", 14, icon, info, Ui.Row(4, copy, rename, del));
        var card = Ui.HoverCard(row, 14);
        card.ContextFlyout = new MenuFlyout
        {
            Items =
            {
                new MenuFlyoutItem { Text = "Salin public key" }.With(i => i.Click += (_, _) => { Ui.CopyText(k.PublicKey); M.Flash("Public key disalin"); }),
                new MenuFlyoutItem { Text = "Salin fingerprint" }.With(i => i.Click += (_, _) => Ui.CopyText(k.Fingerprint)),
            }
        };
        return card;
    }

    private void StartRename(Grid box, SshKey k)
    {
        var tb = new TextBox { Text = k.Label, MinWidth = 200 };
        void Commit()
        {
            var v = tb.Text.Trim();
            if (v.Length > 0 && v != k.Label) M.RenameKey(k, v); else Render();
        }
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Commit();
            else if (e.Key == Windows.System.VirtualKey.Escape) Render();
        };
        tb.LostFocus += (_, _) => Commit();
        box.Children.Clear();
        box.Children.Add(tb);
        tb.Loaded += (_, _) => { tb.Focus(FocusState.Programmatic); tb.SelectAll(); };
    }

    private async Task GenerateDialog()
    {
        var label = Ui.Field("Label (mis. Laptop kantor)", Environment.MachineName);
        var content = Ui.Col(10, Ui.Secondary("Membuat pasangan key Ed25519 baru — cepat, aman, dan didukung semua server modern.", 13, true), label);
        var d = Ui.Dialog("Generate SSH key", content, "Generate");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        SshKey k;
        try { k = M.GenerateKey(label.Text.Trim().Length == 0 ? "key" : label.Text.Trim()); }
        catch (Exception e) { M.Flash(Errors.Text(e)); return; }
        var pub = new TextBox { Text = k.PublicKey, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = Theme.MonoFont, FontSize = 11.5 };
        var done = Ui.Dialog("Key berhasil dibuat", Ui.Col(10,
            Ui.Secondary("Tambahkan public key ini ke ~/.ssh/authorized_keys di server.", 13, true), pub), "Salin & tutup", "Tutup");
        if (await done.ShowAsync() == ContentDialogResult.Primary) { Ui.CopyText(k.PublicKey); M.Flash("Public key disalin"); }
    }

    private async Task ImportDialog()
    {
        var label = Ui.Field("Label");
        var key = new TextBox { PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Theme.MonoFont, FontSize = 11, Height = 150 };
        var pass = new PasswordBox { PlaceholderText = "Passphrase (jika ada)" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var pick = Ui.Secondary("Pilih file…", G.Folder, async () =>
        {
            var p = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(p, App.Window!.Hwnd);
            p.FileTypeFilter.Add("*");
            var f = await p.PickSingleFileAsync();
            if (f == null) return;
            try
            {
                key.Text = await File.ReadAllTextAsync(f.Path);
                if (label.Text.Length == 0) label.Text = Path.GetFileNameWithoutExtension(f.Name);
            }
            catch (Exception e) { error.Message = e.Message; error.IsOpen = true; }
        });
        var content = Ui.Col(10, Ui.Secondary("Pilih file private key (mis. %USERPROFILE%\\.ssh\\id_ed25519) atau tempel isinya. Format OpenSSH.", 13, true),
            label, key, pass, pick, error);
        content.Width = 460;
        var d = Ui.Dialog("Import SSH key", content, "Import");
        d.PrimaryButtonClick += (s, a) =>
        {
            try
            {
                M.ImportKey(label.Text.Trim().Length == 0 ? "key" : label.Text.Trim(), key.Text, pass.Password.Length == 0 ? null : pass.Password);
                M.Flash("Key berhasil diimport");
            }
            catch (Exception e) { error.Message = Errors.Text(e); error.IsOpen = true; a.Cancel = true; }
        };
        await d.ShowAsync();
    }
}
