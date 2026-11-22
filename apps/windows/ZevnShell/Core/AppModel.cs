using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using uniffi.zevn_core;

namespace ZevnShell;

internal enum Phase { Welcome, CreatePassword, Restore, Locked, Main }

internal enum Section { Projects, Overview, Deploy, LiveLogs, Hosts, Keychain, Forwarding, Sftp, Database, Snippets, KnownHosts, Logs, About, Account }

internal enum SyncKind { Off, Idle, Syncing, Synced, Failed, NeedsLogin }

internal static class SectionInfo
{
    public static readonly Section[] Main = [Section.Projects, Section.Overview, Section.Deploy, Section.LiveLogs, Section.Hosts, Section.Keychain, Section.Forwarding, Section.Sftp, Section.Database, Section.Snippets, Section.KnownHosts, Section.Logs];
    public static readonly Section[] Community = [Section.About];

    public static string Title(this Section s) => s switch
    {
        Section.Projects => "Projects", Section.Overview => "Overview", Section.Deploy => "Deploy", Section.LiveLogs => "Live Logs", Section.Hosts => "Hosts",
        Section.Keychain => "Keychain", Section.Forwarding => "Port Forwarding", Section.Sftp => "SFTP",
        Section.Database => "Database", Section.Snippets => "Snippets", Section.KnownHosts => "Known Hosts", Section.Logs => "Logs",
        Section.About => "Tentang", _ => "Akun",
    };

    public static string Glyph(this Section s) => s switch
    {
        Section.Projects => G.Apps, Section.Overview => G.Grid, Section.Deploy => G.Upload, Section.LiveLogs => G.Terminal, Section.Hosts => G.Server,
        Section.Keychain => G.Key, Section.Forwarding => G.Forward, Section.Sftp => G.Folder,
        Section.Database => G.Database, Section.Snippets => G.Code, Section.KnownHosts => G.Fingerprint, Section.Logs => G.History,
        Section.About => G.Info, _ => G.Person,
    };

    public static string Tint(this Section s) => s switch
    {
        Section.Projects => "#08DB89", Section.Overview => "#22C55E", Section.Deploy => "#06B6D4", Section.LiveLogs => "#38BDF8", Section.Hosts => "#3B82F6",
        Section.Keychain => "#F59E0B", Section.Forwarding => "#10B981", Section.Sftp => "#0EA5E9",
        Section.Database => "#F97316", Section.Snippets => "#8B5CF6", Section.KnownHosts => "#EC4899", Section.Logs => "#64748B",
        Section.About => "#6366F1", _ => "#14B8A6",
    };
}

/// <summary>State utama app. Semua data persisten lewat Vault (zevn-core, Rust) — sama dengan AppModel.swift.</summary>
internal sealed class AppModel
{
    public Phase Phase { get; private set; }
    public Section Section { get; private set; } = Section.Hosts;
    public Host[] Hosts { get; private set; } = [];
    public SshKey[] Keys { get; private set; } = [];
    public Snippet[] Snippets { get; private set; } = [];
    public PortForward[] Forwards { get; private set; } = [];
    public SessionLog[] Logs { get; private set; } = [];
    public DbConnection[] Databases { get; private set; } = [];
    public Project[] Projects { get; private set; } = [];
    public DbWorkspace? DbWorkspace { get; private set; }

    /// <summary>Project aktif (disimpan lokal di pengaturan, bukan di vault).</summary>
    public string? ActiveProjectId => Settings.Str(Settings.ActiveProject);
    public Project? ActiveProject => Projects.FirstOrDefault(p => p.Id == ActiveProjectId);

    /// <summary>Host yang terlihat: semua host, atau hanya milik project aktif.</summary>
    public Host[] ProjectHosts
    {
        get
        {
            if (ActiveProject is not { } p) return Hosts;
            var ids = p.HostIds.ToHashSet();
            return Hosts.Where(h => ids.Contains(h.Id)).ToArray();
        }
    }

    /// <summary>Aturan tunnel milik host di project aktif.</summary>
    public PortForward[] ProjectForwards
    {
        get
        {
            if (ActiveProject is not { } p) return Forwards;
            var ids = p.HostIds.ToHashSet();
            return Forwards.Where(f => ids.Contains(f.HostId)).ToArray();
        }
    }

    /// <summary>Tunnel yang dinyalakan otomatis oleh project aktif (dihentikan saat project berganti).</summary>
    private readonly HashSet<string> _autoTunnels = [];

    public string? AccountEmail { get; private set; }
    public string? PendingAccountEmail { get; set; }
    public byte[]? PendingRemoteBlob { get; set; }
    public string? ProfileName { get; private set; } = Settings.Str(Settings.ProfileName);

