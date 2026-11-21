using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using uniffi.zevn_core;

namespace ZevnShell;

/// <summary>Layanan database siap pakai (beberapa memakai protokol engine lain).</summary>
internal sealed record DbPreset(string Id, string Name, DbEngine Engine, ushort Port, bool Tls, string Color, string Abbr, string Hint)
{
    public static readonly DbPreset[] All =
    [
        new("postgres", "PostgreSQL", DbEngine.Postgres, 5432, false, "#336791", "PG", "Database relasional open source"),
        new("mysql", "MySQL", DbEngine.Mysql, 3306, false, "#00758F", "My", "Database relasional populer"),
        new("mariadb", "MariaDB", DbEngine.Mariadb, 3306, false, "#C0765A", "Ma", "Turunan MySQL"),
        new("sqlserver", "SQL Server", DbEngine.Sqlserver, 1433, false, "#CC2927", "MS", "Microsoft SQL Server / MSSQL"),
        new("sqlite", "SQLite", DbEngine.Sqlite, 0, false, "#0F80CC", "SL", "File database lokal"),
        new("redis", "Redis", DbEngine.Redis, 6379, false, "#DC382D", "Rd", "Key-value in-memory"),
        new("mongodb", "MongoDB", DbEngine.Mongodb, 27017, false, "#47A248", "Mg", "Database dokumen"),
        new("clickhouse", "ClickHouse", DbEngine.Clickhouse, 8123, false, "#E8B500", "CH", "Analitik kolumnar (HTTP)"),
        new("cockroach", "CockroachDB", DbEngine.Postgres, 26257, true, "#6933FF", "Cr", "Protokol PostgreSQL"),
        new("timescale", "TimescaleDB", DbEngine.Postgres, 5432, false, "#FDB515", "Ts", "Time-series di atas PostgreSQL"),
        new("yugabyte", "YugabyteDB", DbEngine.Postgres, 5433, false, "#FF6E42", "Yb", "Protokol PostgreSQL"),
        new("supabase", "Supabase", DbEngine.Postgres, 5432, true, "#3ECF8E", "Sb", "Postgres terkelola"),
        new("neon", "Neon", DbEngine.Postgres, 5432, true, "#00E599", "Ne", "Postgres serverless"),
        new("rds-pg", "Amazon RDS PostgreSQL", DbEngine.Postgres, 5432, true, "#FF9900", "RP", "AWS RDS / Aurora PostgreSQL"),
        new("rds-my", "Amazon Aurora MySQL", DbEngine.Mysql, 3306, true, "#FF9900", "RM", "AWS RDS / Aurora MySQL"),
        new("tidb", "TiDB", DbEngine.Mysql, 4000, false, "#E30C34", "Ti", "Protokol MySQL"),
        new("planetscale", "PlanetScale", DbEngine.Mysql, 3306, true, "#111111", "PS", "MySQL serverless"),
        new("percona", "Percona Server", DbEngine.Mysql, 3306, false, "#E4512B", "Pc", "Turunan MySQL"),
        new("azure-sql", "Azure SQL", DbEngine.Sqlserver, 1433, true, "#0078D4", "Az", "SQL Server di Azure"),
        new("valkey", "Valkey", DbEngine.Redis, 6379, false, "#6983FF", "Vk", "Protokol Redis"),
        new("dragonfly", "Dragonfly", DbEngine.Redis, 6379, false, "#8B5CF6", "Df", "Protokol Redis"),
        new("upstash", "Upstash Redis", DbEngine.Redis, 6379, true, "#00E9A3", "Up", "Redis serverless"),
        new("atlas", "MongoDB Atlas", DbEngine.Mongodb, 27017, true, "#00684A", "At", "Tempel URI mongodb+srv:// di kolom host"),
    ];

    public static DbPreset ForEngine(DbEngine e) => All.First(p => p.Engine == e);
    public static DbPreset Find(DbConnection c) =>
        All.FirstOrDefault(p => p.Color.Equals(c.Color, StringComparison.OrdinalIgnoreCase) && p.Engine == c.Engine) ?? ForEngine(c.Engine);
}

internal static class DbExt
{
    public static string Title(this DbEngine e) => DbPreset.ForEngine(e).Name;
    public static bool IsSql(this DbEngine e) => e != DbEngine.Redis && e != DbEngine.Mongodb;
    public static bool SupportsStructure(this DbEngine e) => e.IsSql();
    public static bool ListsAcrossDatabases(this DbEngine e) => e is not (DbEngine.Postgres or DbEngine.Redis);

