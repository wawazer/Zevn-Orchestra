using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>
/// Deploy satu klik untuk project aktif: unggah rilis, pindahkan `current`, restart,
/// health check, dan rollback ke rilis sebelumnya.
/// </summary>
internal sealed class DeployPage : PageBase
{
    private readonly DeployModel D = App.Model.Deploy;

    public DeployPage() : base("deploy", "data")
    {
        Render();
    }

    protected override void Render()
    {
        Children.Clear();
        if (M.ActiveProject is not { } project)
        {
            Children.Add(Ui.Scroll(Ui.Col(22,
                Ui.EmptyState(G.Upload, "Belum ada project aktif",
                    "Deploy berjalan per project. Aktifkan project dulu, lalu atur host tujuan dan folder rilisnya.",
                    Ui.Primary("Buka Projects", G.Apps, () => M.Navigate(Section.Projects))))));
            return;
        }

        var host = M.HostById(project.DeployHostId);
        var basePath = (project.DeployPath ?? "").Trim();
        var local = D.LocalDir(project);
        _ = D.EnsureGitAsync(project);
        var ready = basePath.Length > 0 && host != null;

        var refresh = Ui.Secondary("Muat rilis", G.Refresh, () =>
        {
            if (host != null && M.SshTarget(host.Id) is { } t) _ = D.RefreshAsync(project, t);
        });
        refresh.IsEnabled = ready && !D.Busy;

        var deploy = Ui.Primary("Deploy sekarang", G.Upload, async () =>
        {
            if (!await GitStatus.ConfirmDirtyAsync(D.Git)) return;
            if (host != null && M.SshTarget(host.Id) is { } t) _ = D.DeployAsync(project, t);
        });
        deploy.IsEnabled = ready && !D.Busy && !string.IsNullOrWhiteSpace(local);

        var header = Ui.Header("Deploy",
            $"Rilis ke releases/<timestamp> + symlink current — project {project.Name}.",
            refresh, deploy);

        var body = Ui.Col(18, header, TargetCard(project, host, basePath, local), StatusCard(), ReleasesCard(project, host));
        if (D.Log.Count > 0) body.Children.Add(LogCard());
        Children.Add(Ui.Scroll(body));
    }

    private UIElement TargetCard(Project project, Host? host, string basePath, string? local)
    {
        var choose = Ui.Secondary("Pilih folder…", G.Folder, async () => await PickFolder(project));
        choose.IsEnabled = !D.Busy;

        var localText = string.IsNullOrWhiteSpace(local)
            ? Ui.Text("Belum dipilih", 12, fg: Theme.Brush(Theme.Orange))
            : Ui.Text(local, 12, fg: Theme.Text2, mono: true);
        var localRow = Ui.Columns("*,Auto", 10, localText, choose);

        var commands = Ui.Col(3,
            CommandRow("Bangun", project.DeployBuild),
            CommandRow("Restart", project.DeployRestart),
            CommandRow("Health", project.DeployHealth));

        UIElement target = host == null
            ? Ui.Text("Host tujuan belum dipilih", 12, fg: Theme.Brush(Theme.Orange))
            : Ui.Row(8, Ui.Text(host.Title(), 12, FontWeights.SemiBold),
                Ui.Text(host.DisplayAddress(M.HideAddresses), 11.5, fg: Theme.Text2, mono: true));

        var edit = Ui.Secondary("Atur di Projects", G.Edit, () => M.Navigate(Section.Projects));
        edit.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = Ui.Col(10,
            SectionLabel("Server"), target,
            SectionLabel("Folder rilis"), basePath.Length > 0
                ? Ui.Text(basePath, 12, fg: Theme.Text2, mono: true)
                : Ui.Text("Belum diatur", 12, fg: Theme.Brush(Theme.Orange)),
            SectionLabel("Folder lokal"), localRow,
            SectionLabel("Git"), GitRow(project),
            Ui.Divider(),
            SectionLabel("Perintah"), commands,
            edit);
        return Card(grid);
    }

    /// <summary>Branch, commit terakhir, dan kondisi working tree folder lokal.</summary>
    private UIElement GitRow(Project project)
    {
        if (D.Git is not { } git)
            return Ui.Text(string.IsNullOrWhiteSpace(D.LocalDir(project))
                ? "Folder lokal belum dipilih"
                : "Membaca status Git…", 11.5, fg: Theme.Text3);
        if (git.Error is { } error)
            return Ui.Text(error, 11.5, fg: Theme.Text3);

        var dirty = git.Changed > 0;
        var line = Ui.Columns("*,Auto", 10,
            Ui.Col(2,
                Ui.Row(8,
                    Ui.Text(git.Head, 11.5, fg: Theme.Text2, mono: true),
                    Ui.Badge(dirty ? "KOTOR" : "BERSIH", dirty ? Theme.Orange : Theme.Green)),
                git.Subject is { Length: > 0 } subject
                    ? Ui.Text(subject, 11, fg: Theme.Text3)
                    : null),
            dirty ? Ui.Text($"{git.Changed} berkas", 11, fg: Theme.Brush(Theme.Orange)) : null);
        return line;
    }

