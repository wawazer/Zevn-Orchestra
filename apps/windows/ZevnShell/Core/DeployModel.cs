using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Jembatan langkah deploy dari core (thread Rust) ke UI.</summary>
internal sealed class DeployBridge(Action<string, string> onStep) : DeployProgress
{
    private volatile bool _cancelled;
    public void Cancel() => _cancelled = true;
    public bool IsCancelled() => _cancelled;
    public void OnStep(string step, string detail) => onStep(step, detail);
}

/// <summary>
/// Deploy satu project: koneksi, daftar rilis, deploy, dan rollback.
/// Riwayat & status hanya di perangkat ini; definisi project tetap di vault.
/// </summary>
internal sealed class DeployModel
{
    public enum Status { Idle, Connecting, Ready, Busy, Failed }

    /// <summary>Nama yang tidak ikut diunggah — isi build/dependency lokal.</summary>
    public static readonly string[] DefaultExcludes =
        [".git", ".gitignore", ".gitattributes", "node_modules", ".venv", "venv", "__pycache__", ".idea", ".vscode", ".DS_Store"];

    private readonly AppModel _model;
    private readonly DispatcherQueue _dq;
    private Deployer? _deployer;
    private string? _connectionKey;
    private DeployBridge? _bridge;

    public Status State { get; private set; } = Status.Idle;
    public string? Error { get; private set; }
    public string Step { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public Release[] Releases { get; private set; } = [];
    public List<string> Log { get; } = [];

    public DeployModel(AppModel model)
    {
        _model = model;
        _dq = DispatcherQueue.GetForCurrentThread();
    }

    public bool Busy => State is Status.Connecting or Status.Busy;

    /// <summary>Folder lokal disimpan per perangkat (path berbeda antar PC).</summary>
    public static string LocalDirKey(string projectId) => "deploy.local." + projectId;
    public string? LocalDir(Project p) => Settings.Str(LocalDirKey(p.Id));

    public void SetLocalDir(Project p, string dir)
    {
        Settings.Set(LocalDirKey(p.Id), dir);
        Post(() => { });
    }

    public void Cancel() => _bridge?.Cancel();

    private void Post(Action apply)
    {
        _dq.TryEnqueue(() =>
        {
            apply();
            _model.Raise("deploy");
        });
    }

    /// <summary>Muat daftar rilis dari server (sekaligus menguji koneksi).</summary>
    public async Task RefreshAsync(Project p, SshTarget target, bool force = false)
    {
        if (Busy && !force) return;
        var basePath = (p.DeployPath ?? "").Trim();
        if (basePath.Length == 0) return;
        Post(() => { State = Status.Connecting; Error = null; Step = "Menghubungkan"; Detail = basePath; });
        try
        {
            var d = await ConnectAsync(p, target);
            var releases = await d.Releases(basePath);
            Post(() => { Releases = releases; State = Status.Ready; Step = ""; Detail = ""; });
        }
        catch (Exception e)
        {
            Post(() => { State = Status.Failed; Error = Errors.Text(e); Step = ""; Detail = ""; });
        }
    }

    public async Task DeployAsync(Project p, SshTarget target)
    {
        if (Busy) return;
        var basePath = (p.DeployPath ?? "").Trim();
        var local = LocalDir(p);
        if (basePath.Length == 0)
        {
            Post(() => { State = Status.Failed; Error = "Folder dasar server belum diisi — atur di Projects → Edit."; });
            return;
        }
        if (string.IsNullOrWhiteSpace(local) || !Directory.Exists(local))
        {
            Post(() => { State = Status.Failed; Error = "Folder lokal belum dipilih."; });
            return;
        }

        var bridge = new DeployBridge((step, detail) => Post(() => { Step = step; Detail = detail; }));
        _bridge = bridge;
        Post(() =>
        {
            State = Status.Busy; Error = null; Step = "Mulai"; Detail = basePath;
            Log.Clear();
        });
        try
        {
            var d = await ConnectAsync(p, target);
            var outcome = await d.Deploy(Config(p, basePath, local), bridge);
            Post(() =>
            {
                foreach (var line in outcome.Log) Log.Add(line);
                Step = "Selesai";
                Detail = $"Rilis {outcome.Release} aktif"
                    + (outcome.Previous is { } prev ? $" — sebelumnya {prev}" : "");
                State = Status.Ready;
            });
            await RefreshAsync(p, target, force: true);
        }
        catch (Exception e)
        {
            Post(() =>
            {
                Log.Add(Errors.Text(e));
                Error = Errors.Text(e);
                State = Status.Failed;
                Step = "";
                Detail = "";
            });
        }
        finally
        {
            _bridge = null;
        }
    }

    public async Task RollbackAsync(Project p, SshTarget target, string release)
    {
        if (Busy) return;
        var basePath = (p.DeployPath ?? "").Trim();
        if (basePath.Length == 0) return;

        var bridge = new DeployBridge((step, detail) => Post(() => { Step = step; Detail = detail; }));
        _bridge = bridge;
        Post(() => { State = Status.Busy; Error = null; Step = "Rollback"; Detail = release; Log.Clear(); });
        try
        {
            var d = await ConnectAsync(p, target);
            var outcome = await d.Rollback(basePath, release, Cmd(p.DeployRestart), Cmd(p.DeployHealth), bridge);
            Post(() =>
            {
                foreach (var line in outcome.Log) Log.Add(line);
                Step = "Selesai";
                Detail = $"Rilis {outcome.Release} aktif kembali";
                State = Status.Ready;
            });
            await RefreshAsync(p, target, force: true);
        }
        catch (Exception e)
        {
            Post(() =>
            {
                Log.Add(Errors.Text(e));
                Error = Errors.Text(e);
                State = Status.Failed;
                Step = "";
                Detail = "";
            });
        }
        finally
        {
            _bridge = null;
        }
    }

    public void Close()
    {
        var d = _deployer;
        _deployer = null;
        _connectionKey = null;
        if (d != null) _ = d.Close();
    }

    private async Task<Deployer> ConnectAsync(Project p, SshTarget target)
    {
        var key = $"{p.Id}|{target.Hostname}|{target.Port}|{target.Username}";
        if (_deployer != null && _connectionKey == key) return _deployer;
        Close();
        var d = await Deployer.Connect(target);
        _deployer = d;
        _connectionKey = key;
        return d;
    }

    private static DeployConfig Config(Project p, string basePath, string local) => new(
        basePath,
        local,
        DefaultExcludes,
        Cmd(p.DeployBuild),
        Cmd(p.DeployRestart),
        Cmd(p.DeployHealth),
        5);

    private static string? Cmd(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