    public static string Placeholder(this DbEngine e) => e switch
    {
        DbEngine.Redis => "GET key\nHGETALL user:1",
        DbEngine.Mongodb => "db.users.find({\"aktif\": true})",
        DbEngine.Sqlserver => "SELECT TOP 100 * FROM dbo.users",
        _ => "SELECT * FROM users LIMIT 100;",
    };

    public static HashSet<string> SystemDatabases(this DbEngine e) => e switch
    {
        DbEngine.Mysql or DbEngine.Mariadb => ["information_schema", "mysql", "performance_schema", "sys"],
        DbEngine.Sqlserver => ["master", "tempdb", "model", "msdb"],
        DbEngine.Clickhouse => ["system", "INFORMATION_SCHEMA", "information_schema"],
        DbEngine.Mongodb => ["admin", "local", "config"],
        _ => [],
    };

    public static string DefaultUser(DbEngine e) => e switch
    {
        DbEngine.Postgres => "postgres", DbEngine.Mysql or DbEngine.Mariadb => "root", DbEngine.Sqlserver => "sa",
        DbEngine.Clickhouse => "default", _ => "",
    };

    public static DbConnection Blank(DbPreset p) => new("", "", p.Engine, p.Engine == DbEngine.Sqlite ? "" : "127.0.0.1", p.Port,
        DefaultUser(p.Engine), null, "", "", p.Tls, null, p.Color, 0, false);

    public static string Title(this DbConnection c) =>
        !string.IsNullOrEmpty(c.Label) ? c.Label : c.Engine == DbEngine.Sqlite ? Path.GetFileName(c.FilePath) : c.Host;

    public static string? SizeText(this DbTable t) => t.SizeBytes is { } s && s > 0 ? Ui.Bytes(s) : null;
    public static string? RowsText(this DbTable t) => t.Rows?.ToString("N0", new System.Globalization.CultureInfo("id-ID"));
}

/// <summary>Satu koneksi database yang sedang terbuka (tampilan ala phpMyAdmin) — padanan DbWorkspace di Swift.</summary>
internal sealed class DbWorkspace
{
    public enum Status { Connecting, Ready, Failed }
    public enum Tab { Overview, Browse, Structure, Search, Sql }

    public static string TabTitle(Tab t) => t switch { Tab.Overview => "Ringkasan", Tab.Browse => "Telusuri", Tab.Structure => "Struktur", Tab.Search => "Cari", _ => "SQL" };
    public static string TabGlyph(Tab t) => t switch { Tab.Overview => G.Apps, Tab.Browse => G.Table, Tab.Structure => G.Structure, Tab.Search => G.Search, _ => G.Code };

    /// <summary>Satu baris form "Cari" (kueri dengan contoh, ala phpMyAdmin).</summary>
    public sealed class SearchField
    {
        public required string Column { get; init; }
        public required string Type { get; init; }
        public string Op { get; set; } = "=";
        public string Value { get; set; } = "";

        public bool NeedsValue => Op is not ("IS NULL" or "IS NOT NULL" or "= ''" or "!= ''");
        public bool IsActive => !NeedsValue || Value.Trim().Length > 0;

        public static readonly string[] TextOps = ["LIKE %...%", "NOT LIKE %...%", "LIKE", "NOT LIKE", "=", "!=", "IN (...)", "NOT IN (...)",
                                                   "BETWEEN", "NOT BETWEEN", "= ''", "!= ''", "IS NULL", "IS NOT NULL"];
        public static readonly string[] ValueOps = ["=", "!=", ">", ">=", "<", "<=", "LIKE %...%", "LIKE", "NOT LIKE", "IN (...)", "NOT IN (...)",
                                                    "BETWEEN", "NOT BETWEEN", "IS NULL", "IS NOT NULL"];

        /// <summary>Kolom angka/tanggal: operator perbandingan dulu; teks: LIKE %...% dulu.</summary>
        public static bool IsTextual(string type)
        {
            var t = type.ToLowerInvariant();
            string[] nonText = ["int", "dec", "num", "float", "double", "real", "money", "serial", "bit", "bool", "date", "time", "year"];
            return !nonText.Any(t.Contains) || t.Contains("char") || t.Contains("text");
        }
        public string[] Ops => IsTextual(Type) ? TextOps : ValueOps;
    }

    public DbConnection Conn { get; private set; }
    public Status State { get; private set; } = Status.Connecting;
    public string? FailMessage { get; private set; }

