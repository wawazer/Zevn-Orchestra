using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Satu file / folder di panel lokal atau server.</summary>
internal sealed record FileItem(string Name, string Path, bool IsDir, ulong Size, DateTimeOffset? Modified, uint Permissions);

/// <summary>Satu transfer (upload / download).</summary>
internal sealed class Transfer
{
    public enum Kind { Upload, Download }
    public enum State { Running, Done, Failed, Cancelled }
    public Guid Id { get; } = Guid.NewGuid();
    public Kind Direction { get; init; }
    public string Name { get; init; } = "";
    public ulong Done { get; set; }
    public ulong Total { get; set; }
    public string CurrentFile { get; set; } = "";
    public State Status { get; set; } = State.Running;
    public string? Error { get; set; }
    public double Fraction => Total == 0 ? (Status == State.Done ? 1 : 0) : Math.Min(1, (double)Done / Total);
}

/// <summary>Jembatan progress dari core (thread Rust) ke UI, dengan throttle ±15 fps.</summary>
internal sealed class ProgressBridge(Transfer t, DispatcherQueue dq, Action changed) : TransferProgress
{
    private volatile bool _cancelled;
    private DateTime _last = DateTime.MinValue;
    private readonly object _lock = new();

    public void Cancel() => _cancelled = true;
    public bool IsCancelled() => _cancelled;

    public void OnProgress(ulong done, ulong total, string currentFile)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _last).TotalMilliseconds < 66 && done != total) return;
            _last = now;
        }
        dq.TryEnqueue(() => { t.Done = done; t.Total = total; t.CurrentFile = currentFile; changed(); });
    }
}

/// <summary>Model SFTP dua panel (lokal & server) — padanan SftpModel.swift.</summary>
internal sealed class SftpModel
{
    public enum Status { Idle, Connecting, Connected, Failed }

    private readonly AppModel _model;
    private SftpClient? _client;
    private readonly Dictionary<Guid, ProgressBridge> _bridges = [];
    private readonly DispatcherQueue _dq;

    public Host? Host { get; private set; }
    public Status State { get; private set; } = Status.Idle;
    public string? FailMessage { get; private set; }
    public string RemotePath { get; private set; } = "";
    public List<FileItem> RemoteItems { get; private set; } = [];
    public bool RemoteLoading { get; private set; }
    public string LocalPath { get; private set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public List<FileItem> LocalItems { get; private set; } = [];
    public bool ShowHidden { get; private set; }
    public List<Transfer> Transfers { get; } = [];
    public string? Error { get; private set; }

    public SftpModel(AppModel model)
    {
        _model = model;
        _dq = DispatcherQueue.GetForCurrentThread();
        ReloadLocal();
    }

    private void Raise() => _model.Raise("sftp");
    private void RaiseTransfers() => _model.Raise("transfers");

    public async Task Connect(Host host, SshKey? key)
    {
        await Disconnect();
        Host = host;
        State = Status.Connecting;
        Raise();
        var target = new SshTarget(host.Hostname, host.Port, host.Username,
            host.Auth == AuthMethod.Password ? host.Password : null,
            host.Auth == AuthMethod.Key ? key?.PrivateKey : null,
            host.Auth == AuthMethod.Key ? key?.Passphrase : null, true, host.Proxy);
        try
        {
            var c = await SftpClient.Connect(target);
            _client = c;
            var home = await c.Home();
            State = Status.Connected;
            Raise();
            await OpenRemote(home);
        }
        catch (Exception e)
        {
            State = Status.Failed;
            FailMessage = Errors.Text(e);
            Raise();
        }
    }

    public async Task Disconnect()
    {
        foreach (var b in _bridges.Values) b.Cancel();
        if (_client is { } c) { try { await c.Close(); } catch { } }
        _client = null;
        RemoteItems = [];
        RemotePath = "";
        State = Status.Idle;
        Host = null;
        Raise();
    }

    // MARK: Server

    public async Task OpenRemote(string path)
    {
        if (_client == null) return;
        RemoteLoading = true; Raise();
        try
        {
            var entries = await _client.List(path);
            RemotePath = path;
            RemoteItems = entries.Where(e => ShowHidden || !e.Name.StartsWith('.'))
                .Select(e => new FileItem(e.Name, e.Path, e.IsDir, e.Size, e.Modified > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Modified) : null, e.Permissions))
                .OrderBy(e => !e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) { FlashError(e); }
        RemoteLoading = false;
        Raise();
    }

    public Task RefreshRemote() => OpenRemote(RemotePath);

    public Task RemoteUp()
    {
        if (RemotePath == "/" || RemotePath == "") return Task.CompletedTask;
        var i = RemotePath.TrimEnd('/').LastIndexOf('/');
        return OpenRemote(i <= 0 ? "/" : RemotePath[..i]);
    }

    public async Task RemoteHome()
    {
        if (_client == null) return;
        try { await OpenRemote(await _client.Home()); } catch { }
    }

    public async Task MkdirRemote(string name)
    {
        if (_client == null || name.Length == 0) return;
        try { await _client.Mkdir(Join(RemotePath, name)); await RefreshRemote(); } catch (Exception e) { FlashError(e); }
    }

    public async Task RenameRemote(FileItem item, string name)
    {
        if (_client == null || name.Length == 0 || name == item.Name) return;
        try { await _client.Rename(item.Path, Join(RemotePath, name)); await RefreshRemote(); } catch (Exception e) { FlashError(e); }
    }

    public async Task ChmodRemote(FileItem item, uint mode)
    {
        if (_client == null) return;
        try { await _client.Chmod(item.Path, mode); await RefreshRemote(); } catch (Exception e) { FlashError(e); }
    }

    public async Task MoveRemote(IEnumerable<string> paths, string dir)
    {
        if (_client == null) return;
        foreach (var p in paths)
        {
            try { await _client.Rename(p, Join(dir, p.TrimEnd('/').Split('/').Last())); } catch (Exception e) { FlashError(e); }
        }
        await RefreshRemote();
    }

    public async Task DeleteRemote(IEnumerable<FileItem> items)
    {
        if (_client == null) return;
        foreach (var i in items)
        {
            try { await _client.Remove(i.Path); } catch (Exception e) { FlashError(e); }
        }
        await RefreshRemote();
    }

    // MARK: Lokal

    public void ReloadLocal()
    {
        var items = new List<FileItem>();
        try
        {
            var dir = new DirectoryInfo(LocalPath);
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                var hidden = e.Attributes.HasFlag(FileAttributes.Hidden) || e.Attributes.HasFlag(FileAttributes.System) || e.Name.StartsWith('.');
                if (hidden && !ShowHidden) continue;
                var isDir = e is DirectoryInfo;
                items.Add(new FileItem(e.Name, e.FullName, isDir, isDir ? 0 : (ulong)((FileInfo)e).Length, e.LastWriteTime, 0));
            }
        }
        catch (Exception ex) { FlashError(ex); }
        LocalItems = items.OrderBy(e => !e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Raise();
    }

    public void OpenLocal(string path) { LocalPath = path; ReloadLocal(); }

    public void LocalUp()
    {
        var parent = Directory.GetParent(LocalPath);
        if (parent != null) OpenLocal(parent.FullName);
    }

    public void SetShowHidden(bool on)
    {
        ShowHidden = on;
        ReloadLocal();
        _ = RefreshRemote();
    }

    public void MkdirLocal(string name)
    {
        try { Directory.CreateDirectory(System.IO.Path.Combine(LocalPath, name)); } catch (Exception e) { FlashError(e); }
        ReloadLocal();
    }

    public void DeleteLocal(IEnumerable<FileItem> items)
    {
        foreach (var i in items)
        {
            try { if (i.IsDir) Directory.Delete(i.Path, true); else File.Delete(i.Path); } catch (Exception e) { FlashError(e); }
        }
        ReloadLocal();
    }

    public void RenameLocal(FileItem item, string name)
    {
        if (name.Length == 0 || name == item.Name) return;
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, name);
        try { if (item.IsDir) Directory.Move(item.Path, dest); else File.Move(item.Path, dest); } catch (Exception e) { FlashError(e); }
        ReloadLocal();
    }