    private static UIElement CommandRow(string label, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? Ui.Text("—", 11.5, fg: Theme.Text3)
            : Ui.Text(value.Trim(), 11.5, fg: Theme.Text2, mono: true);
        text.TextWrapping = TextWrapping.Wrap;
        return Ui.Columns("90,*", 8, Ui.Text(label, 11.5, fg: Theme.Text3), text);
    }

    private UIElement StatusCard()
    {
        UIElement content;
        if (D.State == DeployModel.Status.Failed)
        {
            content = Ui.Row(10, Ui.Icon(G.Warning, 14, Theme.Brush(Theme.Orange)),
                Ui.Text(D.Error ?? "Deploy gagal", 12.5, fg: Theme.Brush(Theme.Orange)).With(t => t.TextWrapping = TextWrapping.Wrap));
        }
        else if (D.Busy)
        {
            var cancel = Ui.Secondary("Batalkan", G.Close, () => D.Cancel());
            content = Ui.Columns("*,Auto", 10,
                Ui.Row(10, new ProgressRing { Width = 15, Height = 15, IsActive = true },
                    Ui.Col(1, Ui.Text(D.Step, 13, FontWeights.SemiBold),
                        Ui.Text(D.Detail, 11.5, fg: Theme.Text2, mono: true).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis))),
                cancel);
        }
        else
        {
            var current = D.Releases.FirstOrDefault(r => r.IsCurrent)?.Name;
            content = Ui.Row(10, Ui.Icon(G.Check, 13, Theme.Brush(Theme.Green)),
                Ui.Text(current == null
                        ? "Belum ada rilis aktif di server."
                        : $"Rilis aktif: {current}  ·  {D.Releases.Length} rilis tersimpan",
                    12.5, fg: Theme.Text2));
        }
        return Card(content, compact: true);
    }

    private UIElement ReleasesCard(Project project, Host? host)
    {
        var list = Ui.Col(6);
        if (D.Releases.Length == 0)
        {
            list.Children.Add(Ui.Secondary(D.State == DeployModel.Status.Ready
                ? "Belum ada rilis. Tekan “Deploy sekarang” untuk rilis pertama."
                : "Tekan “Muat rilis” untuk melihat rilis di server.", 12));
        }
        else
        {
            foreach (var r in D.Releases)
            {
                var when = r.CreatedAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(r.CreatedAt).ToLocalTime().ToString("dd MMM yyyy, HH:mm")
                    : "waktu tidak diketahui";
                var rollback = Ui.Secondary("Rollback", G.Back, async () =>
                {
                    if (await Ui.Confirm($"Aktifkan kembali rilis {r.Name}?",
                        "Symlink current dipindah ke rilis ini, lalu perintah restart & health dijalankan."))
                    {
                        if (host != null && M.SshTarget(host.Id) is { } t) _ = D.RollbackAsync(project, t, r.Name);
                    }
                });
                rollback.IsEnabled = !D.Busy && !r.IsCurrent;

                var row = Ui.Columns("Auto,*,Auto", 12,
                    Ui.TintIcon(r.IsCurrent ? G.Check : G.Upload, r.IsCurrent ? Theme.Accent : "#4A5568", 32, 13, 8),
                    Ui.Col(2, Ui.Row(8, Ui.Text(r.Name, 12.5, FontWeights.SemiBold),
                            r.IsCurrent ? Ui.Badge("AKTIF", Theme.Accent) : null),
                        Ui.Text(when, 11, fg: Theme.Text3)),
                    rollback);
                list.Children.Add(new Border
                {
                    Child = new Border { Padding = new Thickness(10, 8, 10, 8), Child = row },
                    Background = r.IsCurrent ? Theme.Brush(Theme.Accent, 0.05) : Theme.Ink(0.02),
                    BorderBrush = r.IsCurrent ? Theme.Brush(Theme.Accent, 0.3) : Theme.CardStroke,
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9)
                });
            }
        }
        return Card(Ui.Col(10, SectionLabel("Rilis di server"), list));
    }

    private UIElement LogCard()
    {
        var text = Ui.Text(string.Join(Environment.NewLine, D.Log), 11.5, fg: Theme.Text2, mono: true);
        text.IsTextSelectionEnabled = true;
        text.TextWrapping = TextWrapping.NoWrap;
        var scroll = new ScrollViewer
        {
            Content = text, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        return Card(Ui.Col(8, SectionLabel("Log deploy"), scroll));
    }

    private static UIElement SectionLabel(string text) => Ui.Caps(text);

    private static Border Card(UIElement content, bool compact = false) => new()
    {
        Child = new Border { Padding = new Thickness(compact ? 12 : 14), Child = content },
        Background = Theme.Card, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12)
    };

    private async Task PickFolder(Project project)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window!.Hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) D.SetLocalDir(project, folder.Path);
        }
        catch (Exception e) { M.Flash(Errors.Text(e)); }
    }
}