    public List<string> Databases { get; private set; } = [];
    public Dictionary<string, DbTable[]> DbTables { get; } = [];
    public HashSet<string> Expanded { get; } = [];
    public HashSet<string> LoadingDbs { get; } = [];
    public string? ActiveDatabase { get; private set; }
    public bool ShowSystem { get; set; }

    public string? SelectedDb { get; private set; }
    public DbTable? SelectedTable { get; private set; }
    public Tab CurrentTab { get; set; } = Tab.Overview;

    public QueryResult? Page { get; private set; }
    public uint PageSize { get; private set; } = 100;
    public ulong Offset { get; private set; }
    public ulong? TotalRows { get; private set; }
    public bool TotalIsEstimate { get; private set; }

    public QueryResult? Structure { get; private set; }

    // Cari: syarat aktif di Telusuri (dari form "Cari" atau kotak cari cepat)
    public List<SearchField> SearchForm { get; private set; } = [];
    public DbFilter[] Filters { get; private set; } = [];
    public bool FilterAny { get; private set; }
    public string QuickSearch { get; set; } = "";
    public bool IsFiltered => Filters.Length > 0;

    public string Query { get; set; } = "";
    public QueryResult? Result { get; private set; }
    public List<string> History { get; } = [];

    public bool Running { get; private set; }
    public string? Error { get; set; }

    public event Action? Changed;

    private DbSession? _session;
    private SshTarget? _ssh;

    public DbWorkspace(DbConnection conn) { Conn = conn; }

    private void Raise() => Changed?.Invoke();

    public List<string> UserDatabases => Databases.Where(d => !Conn.Engine.SystemDatabases().Contains(d)).ToList();
    public List<string> SystemDbs => Databases.Where(d => Conn.Engine.SystemDatabases().Contains(d)).ToList();

    public Tab[] AvailableTabs => SelectedTable != null
        ? (Conn.Engine.SupportsStructure() ? [Tab.Browse, Tab.Structure, Tab.Search, Tab.Sql] : [Tab.Browse, Tab.Sql])
        : [Tab.Overview, Tab.Sql];

    public async Task Connect(SshTarget? ssh)
    {
        _ssh = ssh;
        State = Status.Connecting; Raise();
        try
        {
            var s = await DbSession.Connect(Conn, ssh);
            _session = s;
            List<string> dbs;
            try { dbs = (await s.Databases()).ToList(); } catch { dbs = []; }
            if (Conn.Engine == DbEngine.Sqlite) dbs = ["main"];
            string? fallback = Conn.Engine switch { DbEngine.Sqlite => "main", DbEngine.Postgres => "postgres", DbEngine.Redis => "0", _ => null };
            var active = string.IsNullOrEmpty(Conn.Database) ? fallback : Conn.Database;
            Databases = dbs;
            ActiveDatabase = active;
            State = Status.Ready;
            Raise();
            // Buka otomatis: database di koneksi, atau satu-satunya database pengguna
            var auto = active ?? (UserDatabases.Count == 1 ? UserDatabases[0] : null);
            if (auto != null)
            {
                await Toggle(auto, true);
                SelectedDb ??= auto;
            }
            if ((Conn.Engine == DbEngine.Redis || Conn.Engine == DbEngine.Sqlite) && (active ?? Databases.FirstOrDefault()) is { } a)
            {
                await Toggle(a, true);
                SelectedDb = a;
            }
            Raise();
        }
        catch (Exception e)
        {
            State = Status.Failed;
            FailMessage = Errors.Text(e);
            Raise();
        }
    }

    public async Task Toggle(string db, bool forceOpen = false)
    {
        if (Expanded.Contains(db) && !forceOpen) { Expanded.Remove(db); Raise(); return; }
        Expanded.Add(db);
        Raise();
        if (!DbTables.ContainsKey(db)) await LoadTables(db);
    }

    public async Task LoadTables(string db)
    {
        if (_session == null) return;
        LoadingDbs.Add(db); Raise();
        try
        {
            DbTable[] t;
            if (Conn.Engine.ListsAcrossDatabases()) t = await _session.TablesIn(db);
            else
            {
                if (ActiveDatabase != db) await UseDatabase(db);
                t = await _session!.Tables();
            }
            DbTables[db] = t;
        }
        catch (Exception e) { Error = Errors.Text(e); }
        LoadingDbs.Remove(db);
        Raise();
    }

    public async Task Refresh()
    {
        if (_session == null) return;
        try { var dbs = (await _session.Databases()).ToList(); Databases = Conn.Engine == DbEngine.Sqlite ? ["main"] : dbs; } catch { }
        foreach (var db in Expanded.ToList()) await LoadTables(db);
        if (SelectedTable is { } t && CurrentTab == Tab.Browse) await LoadPage(t);
        Raise();
    }