    // MARK: Transfer

    public bool Connected => _client != null;

    public void Upload(IEnumerable<string> paths, string? into = null)
    {
        if (_client == null) return;
        var client = _client;
        var dir = into ?? RemotePath;
        foreach (var p in paths)
        {
            var t = new Transfer { Direction = Transfer.Kind.Upload, Name = System.IO.Path.GetFileName(p.TrimEnd('\\')) };
            Start(t, b => client.Upload(p, dir, b), async () => { if (RemotePath == dir) await RefreshRemote(); });
        }
    }

    public void Download(IEnumerable<FileItem> items, string? into = null)
    {
        if (_client == null) return;
        var client = _client;
        var dir = into ?? LocalPath;
        foreach (var i in items)
        {
            var t = new Transfer { Direction = Transfer.Kind.Download, Name = i.Name };
            Start(t, b => client.Download(i.Path, dir, b), () => { if (LocalPath == dir) ReloadLocal(); return Task.CompletedTask; });
        }
    }

    public void Cancel(Transfer t) { if (_bridges.TryGetValue(t.Id, out var b)) b.Cancel(); }

    public void ClearFinished()
    {
        Transfers.RemoveAll(t => t.Status != Transfer.State.Running);
        RaiseTransfers();
    }

    private void Start(Transfer t, Func<ProgressBridge, Task> work, Func<Task> onDone)
    {
        var bridge = new ProgressBridge(t, _dq, RaiseTransfers);
        _bridges[t.Id] = bridge;
        Transfers.Insert(0, t);
        RaiseTransfers();
        _ = Run();
        async Task Run()
        {
            try
            {
                await work(bridge);
                t.Status = Transfer.State.Done;
                if (t.Total > 0) t.Done = t.Total;
            }
            catch (CoreException.Cancelled) { t.Status = Transfer.State.Cancelled; }
            catch (Exception e) { t.Status = Transfer.State.Failed; t.Error = Errors.Text(e); }
            _bridges.Remove(t.Id);
            RaiseTransfers();
            await onDone();
        }
    }

    private void FlashError(Exception e)
    {
        var msg = Errors.Text(e);
        Error = msg;
        Raise();
        _ = Task.Delay(4000).ContinueWith(_ => _dq.TryEnqueue(() => { if (Error == msg) { Error = null; Raise(); } }));
    }

    private static string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string PermissionString(uint mode, bool isDir)
    {
        var chars = new (uint bit, char c)[] { (0x100, 'r'), (0x80, 'w'), (0x40, 'x'), (0x20, 'r'), (0x10, 'w'), (0x8, 'x'), (0x4, 'r'), (0x2, 'w'), (0x1, 'x') };
        return (isDir ? "d" : "-") + new string(chars.Select(x => (mode & x.bit) != 0 ? x.c : '-').ToArray());
    }
}