    public List<TerminalSession> Sessions { get; } = [];
    public Guid? ActiveSessionId { get; private set; }

    /// <summary>Panel Info server (spesifikasi & grafik) untuk host di tab aktif.</summary>
    public bool ShowServerInfo { get; private set; }
    public ServerStats? ServerStats { get; private set; }

    public void ToggleServerInfo(bool? show = null)
    {
        ShowServerInfo = show ?? !ShowServerInfo;
        SyncServerStats();
        Raise("serverinfo");
    }

    /// <summary>Hubungkan / putus panel Info server mengikuti tab aktif.</summary>
    public void SyncServerStats()
    {
        var s = ActiveSession;
        if (!ShowServerInfo || s == null || s.IsLocal || SshTarget(s.Host.Id) is not { } target)
        {
            if (ServerStats == null) return;
            ServerStats.Stop();
            ServerStats = null;
            Raise("serverinfo");
            return;
        }
        if (ServerStats?.Host.Id == s.Host.Id) return;
        ServerStats?.Stop();
        ServerStats = new ServerStats(s.Host);
        ServerStats.Start(target);
        Raise("serverinfo");
    }

    public void RetryServerStats()
    {
        if (ServerStats is { } st && SshTarget(st.Host.Id) is { } t) st.Retry(t);
    }
    public HashSet<string> Selection { get; } = [];
    public string? Toast { get; private set; }
    public Host? EditingHost { get; private set; }

    public SyncKind Sync { get; private set; } = SyncKind.Off;
    public DateTimeOffset? LastSynced { get; private set; }
    public string? SyncError { get; private set; }
    public AvailableUpdate? Update { get; private set; }
    public bool UpdateDismissed { get; set; }

    public TunnelManager Tunnels { get; } = new();
    public SftpModel Sftp { get; }
    public DeployModel Deploy { get; }
    public LogModel LiveLogs { get; }

    public bool IsDark { get; private set; } = true;
    public string Appearance { get; private set; } = Settings.Str(Settings.Appearance) ?? "dark";

    /// <summary>Kategori perubahan: phase, data, nav, sessions, toast, sync, update, editing, selection, theme, db, sftp, tunnels.</summary>
    public event Action<string>? Changed;

    private Vault? _vault;
    private CancellationTokenSource? _syncCts;
    private DriveClient.RemoteFile? _remoteFile;
    private readonly DispatcherQueue _dq;
    private readonly Windows.UI.ViewManagement.UISettings _ui = new();

    public AppModel()
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        Phase = ZevnCoreMethods.VaultExists(Config.VaultPath) ? Phase.Locked : Phase.Welcome;
        Sftp = new SftpModel(this);
        Deploy = new DeployModel(this);
        LiveLogs = new LogModel(this);
        Tunnels.Changed += () => Raise("tunnels");
        IsDark = ComputeDark();
        _ui.ColorValuesChanged += (_, _) => _dq.TryEnqueue(() =>
        {
            var d = ComputeDark();
            if (d != IsDark) { IsDark = d; Raise("theme"); }
        });
    }

    public void Raise(string what) => Changed?.Invoke(what);

    // MARK: Tampilan

    private bool ComputeDark()
    {
        if (Appearance == "light") return false;
        if (Appearance == "dark") return true;
        var bg = _ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return bg.R < 128;
    }

    public void SetAppearance(string mode)
    {
        Appearance = mode;
        Settings.Set(Settings.Appearance, mode);
        IsDark = ComputeDark();
        foreach (var s in Sessions) if (s.ThemeId == null) s.PushTheme();
        Raise("theme");
    }

    public bool HideAddresses
    {
        get => Settings.Bool(Settings.HideAddresses, true);
        set { Settings.Set(Settings.HideAddresses, value); Raise("data"); }
    }