    /// <summary>Jadikan database aktif (USE / SELECT n / sambung ulang untuk PostgreSQL).</summary>
    private async Task UseDatabase(string db)
    {
        if (_session == null) return;
        if (await _session.UseDatabase(db)) { ActiveDatabase = db; return; }
        var c = Conn with { Database = db };
        var s = await DbSession.Connect(c, _ssh);
        _session = s;
        Conn = c;
        ActiveDatabase = db;
    }

    /// <summary>Breadcrumb "Server": tampilkan semua database.</summary>
    public void ShowServer()
    {
        SelectedDb = null;
        SelectedTable = null;
        CurrentTab = Tab.Overview;
        Error = null;
        Raise();
    }

    public async Task SelectDatabase(string db)
    {
        SelectedDb = db;
        SelectedTable = null;
        CurrentTab = Tab.Overview;
        Error = null;
        Raise();
        if (ActiveDatabase != db && Conn.Engine != DbEngine.Sqlite)
        {
            try { await UseDatabase(db); } catch (Exception e) { Error = Errors.Text(e); Raise(); }
        }
        if (!Expanded.Contains(db) || !DbTables.ContainsKey(db)) await Toggle(db, true);
    }

    public async Task OpenTable(DbTable t, string db)
    {
        if (ActiveDatabase != db && Conn.Engine != DbEngine.Sqlite)
        {
            try { await UseDatabase(db); } catch (Exception e) { Error = Errors.Text(e); Raise(); return; }
        }
        SelectedDb = db;
        SelectedTable = t;
        if (CurrentTab == Tab.Overview || (!Conn.Engine.SupportsStructure() && CurrentTab is Tab.Structure or Tab.Search)) CurrentTab = Tab.Browse;
        Offset = 0;
        Page = null;
        Structure = null;
        SearchForm = [];
        Filters = [];
        QuickSearch = "";
        TotalRows = t.Rows;
        TotalIsEstimate = t.Rows != null;
        Error = null;
        if (_session != null) Query = _session.PageSql(t.Schema, t.Name, PageSize, 0);
        Raise();
        await LoadTab();
        // Hitung baris persis jika tabel tidak terlalu besar
        if (Conn.Engine.IsSql() && (t.Rows ?? 0) < 2_000_000 && _session != null)
        {
            try
            {
                var n = await _session.CountRows(t.Schema, t.Name);
                if (SelectedTable?.Name == t.Name) { TotalRows = n; TotalIsEstimate = false; Raise(); }
            }
            catch { }
        }
    }

    public async Task SetTab(Tab t) { CurrentTab = t; Raise(); await LoadTab(); }

    public async Task LoadTab()
    {
        if (SelectedTable is not { } t) return;
        if (CurrentTab == Tab.Browse && Page == null) await LoadPage(t);
        else if (CurrentTab == Tab.Structure && Structure == null) await LoadStructure(t);
        else if (CurrentTab == Tab.Search) await PrepareSearchForm(t);
    }

    // MARK: Cari

    /// <summary>Isi form "Cari" dari struktur tabel (kolom & tipe).</summary>
    public async Task PrepareSearchForm(DbTable t)
    {
        if (Structure == null) await LoadStructure(t);
        if (SearchForm.Count > 0 || Structure is not { } s) return;
        var typeIdx = Array.IndexOf(s.Columns, "Tipe"); if (typeIdx < 0) typeIdx = 1;
        SearchForm = s.Rows.Where(r => r.Length > 0 && r[0] != null).Select(r =>
        {
            var type = r.Length > typeIdx ? r[typeIdx] ?? "" : "";
            return new SearchField { Column = r[0]!, Type = type, Op = SearchField.IsTextual(type) ? "LIKE %...%" : "=" };
        }).ToList();
        Raise();
    }

    /// <summary>Jalankan form "Cari": hasilnya tampil di Telusuri dengan paging.</summary>
    public Task SubmitSearch()
    {
        QuickSearch = "";
        return ApplyFilters(SearchForm.Where(f => f.IsActive).Select(f => new DbFilter(f.Column, f.Op, f.Value.Trim())).ToArray(), false);
    }

    /// <summary>Kotak "Cari di tabel ini": cocokkan teks di semua kolom.</summary>
    public async Task SubmitQuickSearch()
    {
        var text = QuickSearch.Trim();
        if (text.Length == 0) { await ClearFilters(); return; }
        var cols = Page?.Columns ?? [];
        if (cols.Length == 0 && SelectedTable is { } t)
        {
            await LoadStructure(t);
            cols = Structure?.Rows.Where(r => r.Length > 0 && r[0] != null).Select(r => r[0]!).ToArray() ?? [];
        }
        await ApplyFilters(cols.Select(c => new DbFilter(c, "LIKE %...%", text)).ToArray(), true);
    }

    public Task ClearFilters()
    {
        QuickSearch = "";
        foreach (var f in SearchForm) f.Value = "";
        return ApplyFilters([], false);
    }

    private async Task ApplyFilters(DbFilter[] f, bool any)
    {
        if (SelectedTable is not { } t) return;
        Filters = f;
        FilterAny = any;
        Offset = 0;
        CurrentTab = Tab.Browse;
        TotalRows = f.Length == 0 ? t.Rows : null;
        TotalIsEstimate = f.Length == 0 && t.Rows != null;
        Raise();
        await LoadPage(t);
        if (_session == null) return;
        try
        {
            var n = await _session.CountSearch(t.Schema, t.Name, f, any);
            if (SelectedTable?.Name == t.Name) { TotalRows = n; TotalIsEstimate = false; Raise(); }
        }
        catch { }
    }

    /// <summary>Ringkasan syarat aktif, mis. <c>kota = Medan · nama berisi "di"</c>.</summary>
    public string FilterSummary => FilterAny
        ? $"“{Filters.FirstOrDefault()?.Value}” di semua kolom"
        : string.Join(" · ", Filters.Select(f => f.Op switch
        {
            "IS NULL" or "IS NOT NULL" or "= ''" or "!= ''" => $"{f.Column} {f.Op}",
            "LIKE %...%" => $"{f.Column} berisi “{f.Value}”",
            "NOT LIKE %...%" => $"{f.Column} tidak berisi “{f.Value}”",
            _ => $"{f.Column} {f.Op} {f.Value}",
        }));

    public async Task LoadPage(DbTable t)
    {
        if (_session == null) return;
        Running = true; Error = null; Raise();
        try
        {
            Page = Filters.Length == 0
                ? await _session.Page(t.Schema, t.Name, PageSize, Offset)
                : await _session.Search(t.Schema, t.Name, Filters, FilterAny, PageSize, Offset);
        }
        catch (Exception e) { Error = Errors.Text(e); }
        Running = false; Raise();
    }

    public async Task LoadStructure(DbTable t)
    {
        if (_session == null) return;
        Running = true; Error = null; Raise();
        try { Structure = await _session.Columns(t.Schema, t.Name); }
        catch (Exception e) { Error = Errors.Text(e); }
        Running = false; Raise();
    }

    public async Task GoToPage(ulong offset)
    {
        if (SelectedTable is not { } t) return;
        Offset = offset;
        await LoadPage(t);
    }

    public async Task SetPageSize(uint n)
    {
        PageSize = n;
        Offset = 0;
        if (SelectedTable is { } t) await LoadPage(t);
    }

    public async Task Run(string? sql = null)
    {
        var text = (sql ?? Query).Trim();
        if (_session == null || text.Length == 0 || Running) return;
        Running = true; Error = null; Raise();
        try
        {
            Result = await _session.Run(text);
            if (History.FirstOrDefault() != text) { History.Insert(0, text); if (History.Count > 30) History.RemoveRange(30, History.Count - 30); }
            var m = Regex.Match(text, @"^\s*use\s+[`\[]?([^`\]\s;]+)", RegexOptions.IgnoreCase);
            if (m.Success) { ActiveDatabase = m.Groups[1].Value; SelectedDb = m.Groups[1].Value; }
            if (Regex.IsMatch(text, @"^\s*(create|drop|alter|rename|truncate)", RegexOptions.IgnoreCase))
            {
                Running = false;
                await Refresh();
                Structure = null;
            }
            else if (Regex.IsMatch(text, @"^\s*(insert|update|delete|replace)", RegexOptions.IgnoreCase)) Page = null;
        }
        catch (Exception e) { Error = Errors.Text(e); }
        Running = false;
        Raise();
    }

    public QueryResult? ShownResult => CurrentTab switch { Tab.Browse => Page, Tab.Structure => Structure, Tab.Sql => Result, _ => null };

    public static string ToCsv(QueryResult r)
    {
        static string Esc(string? v) => v == null ? "" : v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", r.Columns.Select(Esc)));
        foreach (var row in r.Rows) sb.AppendLine(string.Join(",", row.Select(Esc)));
        return sb.ToString();
    }
}