#if DEBUG
    /// <summary>Vault contoh untuk tes tampilan (hanya build debug, folder ZEVN_DATA_DIR).</summary>
    public void LoadDemo()
    {
        var path = Config.VaultPath;
        var v = File.Exists(path) ? Vault.Unlock(path, "demo-password-123") : Vault.Create(path, "demo-password-123");
        if (v.Hosts().Length == 0)
        {
            var samples = new[] {
                ("prod-api", "10.0.1.10", "Production", new[] { "api", "prod" }, "#3FD0B4", "ubuntu"),
                ("prod-db", "10.0.1.20", "Production", new[] { "postgres" }, "#F0616D", "postgres"),
                ("staging", "staging.zevn.local", "Staging", new[] { "web" }, "#5B9CF5", "deploy"),
                ("homelab", "192.168.1.5", (string?)null, new[] { "pi" }, "#9D7CF2", "pi"),
            };
            foreach (var (label, hostname, group, tags, color, user) in samples)
                v.UpsertHost(HostExt.Blank() with { Label = label, Hostname = hostname, Group = group, Tags = tags, Color = color, Username = user });
            v.GenerateKey("Laptop Windows");
            var hs = v.Hosts();
            v.UpsertSnippet(new Snippet("", "Cek disk", "df -h", 0, false));
            v.UpsertSnippet(new Snippet("", "Update sistem", "sudo apt update && sudo apt upgrade -y", 0, false));
            v.UpsertForward(new PortForward("", "Postgres prod", hs[1].Id, ForwardKind.Local, "127.0.0.1", 5433, "localhost", 5432, 0, false));
            var l = v.StartLog(hs[0].Id, hs[0].Title(), hs[0].Address());
            v.FinishLog(l, 0);
            v.UpsertDatabase(new DbConnection("", "Produksi API", DbEngine.Postgres, "10.0.1.20", 5432, "postgres", null, "app", "", true, hs[0].Id, "#336791", 0, false));
            v.UpsertDatabase(new DbConnection("", "Cache", DbEngine.Redis, "127.0.0.1", 6379, "", null, "0", "", false, null, "#DC382D", 0, false));
        }
        var env = Environment.GetEnvironmentVariable("ZEVN_TEST_HOST");
        if (env is { Length: > 0 })
        {
            // ZEVN_TEST_HOST=user@host:port ; ZEVN_TEST_PW=password ; ZEVN_TEST_KEY=path private key
            var at = env.IndexOf('@'); var colon = env.LastIndexOf(':');
            var h = HostExt.Blank() with
            {
                Label = "server-tes", Username = env[..at], Hostname = env[(at + 1)..colon], Port = ushort.Parse(env[(colon + 1)..]), Color = "#F2A65A",
                Password = Environment.GetEnvironmentVariable("ZEVN_TEST_PW"),
            };
            if (Environment.GetEnvironmentVariable("ZEVN_TEST_KEY") is { Length: > 0 } kp)
            {
                var pem = File.ReadAllText(kp);
                var k = v.Keys().FirstOrDefault(x => x.Label == "tes-terminal" && x.PrivateKey.Trim() == pem.Trim())
                        ?? v.ImportKey("tes-terminal", pem, Environment.GetEnvironmentVariable("ZEVN_TEST_KEYPASS"));
                h = h with { Auth = AuthMethod.Key, KeyId = k.Id };
            }
            // ZEVN_TEST_PROXY=socks5|http,host,port,user,pass
            if (Environment.GetEnvironmentVariable("ZEVN_TEST_PROXY") is { Length: > 0 } px && px.Split(',') is { Length: 5 } pp)
                h = h with { Proxy = new HostProxy(pp[0] == "http" ? ProxyKind.Http : ProxyKind.Socks5, pp[1], ushort.Parse(pp[2]), pp[3], pp[4]) };
            var old = v.Hosts().FirstOrDefault(x => x.Label == "server-tes");
            v.UpsertHost(old == null ? h : h with { Id = old.Id });
        }
        Open(v);
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_SECTION") is { Length: > 0 } sec && Enum.TryParse<Section>(sec, true, out var s)) Navigate(s);
        var test = Hosts.FirstOrDefault(x => x.Label == "server-tes");
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_LIGHT") == "1") SetAppearance("light");
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_EDIT") is "1" or "test") Edit(Environment.GetEnvironmentVariable("ZEVN_DEMO_EDIT") == "test" ? test : Hosts.FirstOrDefault());
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_SFTP") == "1" && test != null) _ = OpenSftp(test);
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_TUNNEL") is { Length: > 0 } tport && test != null)
        {
            // Tunnel tes: 127.0.0.1:<tport> → 127.0.0.1:<port ssh server> lewat server-tes
            var f = Forwards.FirstOrDefault(x => x.Label == "tunnel-tes") ?? _vault!.UpsertForward(new PortForward("", "tunnel-tes", test.Id, ForwardKind.Local, "127.0.0.1", ushort.Parse(tport), "127.0.0.1", test.Port, 0, false));
            Reload();
            Navigate(Section.Forwarding);
            ToggleTunnel(f);
        }
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_DB") == "1")
        {
            if (Environment.GetEnvironmentVariable("ZEVN_DEMO_DBTREE") is "0" or "1") Settings.Set(Settings.DbShowTree, Environment.GetEnvironmentVariable("ZEVN_DEMO_DBTREE") == "1");
            var file = Path.Combine(Config.DataDir, "toko.sqlite");
            var conn = Databases.FirstOrDefault(x => x.Label == "Toko Lokal") ?? _vault!.UpsertDatabase(new DbConnection("", "Toko Lokal", DbEngine.Sqlite, "", 0, "", null, "", file, false, null, "#0F80CC", 0, false));
            Reload();
            _ = Task.Run(async () =>
            {
                if (!File.Exists(file))
                {
                    var s = await DbSession.Connect(conn, null);
                    await s.Run("CREATE TABLE produk (id INTEGER PRIMARY KEY, nama TEXT, harga INTEGER, stok INTEGER, kategori TEXT)");
                    await s.Run("CREATE TABLE pelanggan (id INTEGER PRIMARY KEY, nama TEXT, kota TEXT)");
                    await s.Run("CREATE VIEW stok_rendah AS SELECT * FROM produk WHERE stok < 10");
                    await s.Run("INSERT INTO produk (nama, harga, stok, kategori) VALUES ('Kopi Gayo 250g', 85000, 42, 'Minuman'), ('Teh Tarik', 18000, 7, 'Minuman'), ('Keripik Pisang', 22000, 120, 'Camilan'), ('Kue Adee', 35000, NULL, 'Kue'), ('Madu Hutan', 120000, 5, 'Herbal'), ('Kopi Ulee Kareng', 65000, 31, 'Minuman')");
                }
            }).ContinueWith(_ => _dq.TryEnqueue(async () =>
            {
                await OpenDatabase(conn);
                var mode = Environment.GetEnvironmentVariable("ZEVN_DEMO_DBTAB");
                if (DbWorkspace is { } ws && ws.DbTables.TryGetValue("main", out var tables) && tables.FirstOrDefault(x => x.Name == "produk") is { } t && mode != "overview")
                {
                    if (mode == "sql") { ws.CurrentTab = DbWorkspace.Tab.Sql; ws.Query = "SELECT nama, harga, stok, kategori FROM produk ORDER BY harga DESC;"; await ws.OpenTable(t, "main"); await ws.SetTab(DbWorkspace.Tab.Sql); await ws.Run(); }
                    else if (mode == "structure") { await ws.OpenTable(t, "main"); await ws.SetTab(DbWorkspace.Tab.Structure); }
                    else if (mode == "search")
                    {
                        // ZEVN_DEMO_DBFIND=kolom=nilai;kolom=nilai ; ZEVN_DEMO_DBSUBMIT=1 ; ZEVN_DEMO_DBQUICK=teks
                        await ws.OpenTable(t, "main"); await ws.SetTab(DbWorkspace.Tab.Search);
                        foreach (var pair in (Environment.GetEnvironmentVariable("ZEVN_DEMO_DBFIND") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                            if (pair.Split('=', 2) is [var k, var v] && ws.SearchForm.FirstOrDefault(x => x.Column == k) is { } fld) fld.Value = v;
                        await ws.SetTab(DbWorkspace.Tab.Search);
                        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_DBSUBMIT") == "1") await ws.SubmitSearch();
                        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_DBQUICK") is { Length: > 0 } q) { ws.QuickSearch = q; await ws.SubmitQuickSearch(); }
                    }
                    else await ws.OpenTable(t, "main");
                }
            }));
        }
        // ZEVN_DEMO_LOCAL=powershell|cmd|wsl|pwsh : buka terminal lokal (ZEVN_DEMO_RUN dikirim setelah siap)
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_LOCAL") is { Length: > 0 } ls)
            OpenLocalTerminal(LocalShell.Available.FirstOrDefault(x => x.Id == ls), Environment.GetEnvironmentVariable("ZEVN_DEMO_RUN"));
        if (Environment.GetEnvironmentVariable("ZEVN_DEMO_CONNECT") == "1" && Hosts.FirstOrDefault(x => x.Label == "server-tes") is { } t)
        {
            Connect([t.Id], Environment.GetEnvironmentVariable("ZEVN_DEMO_RUN"));
            if (Environment.GetEnvironmentVariable("ZEVN_DEMO_INFO") == "1") ToggleServerInfo(true);
            // ZEVN_DEMO_TYPE = ketikan dipisah "|" (\r = Enter, <accept> = terima saran autocomplete)
            var steps = (Environment.GetEnvironmentVariable("ZEVN_DEMO_TYPE") ?? "").Split('|').Select(x => x.Replace("\\r", "\r")).ToList();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step.Length == 0) continue;
                _ = Task.Delay(4000 + i * 1300).ContinueWith(_ => _dq.TryEnqueue(() =>
                {
                    if (step == "<accept>") ActiveSession?.DebugAccept(); else ActiveSession?.Send(step);
                }));
            }
        }
    }
#endif

    // MARK: Vault

    public void SetPhase(Phase p) { Phase = p; Raise("phase"); }

    public async Task CreateVault(string password)
    {
        var path = Config.VaultPath;
        var v = await Task.Run(() => Vault.Create(path, password));
        if (PendingAccountEmail != null) v.SetAccountEmail(PendingAccountEmail);
        Open(v);
        if (HelloUnlock.HasStoredKey) HelloUnlock.Store(v.ExportKey());
    }

    public async Task Unlock(string password)
    {
        var path = Config.VaultPath;
        var v = await Task.Run(() => Vault.Unlock(path, password));
        Open(v);
        if (HelloUnlock.HasStoredKey) HelloUnlock.Store(v.ExportKey());
    }

    /// <summary>Pulihkan vault dari Google Drive di perangkat ini.</summary>
    public async Task Restore(string password)
    {
        if (PendingRemoteBlob == null) return;
        var path = Config.VaultPath;
        await File.WriteAllBytesAsync(path, PendingRemoteBlob);
        try
        {
            var v = await Task.Run(() => Vault.Unlock(path, password));
            if (PendingAccountEmail != null) v.SetAccountEmail(PendingAccountEmail);
            PendingRemoteBlob = null;
            Open(v);
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    public async Task UnlockWithHello()
    {
        var key = await HelloUnlock.Retrieve();
        var path = Config.VaultPath;
        var v = await Task.Run(() => Vault.UnlockWithKey(path, key));
        Open(v);
    }

    public void EnableHello(bool on)
    {
        if (on && _vault != null) HelloUnlock.Store(_vault.ExportKey());
        else HelloUnlock.Forget();
        Raise("data");
    }

    public void Lock()
    {
        Deploy.Close();
        LiveLogs.Close();
        _ = Sftp.Disconnect();
        foreach (var s in Sessions) s.Terminate();
        Tunnels.StopAll();
        _syncCts?.Cancel();
        Sessions.Clear();
        ActiveSessionId = null;
        _vault = null;
        Hosts = []; Keys = []; Snippets = []; Forwards = []; Logs = []; Databases = []; Projects = [];
        _autoTunnels.Clear();
        DbWorkspace = null;
        EditingHost = null;
        Selection.Clear();
        Raise("sessions");
        SetPhase(Phase.Locked);
    }

    private void Open(Vault v)
    {
        _vault = v;
        Reload();
        AccountEmail = v.AccountEmail();
        Sync = AccountEmail == null ? SyncKind.Off : SyncKind.Idle;
        SetPhase(Phase.Main);
        StartAutoTunnels();
        if (AccountEmail != null) SyncNow();
        _ = CheckForUpdates(false);
    }

    public async Task CheckForUpdates(bool manual)
    {
        try
        {
            var u = await UpdateChecker.Check();
            Update = u;
            if (u != null) UpdateDismissed = false;
            Raise("update");
            if (manual && u == null) Flash($"Zevn Shell {Config.Version} sudah versi terbaru");
        }
        catch
        {
            if (manual) Flash("Gagal memeriksa pembaruan");
        }
    }

    private void Reload()
    {
        if (_vault == null) return;
        Hosts = _vault.Hosts();
        Keys = _vault.Keys();
        Snippets = _vault.Snippets();
        Forwards = _vault.Forwards();
        Logs = _vault.Logs();
        Databases = _vault.Databases();
        Projects = _vault.Projects();
        // Project yang dihapus di perangkat lain tidak boleh tertinggal sebagai konteks aktif.
        if (ActiveProjectId is { } id && Projects.All(p => p.Id != id)) Settings.Set(Settings.ActiveProject, null);
    }

    // MARK: Project

    public Project? ProjectById(string? id) => id == null ? null : Projects.FirstOrDefault(p => p.Id == id);

    /// <summary>Aktifkan project (null = semua host). Tunnel yang dinyalakan otomatis ikut dihentikan.</summary>
    public void SetActiveProject(string? id, bool announce = true)
    {
        if (id != null && Projects.All(p => p.Id != id)) id = null;
        StopAutoTunnels();
        Settings.Set(Settings.ActiveProject, id);
        StartAutoTunnels();
        Raise("data");
        Raise("tunnels");
        if (announce) Flash(id == null ? "Project dinonaktifkan — semua host ditampilkan" : $"Project \u201c{ActiveProject?.Name}\u201d aktif");
    }

    public Project? Save(Project p)
    {
        if (_vault == null) return null;
        try
        {
            var saved = _vault.UpsertProject(p);
            var wasActive = saved.Id == ActiveProjectId;
            DataChanged();
            if (wasActive) { StopAutoTunnels(); StartAutoTunnels(); Raise("tunnels"); }
            return saved;
        }
        catch (Exception e) { Flash(Errors.Text(e)); return null; }
    }

    public void Delete(Project p)
    {
        if (p.Id == ActiveProjectId) SetActiveProject(null, false);
        try { _vault?.DeleteProject(p.Id); } catch { }
        DataChanged();
    }

    /// <summary>Nyalakan aturan tunnel milik project aktif bila project memintanya.</summary>
    private void StartAutoTunnels()
    {
        if (ActiveProject is not { AutoTunnels: true } p) return;
        foreach (var f in Forwards.Where(f => p.HostIds.Contains(f.HostId)))
        {
            if (Tunnels.IsRunning(f.Id) || HostById(f.HostId) is not { } h) continue;
            Tunnels.Start(f, h, KeyById(h.KeyId), HostById(h.JumpHostId));
            _autoTunnels.Add(f.Id);
        }
    }

    private void StopAutoTunnels()
    {
        foreach (var id in _autoTunnels.ToList()) Tunnels.Stop(id);
        _autoTunnels.Clear();
    }

    /// <summary>Setelah setiap perubahan data: muat ulang tampilan & jadwalkan sync.</summary>
    private void DataChanged()
    {
        Reload();
        Raise("data");
        foreach (var s in Sessions) s.PushConfig();
        ScheduleSync();
    }

    // MARK: Akun Google & sync

    private void SaveProfile(string? name, string? picture)
    {
        Settings.Set(Settings.ProfileName, name);
        Settings.Set(Settings.ProfilePicture, picture);
        ProfileName = name;
    }

    /// <summary>Login Google. Jika vault sudah ada di Drive (dan belum ada vault lokal), kembalikan true (perlu dipulihkan).</summary>
    public async Task<bool> SignInGoogle()
    {
        var r = await GoogleAuth.SignIn();
        SaveProfile(r.Name, r.Picture);
        PendingAccountEmail = r.Email;
        if (_vault != null)
        {
            _vault.SetAccountEmail(r.Email);
            AccountEmail = r.Email;
            Sync = SyncKind.Idle;
            Raise("sync");
            SyncNow();
            return false;
        }
        if (await DriveClient.Find() is { } file)
        {
            PendingRemoteBlob = await DriveClient.Download(file);
            return true;
        }
        return false;
    }

    public void DisconnectGoogle()
    {
        GoogleTokens.Forget();
        SaveProfile(null, null);
        try { _vault?.SetAccountEmail(null); } catch { }
        AccountEmail = null;
        _remoteFile = null;
        Sync = SyncKind.Off;
        Raise("sync");
    }

    /// <summary>Keluar: putuskan Google & kunci; jika deleteLocal, hapus vault & kunci Hello dari PC ini.</summary>
    public void SignOut(bool deleteLocal)
    {
        DisconnectGoogle();
        Lock();
        if (!deleteLocal) return;
        try { File.Delete(Config.VaultPath); } catch { }
        HelloUnlock.Forget();
        PendingRemoteBlob = null;
        PendingAccountEmail = null;
        SetPhase(Phase.Welcome);
    }

    private void ScheduleSync()
    {
        if (AccountEmail == null) return;
        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _ = Task.Delay(2500, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _dq.TryEnqueue(() => _ = PerformSync(cts.Token)); });
    }

    public void SyncNow()
    {
        if (AccountEmail == null) return;
        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _ = PerformSync(cts.Token);
    }

    private async Task PerformSync(CancellationToken ct)
    {
        if (_vault == null) return;
        var vault = _vault;
        if (!GoogleTokens.HasRefreshToken) { Sync = SyncKind.NeedsLogin; Raise("sync"); return; }
        Sync = SyncKind.Syncing; Raise("sync");
        try
        {
            var remote = await DriveClient.Find();
            _remoteFile = remote;
            if (ct.IsCancellationRequested) return;
            if (remote != null)
            {
                var blob = await DriveClient.Download(remote);
                var outcome = vault.MergeRemote(blob);
                if (outcome.LocalChanged) { Reload(); Raise("data"); }
                if (outcome.RemoteOutdated) await DriveClient.Upload(vault.EncryptedBlob(), remote);
            }
            else
            {
                await DriveClient.Upload(vault.EncryptedBlob(), null);
            }
            Sync = SyncKind.Synced; LastSynced = DateTimeOffset.Now; SyncError = null;
        }
        catch (NotSignedInException) { Sync = SyncKind.NeedsLogin; }
        catch (Exception e) { Sync = SyncKind.Failed; SyncError = Errors.Text(e); }
        Raise("sync");
    }

    public string SyncStatusText => Sync switch
    {
        SyncKind.Off => "Lokal saja",
        SyncKind.Idle => "Siap sync",
        SyncKind.Syncing => "Menyinkronkan…",
        SyncKind.Synced => LastSynced is { } d && DateTimeOffset.Now - d < TimeSpan.FromMinutes(1) ? "Tersinkron" : $"Sinkron {Ui.Relative(LastSynced ?? DateTimeOffset.Now)}",
        SyncKind.Failed => "Sync gagal — klik untuk ulang",
        _ => "Masuk ulang Google",
    };

    // MARK: Navigasi

    public void Navigate(Section s)
    {
        Section = s;
        ActiveSessionId = null;
        Raise("nav");
    }

    public void ShowHome() { ActiveSessionId = null; Raise("nav"); }

    public void Edit(Host? h) { EditingHost = h; Raise("editing"); }

    public void ToggleSelect(string id)
    {
        if (!Selection.Remove(id)) Selection.Add(id);
        Raise("selection");
    }

    public void SetSelection(IEnumerable<string> ids) { Selection.Clear(); foreach (var i in ids) Selection.Add(i); Raise("selection"); }

    // MARK: Hosts

    public Host? Save(Host host)
    {
        if (_vault == null) return null;
        try
        {
            var saved = _vault.UpsertHost(host);
            foreach (var s in Sessions.Where(s => s.Host.Id == saved.Id && s.ThemeId != saved.TerminalTheme)) s.ApplyTheme(saved.TerminalTheme);
            DataChanged();
            return saved;
        }
        catch (Exception e) { Flash(Errors.Text(e)); return null; }
    }

    public void Delete(Host host)
    {
        try { _vault?.DeleteHost(host.Id); } catch { }
        Selection.Remove(host.Id);
        if (EditingHost?.Id == host.Id) Edit(null);
        DataChanged();
    }

    public Host? HostById(string? id) => id == null ? null : Hosts.FirstOrDefault(h => h.Id == id);
    public SshKey? KeyById(string? id) => id == null ? null : Keys.FirstOrDefault(k => k.Id == id);

    // MARK: Keychain

    public SshKey GenerateKey(string label)
    {
        var k = _vault!.GenerateKey(label);
        DataChanged();
        return k;
    }

    public void ImportKey(string label, string privateKey, string? passphrase)
    {
        _vault!.ImportKey(label, privateKey, passphrase);
        DataChanged();
    }

    public void RenameKey(SshKey k, string label) { try { _vault?.RenameKey(k.Id, label); } catch { } DataChanged(); }
    public void DeleteKey(SshKey k) { try { _vault?.DeleteKey(k.Id); } catch { } DataChanged(); }

    // MARK: Snippets

    public void Save(Snippet s) { try { _vault?.UpsertSnippet(s); DataChanged(); } catch (Exception e) { Flash(Errors.Text(e)); } }
    public void Delete(Snippet s) { try { _vault?.DeleteSnippet(s.Id); } catch { } DataChanged(); }

    public void RunInActiveSession(Snippet s) => ActiveSession?.Send(s.Command + "\r");

    // MARK: Port forwarding

    public void Save(PortForward f) { try { _vault?.UpsertForward(f); DataChanged(); } catch (Exception e) { Flash(Errors.Text(e)); } }

    public void Delete(PortForward f)
    {
        Tunnels.Stop(f.Id);
        try { _vault?.DeleteForward(f.Id); } catch { }
        DataChanged();
    }

    public void ToggleTunnel(PortForward f)
    {
        if (Tunnels.IsRunning(f.Id)) { _autoTunnels.Remove(f.Id); Tunnels.Stop(f.Id); }
        else if (HostById(f.HostId) is { } h) Tunnels.Start(f, h, KeyById(h.KeyId), HostById(h.JumpHostId));
        else Flash("Host untuk aturan ini sudah tidak ada");
    }

    // MARK: Database

    public void Save(DbConnection c) { try { _vault?.UpsertDatabase(c); DataChanged(); } catch (Exception e) { Flash(Errors.Text(e)); } }

    public void Delete(DbConnection c)
    {
        if (DbWorkspace?.Conn.Id == c.Id) DbWorkspace = null;
        try { _vault?.DeleteDatabase(c.Id); } catch { }
        DataChanged();
    }

    public async Task OpenDatabase(DbConnection c)
    {
        var ws = new DbWorkspace(c);
        DbWorkspace = ws;
        Section = Section.Database;
        ActiveSessionId = null;
        Raise("nav");
        await ws.Connect(SshTarget(c.SshHostId));
    }

    public void CloseDatabase() { DbWorkspace = null; Raise("db"); }

    /// <summary>Target SSH dari host tersimpan (untuk tunnel database & SFTP).</summary>
    public SshTarget? SshTarget(string? id)
    {
        if (HostById(id) is not { } h) return null;
        var k = KeyById(h.KeyId);
        return new SshTarget(h.Hostname, h.Port, h.Username,
            h.Auth == AuthMethod.Password ? h.Password : null,
            h.Auth == AuthMethod.Key ? k?.PrivateKey : null,
            h.Auth == AuthMethod.Key ? k?.Passphrase : null, true, h.Proxy);
    }

    // MARK: SFTP

    public async Task OpenSftp(Host h)
    {
        Section = Section.Sftp;
        ActiveSessionId = null;
        Raise("nav");
        await Sftp.Connect(h, KeyById(h.KeyId));
    }

    // MARK: Logs

    public string? LogStart(Host h)
    {
        try { var id = _vault?.StartLog(h.Id, h.Title(), h.Address()); Reload(); Raise("logs"); return id; } catch { return null; }
    }

    public void LogEnd(string? id, int? code)
    {
        if (id == null || _vault == null) return;
        try { _vault.FinishLog(id, code); } catch { }
        Reload();
        Raise("logs");
        ScheduleSync();
    }

    public void ClearLogs() { try { _vault?.ClearLogs(); } catch { } DataChanged(); }

    // MARK: Sesi

    /// <summary>Buka satu tab per host. `run` = perintah yang dikirim setelah tersambung.</summary>
    public void Connect(IEnumerable<string> ids, string? run = null)
    {
        var targets = ids.Select(HostById).OfType<Host>().ToList();
        if (targets.Count == 0) return;
        TerminalSession? first = null;
        foreach (var h in targets)
        {
            var s = new TerminalSession(h, KeyById(h.KeyId), HostById(h.JumpHostId), run, this);
            s.Changed += () => Raise("session");
            Sessions.Add(s);
            first ??= s;
        }
        ActiveSessionId = first!.Id;
        Selection.Clear();
        Raise("sessions");
        Raise("selection");
    }

    /// <summary>Buka tab terminal lokal (PowerShell / CMD / WSL).</summary>
    public void OpenLocalTerminal(LocalShell? shell = null, string? run = null)
    {
        shell ??= LocalShell.Default;
        var s = new TerminalSession(shell.AsHost(), null, null, run, this, shell);
        s.Changed += () => Raise("session");
        Sessions.Add(s);
        ActiveSessionId = s.Id;
        Raise("sessions");
    }

    public void Close(TerminalSession s)
    {
        s.Terminate();
        var idx = Sessions.IndexOf(s);
        if (idx < 0) return;
        Sessions.RemoveAt(idx);
        if (ActiveSessionId == s.Id) ActiveSessionId = Sessions.Count == 0 ? null : Sessions[Math.Max(0, idx - 1)].Id;
        Raise("sessions");
    }

    public void Activate(TerminalSession s) { ActiveSessionId = s.Id; Raise("sessions"); s.Focus(); }

    public TerminalSession? ActiveSession => Sessions.FirstOrDefault(s => s.Id == ActiveSessionId);

    public void CloseActive() { if (ActiveSession is { } s) Close(s); }

    public void FocusTab(int n)
    {
        if (n == 0) { ShowHome(); return; }
        if (n >= 1 && n <= Sessions.Count) Activate(Sessions[n - 1]);
    }

    public void CycleTab(int dir)
    {
        if (Sessions.Count == 0) return;
        var i = Sessions.FindIndex(s => s.Id == ActiveSessionId);
        var next = i < 0 ? 0 : (i + dir + Sessions.Count) % Sessions.Count;
        Activate(Sessions[next]);
    }

    public void SessionStatusChanged() => Raise("sessions");

    public void HandleTerminalShortcut(string k)
    {
        switch (k)
        {
            case "nextTab": CycleTab(1); break;
            case "prevTab": CycleTab(-1); break;
            case "close": CloseActive(); break;
            case "lock": Lock(); break;
            default:
                if (k.StartsWith("tab") && int.TryParse(k[3..], out var n)) FocusTab(n);
                break;
        }
    }

    public void PushAutocompleteConfig() { foreach (var s in Sessions) s.PushConfig(); }

    public int OnlineCount => Sessions.Where(s => s.State == TerminalSession.Status.Connected).Select(s => s.Host.Id).Distinct().Count();

    public void Flash(string message)
    {
        Toast = message;
        Raise("toast");
        _ = Task.Delay(2600).ContinueWith(_ => _dq.TryEnqueue(() => { if (Toast == message) { Toast = null; Raise("toast"); } }));
    }
}
