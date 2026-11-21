//! Klien database: PostgreSQL, MySQL/MariaDB, SQL Server, SQLite.
//! Bisa langsung, atau lewat tunnel SSH (port lokal sementara yang diteruskan via russh).
//!
//! Semua nilai dikembalikan sebagai teks agar mudah ditampilkan di tabel hasil.

use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::net::{TcpListener, TcpStream};
use tokio::sync::Mutex;
use tokio::task::JoinHandle;
use tokio_util::compat::{Compat, TokioAsyncWriteCompatExt};

use crate::model::{DbConnection, DbEngine};
use crate::ssh::{self, SshTarget};
use crate::CoreError;

/// Batas baris yang dikembalikan per query (sisanya dipotong).
const MAX_ROWS: usize = 5000;

#[derive(Debug, Clone, uniffi::Record)]
pub struct QueryResult {
    pub columns: Vec<String>,
    pub rows: Vec<Vec<Option<String>>>,
    /// Baris terpengaruh (INSERT/UPDATE/DELETE) jika diketahui.
    pub affected: Option<u64>,
    pub elapsed_ms: u64,
    /// `true` jika hasil dipotong di MAX_ROWS.
    pub truncated: bool,
}

/// Satu syarat pencarian (tab "Cari" ala phpMyAdmin).
///
/// `op`: `=`, `!=`, `>`, `>=`, `<`, `<=`, `LIKE`, `LIKE %...%`, `NOT LIKE`, `NOT LIKE %...%`,
/// `IN (...)`, `NOT IN (...)`, `BETWEEN`, `NOT BETWEEN`, `IS NULL`, `IS NOT NULL`, `= ''`, `!= ''`.
/// Syarat dengan nilai kosong diabaikan (kecuali operator tanpa nilai).
#[derive(Debug, Clone, uniffi::Record)]
pub struct DbFilter {
    pub column: String,
    pub op: String,
    pub value: String,
}

#[derive(Debug, Clone, uniffi::Record)]
pub struct DbTable {
    pub schema: String,
    pub name: String,
    pub is_view: bool,
    /// Perkiraan jumlah baris (statistik server), jika tersedia.
    pub rows: Option<u64>,
    /// Ukuran data + index dalam byte, jika tersedia.
    pub size_bytes: Option<u64>,
}

fn db_err(e: impl std::fmt::Display) -> CoreError {
    CoreError::Db { msg: e.to_string() }
}

enum Conn {
    Pg(tokio_postgres::Client),
    My(mysql_async::Conn),
    Ms(tiberius::Client<Compat<TcpStream>>),
    Lite(Arc<std::sync::Mutex<rusqlite::Connection>>),
    Redis(redis::aio::MultiplexedConnection),
    Mongo(mongodb::Client, mongodb::Database),
    Ch(ClickHouse),
}

struct ClickHouse {
    http: reqwest::Client,
    base: String,
    user: String,
    password: String,
    database: String,
}

/// Tunnel SSH: listener lokal yang meneruskan setiap koneksi ke host:port tujuan di sisi server.
struct Tunnel {
    _handle: Arc<russh::client::Handle<ssh::Client>>,
    task: JoinHandle<()>,
    port: u16,
}

impl Drop for Tunnel {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn open_tunnel(target: &SshTarget, dest_host: String, dest_port: u16) -> Result<Tunnel, CoreError> {
    let handle = Arc::new(ssh::connect(target).await?);
    let listener = TcpListener::bind("127.0.0.1:0").await?;
    let port = listener.local_addr()?.port();
    let h = handle.clone();
    let task = tokio::spawn(async move {
        while let Ok((mut tcp, peer)) = listener.accept().await {
            let h = h.clone();
            let dest = dest_host.clone();
            tokio::spawn(async move {
                if let Ok(ch) = h.channel_open_direct_tcpip(dest, dest_port as u32, peer.ip().to_string(), peer.port() as u32).await {
                    let mut stream = ch.into_stream();
                    let _ = tokio::io::copy_bidirectional(&mut tcp, &mut stream).await;
                }
            });
        }
    });
    Ok(Tunnel { _handle: handle, task, port })
}

#[derive(uniffi::Object)]
pub struct DbSession {
    engine: DbEngine,
    conn: Mutex<Conn>,
    _tunnel: Option<Tunnel>,
}

#[uniffi::export(async_runtime = "tokio")]
impl DbSession {
    /// Sambungkan ke database. `ssh` diisi jika koneksi lewat tunnel SSH.
    #[uniffi::constructor]
    pub async fn connect(conn: DbConnection, ssh: Option<SshTarget>) -> Result<Arc<Self>, CoreError> {
        let tunnel = match (&ssh, conn.engine) {
            (Some(t), e) if e != DbEngine::Sqlite => Some(open_tunnel(t, conn.host.clone(), conn.port).await?),
            _ => None,
        };
        let (host, port) = match &tunnel {
            Some(t) => ("127.0.0.1".to_string(), t.port),
            None => (conn.host.clone(), conn.port),
        };
        let fut = connect_engine(&conn, host, port);
        let inner = tokio::time::timeout(Duration::from_secs(25), fut)
            .await
            .map_err(|_| db_err("waktu koneksi habis (25 detik)"))??;
        Ok(Arc::new(Self { engine: conn.engine, conn: Mutex::new(inner), _tunnel: tunnel }))
    }

    /// Jalankan SQL; mengembalikan hasil dari statement terakhir yang mengembalikan baris.
    pub async fn run(&self, sql: String) -> Result<QueryResult, CoreError> {
        let start = Instant::now();
        let mut conn = self.conn.lock().await;
        let mut r = match &mut *conn {
            Conn::Pg(c) => run_pg(c, &sql).await?,
            Conn::My(c) => run_my(c, &sql).await?,
            Conn::Ms(c) => run_ms(c, &sql).await?,
            Conn::Lite(c) => {
                let c = c.clone();
                tokio::task::spawn_blocking(move || run_lite(&c.lock().unwrap(), &sql)).await.map_err(db_err)??
            }
            Conn::Redis(c) => run_redis(c, &sql).await?,
            Conn::Mongo(_, db) => run_mongo(db, &sql).await?,
            Conn::Ch(c) => run_ch(c, &sql).await?,
        };
        r.elapsed_ms = start.elapsed().as_millis() as u64;
        Ok(r)
    }

    /// Daftar database di server (SQLite: "main").
    pub async fn databases(&self) -> Result<Vec<String>, CoreError> {
        let sql = match self.engine {
            DbEngine::Postgres => "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY 1",
            DbEngine::Mysql | DbEngine::Mariadb => "SHOW DATABASES",
            DbEngine::Sqlserver => "SELECT name FROM sys.databases ORDER BY name",
            DbEngine::Sqlite => return Ok(vec!["main".into()]),
            DbEngine::Redis => return Ok((0..16).map(|i| i.to_string()).collect()),
            DbEngine::Mongodb => {
                if let Conn::Mongo(c, _) = &*self.conn.lock().await {
                    return c.list_database_names().await.map_err(db_err);
                }
                return Ok(vec![]);
            }
            DbEngine::Clickhouse => "SHOW DATABASES",
        };
        let r = self.run(sql.into()).await?;
        Ok(r.rows.into_iter().filter_map(|row| row.into_iter().next().flatten()).collect())
    }

    /// Tabel & view di database aktif.
    pub async fn tables(&self) -> Result<Vec<DbTable>, CoreError> {
        self.list_tables(None).await
    }

    /// Tabel & view di database tertentu (untuk pohon database ala phpMyAdmin).
    /// PostgreSQL tidak bisa lintas database: selalu database yang sedang tersambung.
    pub async fn tables_in(&self, database: String) -> Result<Vec<DbTable>, CoreError> {
        self.list_tables(Some(database)).await
    }

    /// Pindah database aktif di sesi yang sama. `false` = engine butuh sambung ulang (PostgreSQL).
    pub async fn use_database(&self, name: String) -> Result<bool, CoreError> {
        match self.engine {
            DbEngine::Mysql | DbEngine::Mariadb => {
                self.run(format!("USE {}", self.ident(&name))).await?;
            }
            DbEngine::Sqlserver => {
                self.run(format!("USE {}", self.ident(&name))).await?;
            }
            DbEngine::Redis => {
                let n: u32 = name.parse().map_err(|_| db_err("nomor database Redis tidak valid"))?;
                self.run(format!("SELECT {n}")).await?;
            }
            DbEngine::Clickhouse => {
                if let Conn::Ch(ch) = &mut *self.conn.lock().await { ch.database = name; }
            }
            DbEngine::Mongodb => {
                if let Conn::Mongo(client, db) = &mut *self.conn.lock().await { *db = client.database(&name); }
            }
            DbEngine::Postgres => return Ok(false),
            DbEngine::Sqlite => {}
        }
        Ok(true)
    }

    /// Struktur tabel: kolom, tipe, null, kunci, default, ekstra, komentar.
    pub async fn columns(&self, schema: String, table: String) -> Result<QueryResult, CoreError> {
        let (s, t) = (self.lit(&schema), self.lit(&table));
        let sql = match self.engine {
            DbEngine::Mysql | DbEngine::Mariadb => format!(
                "SELECT COLUMN_NAME AS `Kolom`, COLUMN_TYPE AS `Tipe`, IS_NULLABLE AS `Null`, COLUMN_KEY AS `Kunci`, \
                 NULLIF(COLUMN_DEFAULT, 'NULL') AS `Default`, EXTRA AS `Ekstra`, COLLATION_NAME AS `Collation`, COLUMN_COMMENT AS `Komentar` \
                 FROM information_schema.columns WHERE table_schema = {s} AND table_name = {t} ORDER BY ORDINAL_POSITION"),
            DbEngine::Postgres => format!(
                "SELECT c.column_name AS \"Kolom\", \
                 CASE WHEN c.character_maximum_length IS NOT NULL THEN c.data_type || '(' || c.character_maximum_length || ')' ELSE c.data_type END AS \"Tipe\", \
                 c.is_nullable AS \"Null\", \
                 CASE WHEN EXISTS (SELECT 1 FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage k \
                   ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema \
                   WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name \
                   AND k.column_name = c.column_name) THEN 'PRI' ELSE '' END AS \"Kunci\", \
                 c.column_default AS \"Default\", \
                 col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position::int) AS \"Komentar\" \
                 FROM information_schema.columns c WHERE c.table_schema = {s} AND c.table_name = {t} ORDER BY c.ordinal_position"),
            DbEngine::Sqlserver => format!(
                "SELECT COLUMN_NAME AS [Kolom], DATA_TYPE + COALESCE('(' + CAST(NULLIF(CHARACTER_MAXIMUM_LENGTH, -1) AS varchar) + ')', '') AS [Tipe], \
                 IS_NULLABLE AS [Null], COLUMN_DEFAULT AS [Default] \
                 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = {s} AND TABLE_NAME = {t} ORDER BY ORDINAL_POSITION"),
            DbEngine::Sqlite => format!("SELECT name AS Kolom, type AS Tipe, CASE \"notnull\" WHEN 1 THEN 'NO' ELSE 'YES' END AS \"Null\", \
                 CASE WHEN pk > 0 THEN 'PRI' ELSE '' END AS Kunci, dflt_value AS \"Default\" FROM pragma_table_info({t})"),
            DbEngine::Clickhouse => format!(
                "SELECT name AS Kolom, type AS Tipe, default_expression AS `Default`, comment AS Komentar \
                 FROM system.columns WHERE database = {s} AND table = {t} ORDER BY position"),
            DbEngine::Redis | DbEngine::Mongodb => return Ok(empty()),
        };
        self.run(sql).await
    }

    /// Jumlah baris persis (SELECT COUNT(*)).
    pub async fn count_rows(&self, schema: String, table: String) -> Result<u64, CoreError> {
        if !self.engine_is_sql() { return Ok(0); }
        let r = self.run(format!("SELECT COUNT(*) FROM {}", self.qualified(&schema, &table))).await?;
        Ok(r.rows.first().and_then(|row| row.first().cloned().flatten()).and_then(|v| v.parse().ok()).unwrap_or(0))
    }

    /// Query untuk melihat isi tabel (dengan kutip identifier sesuai engine).
    pub fn preview_sql(&self, schema: String, table: String, limit: u32) -> String {
        self.page_sql(schema, table, limit, 0)
    }

    /// Query halaman data tabel (LIMIT/OFFSET sesuai engine).
    pub fn page_sql(&self, schema: String, table: String, limit: u32, offset: u64) -> String {
        self.search_sql(schema, table, vec![], false, limit, offset)
    }

    /// Query halaman data dengan syarat pencarian. `any`: gabung syarat dengan OR (cari di semua kolom),
    /// selain itu AND (form "Cari" per kolom).
    pub fn search_sql(&self, schema: String, table: String, filters: Vec<DbFilter>, any: bool, limit: u32, offset: u64) -> String {
        let q = self.qualified(&schema, &table);
        let w = self.where_sql(&filters, any);
        match self.engine {
            DbEngine::Sqlserver if offset == 0 => format!("SELECT TOP {limit} * FROM {q}{w}"),
            DbEngine::Sqlserver => format!("SELECT * FROM {q}{w} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"),
            DbEngine::Redis => format!("TYPE {}", quote_arg(&table)),
            DbEngine::Mongodb => format!("db.{table}.find({{}})"),
            _ if offset == 0 => format!("SELECT * FROM {q}{w} LIMIT {limit}"),
            _ => format!("SELECT * FROM {q}{w} LIMIT {limit} OFFSET {offset}"),
        }
    }

    /// Satu halaman hasil pencarian.
    pub async fn search(&self, schema: String, table: String, filters: Vec<DbFilter>, any: bool, limit: u32, offset: u64) -> Result<QueryResult, CoreError> {
        if !self.engine_is_sql() {
            return Err(db_err("pencarian per kolom hanya untuk database SQL"));
        }
        self.run(self.search_sql(schema, table, filters, any, limit, offset)).await
    }

    /// Jumlah baris yang cocok dengan pencarian.
    pub async fn count_search(&self, schema: String, table: String, filters: Vec<DbFilter>, any: bool) -> Result<u64, CoreError> {
        if !self.engine_is_sql() { return Ok(0); }
        let sql = format!("SELECT COUNT(*) FROM {}{}", self.qualified(&schema, &table), self.where_sql(&filters, any));
        let r = self.run(sql).await?;
        Ok(r.rows.first().and_then(|row| row.first().cloned().flatten()).and_then(|v| v.parse().ok()).unwrap_or(0))
    }

    /// Lihat isi tabel / key / koleksi. Redis memilih perintah sesuai tipe key.
    pub async fn preview(&self, schema: String, table: String) -> Result<QueryResult, CoreError> {
        if self.engine == DbEngine::Redis {
            let t = self.run(format!("TYPE {}", quote_arg(&table))).await?;
            let kind = t.rows.first().and_then(|r| r.get(1).cloned().flatten()).unwrap_or_default();
            let k = quote_arg(&table);
            let cmd = match kind.as_str() {
                "hash" => format!("HGETALL {k}"),
                "list" => format!("LRANGE {k} 0 499"),
                "set" => format!("SMEMBERS {k}"),
                "zset" => format!("ZRANGE {k} 0 499 WITHSCORES"),
                "stream" => format!("XRANGE {k} - + COUNT 200"),
                _ => format!("GET {k}"),
            };
            return self.run(cmd).await;
        }
        self.run(self.preview_sql(schema, table, 200)).await
    }

    /// Satu halaman data tabel.
    pub async fn page(&self, schema: String, table: String, limit: u32, offset: u64) -> Result<QueryResult, CoreError> {
        if !self.engine_is_sql() { return self.preview(schema, table).await; }
        self.run(self.page_sql(schema, table, limit, offset)).await
    }
}

impl DbSession {
    fn engine_is_sql(&self) -> bool {
        !matches!(self.engine, DbEngine::Redis | DbEngine::Mongodb)
    }

    /// Kutip identifier sesuai engine.
    fn ident(&self, s: &str) -> String {
        match self.engine {
            DbEngine::Mysql | DbEngine::Mariadb => format!("`{}`", s.replace('`', "``")),
            DbEngine::Clickhouse => format!("`{}`", s.replace('\\', "\\\\").replace('`', "\\`")),
            DbEngine::Sqlserver => format!("[{}]", s.replace(']', "]]")),
            _ => format!("\"{}\"", s.replace('"', "\"\"")),
        }
    }

    /// Klausa ` WHERE ...` dari syarat pencarian (kosong jika tidak ada syarat yang berlaku).
    fn where_sql(&self, filters: &[DbFilter], any: bool) -> String {
        let parts: Vec<String> = filters.iter().filter_map(|f| self.condition(f)).collect();
        if parts.is_empty() {
            return String::new();
        }
        format!(" WHERE {}", parts.iter().map(|p| format!("({p})")).collect::<Vec<_>>().join(if any { " OR " } else { " AND " }))
    }

    fn condition(&self, f: &DbFilter) -> Option<String> {
        let c = self.ident(&f.column);
        // LIKE pada kolom angka/tanggal: ubah ke teks dulu di engine yang ketat tipe
        let text = match self.engine {
            DbEngine::Postgres => format!("CAST({c} AS TEXT)"),
            DbEngine::Sqlserver => format!("CAST({c} AS NVARCHAR(MAX))"),
            DbEngine::Clickhouse => format!("toString({c})"),
            _ => c.clone(),
        };
        let v = f.value.as_str();
        let list = || v.split(',').map(|x| self.lit(x.trim())).collect::<Vec<_>>().join(", ");
        let pair = || {
            let (a, b) = v.split_once(',').or_else(|| v.to_ascii_uppercase().find(" AND ").map(|i| (&v[..i], &v[i + 5..])))?;
            Some((self.lit(a.trim()), self.lit(b.trim())))
        };
        Some(match f.op.as_str() {
            "IS NULL" => format!("{c} IS NULL"),
            "IS NOT NULL" => format!("{c} IS NOT NULL"),
            "= ''" => format!("{c} = ''"),
            "!= ''" => format!("{c} <> ''"),
            _ if v.is_empty() => return None,
            // "%...%" = cari teks: tanpa beda huruf besar/kecil di semua engine
            "LIKE %...%" => format!("{text} {} {}", self.ilike(), self.lit(&format!("%{v}%"))),
            "NOT LIKE %...%" => format!("{text} NOT {} {}", self.ilike(), self.lit(&format!("%{v}%"))),
            "LIKE" => format!("{text} LIKE {}", self.lit(v)),
            "NOT LIKE" => format!("{text} NOT LIKE {}", self.lit(v)),
            "IN (...)" => format!("{c} IN ({})", list()),
            "NOT IN (...)" => format!("{c} NOT IN ({})", list()),
            "BETWEEN" => { let (a, b) = pair()?; format!("{c} BETWEEN {a} AND {b}") }
            "NOT BETWEEN" => { let (a, b) = pair()?; format!("{c} NOT BETWEEN {a} AND {b}") }
            "!=" => format!("{c} <> {}", self.lit(v)),
            op @ ("=" | ">" | ">=" | "<" | "<=") => format!("{c} {op} {}", self.lit(v)),
            _ => return None,
        })
    }

    /// LIKE yang tidak membedakan huruf besar/kecil (MySQL/SQLite/SQL Server sudah begitu secara bawaan).
    fn ilike(&self) -> &'static str {
        match self.engine {
            DbEngine::Postgres | DbEngine::Clickhouse => "ILIKE",
            _ => "LIKE",
        }
    }

    /// Literal string SQL yang aman.
    fn lit(&self, s: &str) -> String {
        match self.engine {
            DbEngine::Mysql | DbEngine::Mariadb | DbEngine::Clickhouse => format!("'{}'", s.replace('\\', "\\\\").replace('\'', "''")),
            _ => format!("'{}'", s.replace('\'', "''")),
        }
    }

    fn qualified(&self, schema: &str, table: &str) -> String {
        match self.engine {
            DbEngine::Sqlite => self.ident(table),
            _ if schema.is_empty() => self.ident(table),
            _ => format!("{}.{}", self.ident(schema), self.ident(table)),
        }
    }

    /// `database`: None = database aktif.
    async fn list_tables(&self, database: Option<String>) -> Result<Vec<DbTable>, CoreError> {
        let sql = match self.engine {
            DbEngine::Postgres => "SELECT n.nspname, c.relname, CASE WHEN c.relkind IN ('v','m') THEN 'VIEW' ELSE 'TABLE' END, \
                 GREATEST(c.reltuples, 0)::bigint, pg_total_relation_size(c.oid) \
                 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace \
                 WHERE c.relkind IN ('r','v','m','p','f') AND n.nspname NOT IN ('pg_catalog','information_schema') \
                 AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%' ORDER BY 1, 2".to_string(),
            DbEngine::Mysql | DbEngine::Mariadb => format!(
                "SELECT table_schema, table_name, table_type, table_rows, data_length + index_length \
                 FROM information_schema.tables WHERE table_schema = {} ORDER BY table_name",
                database.as_deref().map(|d| self.lit(d)).unwrap_or_else(|| "DATABASE()".into())),
            DbEngine::Sqlserver => {
                let p = database.as_deref().map(|d| format!("{}.", self.ident(d))).unwrap_or_default();
                format!("SELECT s.name, o.name, o.type_desc, SUM(p.rows), NULL \
                     FROM {p}sys.objects o JOIN {p}sys.schemas s ON s.schema_id = o.schema_id \
                     LEFT JOIN {p}sys.partitions p ON p.object_id = o.object_id AND p.index_id IN (0, 1) \
                     WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 GROUP BY s.name, o.name, o.type_desc ORDER BY 1, 2")
            }
            DbEngine::Sqlite => "SELECT 'main', name, type, NULL, NULL FROM sqlite_master WHERE type IN ('table','view') \
                 AND name NOT LIKE 'sqlite_%' ORDER BY 2".to_string(),
            DbEngine::Clickhouse => format!(
                "SELECT database, name, engine, total_rows, total_bytes FROM system.tables WHERE database = {} ORDER BY name",
                database.as_deref().map(|d| self.lit(d)).unwrap_or_else(|| "currentDatabase()".into())),
            DbEngine::Redis => {
                // Ambil key lewat SCAN (maks 1000) tanpa memblokir server seperti KEYS *
                let mut guard = self.conn.lock().await;
                let Conn::Redis(c) = &mut *guard else { return Ok(vec![]) };
                let mut keys: Vec<String> = vec![];
                let mut cursor: u64 = 0;
                loop {
                    let (next, batch): (u64, Vec<String>) = redis::cmd("SCAN").arg(cursor).arg("COUNT").arg(500)
                        .query_async(c).await.map_err(db_err)?;
                    keys.extend(batch);
                    cursor = next;
                    if cursor == 0 || keys.len() >= 1000 { break; }
                }
                keys.sort();
                keys.dedup();
                return Ok(keys.into_iter().map(|k| DbTable { schema: "key".into(), name: k, is_view: false, rows: None, size_bytes: None }).collect());
            }
            DbEngine::Mongodb => {
                if let Conn::Mongo(client, db) = &*self.conn.lock().await {
                    let db = match &database { Some(d) => client.database(d), None => db.clone() };
                    let mut names = db.list_collection_names().await.map_err(db_err)?;
                    names.sort();
                    return Ok(names.into_iter().map(|n| DbTable { schema: db.name().to_string(), name: n, is_view: false, rows: None, size_bytes: None }).collect());
                }
                return Ok(vec![]);
            }
        };
        let r = self.run(sql).await?;
        Ok(r.rows
            .into_iter()
            .map(|row| {
                let get = |i: usize| row.get(i).cloned().flatten();
                let num = |i: usize| get(i).and_then(|v| v.parse::<f64>().ok()).map(|v| v.max(0.0) as u64);
                DbTable {
                    schema: get(0).unwrap_or_default(),
                    name: get(1).unwrap_or_default(),
                    is_view: get(2).unwrap_or_default().to_lowercase().contains("view"),
                    rows: num(3),
                    size_bytes: num(4),
                }
            })
            .collect())
    }
}

async fn connect_engine(c: &DbConnection, host: String, port: u16) -> Result<Conn, CoreError> {
    match c.engine {
        DbEngine::Postgres => {
            let mut cfg = tokio_postgres::Config::new();
            cfg.host(&host).port(port).user(&c.username).connect_timeout(Duration::from_secs(20));
            if let Some(pw) = c.password.as_deref() { cfg.password(pw); }
            cfg.dbname(if c.database.is_empty() { "postgres" } else { &c.database });
            let tls = native_tls::TlsConnector::builder().danger_accept_invalid_certs(true).build().map_err(db_err)?;
            let tls = postgres_native_tls::MakeTlsConnector::new(tls);
            // TLS dulu; jika tidak diwajibkan, jatuh ke koneksi biasa bila server tidak mendukung TLS
            match cfg.connect(tls).await {
                Ok((client, connection)) => {
                    tokio::spawn(connection);
                    Ok(Conn::Pg(client))
                }
                Err(e) if !c.tls => {
                    let (client, connection) = cfg.connect(tokio_postgres::NoTls).await.map_err(|e2| db_err(format!("{e2} (TLS: {e})")))?;
                    tokio::spawn(connection);
                    Ok(Conn::Pg(client))
                }
                Err(e) => Err(db_err(e)),
            }
        }
        DbEngine::Mysql | DbEngine::Mariadb => {
            let mut opts = mysql_async::OptsBuilder::default()
                .ip_or_hostname(host)
                .tcp_port(port)
                .user(Some(c.username.clone()))
                .pass(c.password.clone())
                .db_name(if c.database.is_empty() { None } else { Some(c.database.clone()) });
            if c.tls {
                opts = opts.ssl_opts(Some(mysql_async::SslOpts::default().with_danger_accept_invalid_certs(true)));
            }
            Ok(Conn::My(mysql_async::Conn::new(opts).await.map_err(db_err)?))
        }
        DbEngine::Sqlserver => {
            let mut cfg = tiberius::Config::new();
            cfg.host(&host);
            cfg.port(port);
            cfg.authentication(tiberius::AuthMethod::sql_server(&c.username, c.password.as_deref().unwrap_or("")));
            if !c.database.is_empty() { cfg.database(&c.database); }
            cfg.trust_cert();
            if !c.tls { cfg.encryption(tiberius::EncryptionLevel::Off); }
            let tcp = TcpStream::connect(cfg.get_addr()).await?;
            tcp.set_nodelay(true)?;
            Ok(Conn::Ms(tiberius::Client::connect(cfg, tcp.compat_write()).await.map_err(db_err)?))
        }
        DbEngine::Sqlite => {
            let conn = rusqlite::Connection::open(&c.file_path).map_err(db_err)?;
            Ok(Conn::Lite(Arc::new(std::sync::Mutex::new(conn))))
        }
        DbEngine::Redis => {
            let auth = match (c.username.is_empty(), c.password.as_deref()) {
                (_, None) => String::new(),
                (true, Some(pw)) => format!(":{}@", enc(pw)),
                (false, Some(pw)) => format!("{}:{}@", enc(&c.username), enc(pw)),
            };
            let db = c.database.parse::<u32>().unwrap_or(0);
            let url = format!("{}://{auth}{host}:{port}/{db}{}", if c.tls { "rediss" } else { "redis" }, if c.tls { "#insecure" } else { "" });
            let client = redis::Client::open(url).map_err(db_err)?;
            Ok(Conn::Redis(client.get_multiplexed_async_connection().await.map_err(db_err)?))
        }
        DbEngine::Mongodb => {
            // Host boleh berisi URI lengkap (mongodb:// atau mongodb+srv://) untuk Atlas
            let uri = if c.host.starts_with("mongodb") {
                c.host.clone()
            } else {
                let auth = match c.password.as_deref() {
                    Some(pw) if !c.username.is_empty() => format!("{}:{}@", enc(&c.username), enc(pw)),
                    _ => String::new(),
                };
                format!("mongodb://{auth}{host}:{port}/?directConnection=true{}", if c.tls { "&tls=true&tlsAllowInvalidCertificates=true" } else { "" })
            };
            let client = mongodb::Client::with_uri_str(&uri).await.map_err(db_err)?;
            let db = client.database(if c.database.is_empty() { "admin" } else { &c.database });
            db.run_command(mongodb::bson::doc! { "ping": 1 }).await.map_err(db_err)?;
            Ok(Conn::Mongo(client, db))
        }
        DbEngine::Clickhouse => {
            let http = reqwest::Client::builder()
                .danger_accept_invalid_certs(true)
                .timeout(Duration::from_secs(60))
                .build()
                .map_err(db_err)?;
            let ch = ClickHouse {
                http,
                base: format!("{}://{host}:{port}/", if c.tls { "https" } else { "http" }),
                user: if c.username.is_empty() { "default".into() } else { c.username.clone() },
                password: c.password.clone().unwrap_or_default(),
                database: if c.database.is_empty() { "default".into() } else { c.database.clone() },
            };
            run_ch(&ch, "SELECT 1").await?;
            Ok(Conn::Ch(ch))
        }
    }
}

/// Percent-encode untuk user/password di URL.
fn enc(s: &str) -> String {
    s.bytes()
        .map(|b| if b.is_ascii_alphanumeric() || b"-._~".contains(&b) { (b as char).to_string() } else { format!("%{b:02X}") })
        .collect()
}

/// Kutip argumen Redis bila perlu.
fn quote_arg(s: &str) -> String {
    if s.chars().any(|c| c.is_whitespace() || c == '"') { format!("\"{}\"", s.replace('"', "\\\"")) } else { s.to_string() }
}

/// Pecah baris perintah Redis menjadi argumen (mendukung "kutip" dan 'kutip').
fn split_args(line: &str) -> Vec<String> {
    let mut out = vec![];
    let mut cur = String::new();
    let mut quote: Option<char> = None;
    let mut has = false;
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        match (quote, ch) {
            (Some(q), c) if c == q => quote = None,
            (Some(_), '\\') => { if let Some(n) = chars.next() { cur.push(n); } }
            (Some(_), c) => cur.push(c),
            (None, '"') | (None, '\'') => { quote = Some(ch); has = true; }
            (None, c) if c.is_whitespace() => { if has || !cur.is_empty() { out.push(std::mem::take(&mut cur)); has = false; } }
            (None, c) => cur.push(c),
        }
    }
    if has || !cur.is_empty() { out.push(cur); }
    out
}

async fn run_redis(c: &mut redis::aio::MultiplexedConnection, line: &str) -> Result<QueryResult, CoreError> {
    let mut out = empty();
    // Boleh beberapa perintah, satu per baris; hasil dari perintah terakhir
    let lines: Vec<&str> = line.lines().map(str::trim).filter(|l| !l.is_empty() && !l.starts_with('#')).collect();
    let mut last = redis::Value::Nil;
    for l in lines {
        let args = split_args(l);
        let Some((name, rest)) = args.split_first() else { continue };
        let mut cmd = redis::cmd(name);
        for a in rest { cmd.arg(a); }
        last = cmd.query_async(c).await.map_err(db_err)?;
    }
    out.columns = vec!["#".into(), "value".into()];
    match last {
        redis::Value::Array(items) | redis::Value::Set(items) => {
            out.rows = items.into_iter().enumerate().map(|(i, v)| vec![Some(i.to_string()), redis_str(v)]).collect();
        }
        redis::Value::Map(pairs) => {
            out.columns = vec!["field".into(), "value".into()];
            out.rows = pairs.into_iter().map(|(k, v)| vec![redis_str(k), redis_str(v)]).collect();
        }
        v => out.rows = vec![vec![Some("0".into()), redis_str(v)]],
    }
    // HGETALL di RESP2 berupa array berpasangan → tampilkan sebagai field/value
    Ok(out)
}

fn redis_str(v: redis::Value) -> Option<String> {
    use redis::Value as V;
    match v {
        V::Nil => None,
        V::Int(i) => Some(i.to_string()),
        V::BulkString(b) => Some(String::from_utf8_lossy(&b).into_owned()),
        V::SimpleString(s) => Some(s),
        V::Okay => Some("OK".into()),
        V::Double(d) => Some(d.to_string()),
        V::Boolean(b) => Some(b.to_string()),
        V::VerbatimString { text, .. } => Some(text),
        V::Array(items) | V::Set(items) => Some(format!("[{}]", items.into_iter().map(|i| redis_str(i).unwrap_or("nil".into())).collect::<Vec<_>>().join(", "))),
        other => Some(format!("{other:?}")),
    }
}

async fn run_mongo(db: &mongodb::Database, input: &str) -> Result<QueryResult, CoreError> {
    use mongodb::bson::{doc, Bson, Document};
    let text = input.trim().trim_end_matches(';');
    let parse = |s: &str| -> Result<Document, CoreError> {
        let s = s.trim();
        if s.is_empty() { return Ok(Document::new()); }
        let v: serde_json::Value = serde_json::from_str(s).map_err(|e| db_err(format!("JSON tidak valid: {e}")))?;
        match Bson::try_from(v).map_err(db_err)? {
            Bson::Document(d) => Ok(d),
            _ => Err(db_err("harus berupa objek JSON")),
        }
    };
    let docs: Vec<Document> = if let Some(rest) = text.strip_prefix("db.") {
        // db.<koleksi>.<metode>(<argumen>)
        let (coll, call) = rest.split_once('.').ok_or_else(|| db_err("format: db.koleksi.find({...})"))?;
        let (method, args) = call.split_once('(').ok_or_else(|| db_err("format: db.koleksi.find({...})"))?;
        let args = args.trim_end().trim_end_matches(')');
        let c = db.collection::<Document>(coll);
        match method.trim() {
            "find" => {
                use futures_util::TryStreamExt;
                let cur = c.find(parse(args)?).limit(MAX_ROWS as i64).await.map_err(db_err)?;
                cur.try_collect().await.map_err(db_err)?
            }
            "findOne" => c.find_one(parse(args)?).await.map_err(db_err)?.into_iter().collect(),
            "countDocuments" | "count" => vec![doc! { "count": c.count_documents(parse(args)?).await.map_err(db_err)? as i64 }],
            "aggregate" => {
                use futures_util::TryStreamExt;
                let v: serde_json::Value = serde_json::from_str(args).map_err(|e| db_err(format!("JSON tidak valid: {e}")))?;
                let stages: Vec<Document> = match Bson::try_from(v).map_err(db_err)? {
                    Bson::Array(a) => a.into_iter().filter_map(|b| b.as_document().cloned()).collect(),
                    _ => return Err(db_err("aggregate butuh array pipeline")),
                };
                c.aggregate(stages).await.map_err(db_err)?.try_collect().await.map_err(db_err)?
            }
            "insertOne" => {
                let r = c.insert_one(parse(args)?).await.map_err(db_err)?;
                vec![doc! { "insertedId": r.inserted_id }]
            }
            "deleteMany" => vec![doc! { "deleted": c.delete_many(parse(args)?).await.map_err(db_err)?.deleted_count as i64 }],
            m => return Err(db_err(format!("metode '{m}' belum didukung — pakai find, findOne, aggregate, countDocuments, insertOne, deleteMany, atau perintah JSON"))),
        }
    } else if text.starts_with('{') {
        // Perintah database mentah, mis. {"listCollections": 1}
        let r = db.run_command(parse(text)?).await.map_err(db_err)?;
        match r.get_document("cursor").ok().and_then(|c| c.get_array("firstBatch").ok()) {
            Some(batch) => batch.iter().filter_map(|b| b.as_document().cloned()).collect(),
            None => vec![r],
        }
    } else {
        // Nama koleksi saja → 200 dokumen pertama
        use futures_util::TryStreamExt;
        db.collection::<Document>(text).find(doc! {}).limit(200).await.map_err(db_err)?.try_collect().await.map_err(db_err)?
    };

    let mut out = empty();
    for d in &docs {
        for k in d.keys() {
            if !out.columns.contains(k) { out.columns.push(k.clone()); }
        }
    }
    out.truncated = docs.len() >= MAX_ROWS;
    out.rows = docs
        .iter()
        .map(|d| out.columns.iter().map(|k| d.get(k).map(bson_str)).collect())
        .collect();
    Ok(out)
}

fn bson_str(b: &mongodb::bson::Bson) -> String {
    use mongodb::bson::Bson;
    match b {
        Bson::String(s) => s.clone(),
        Bson::ObjectId(o) => o.to_hex(),
        Bson::Int32(i) => i.to_string(),
        Bson::Int64(i) => i.to_string(),
        Bson::Double(d) => d.to_string(),
        Bson::Boolean(v) => v.to_string(),
        Bson::DateTime(d) => d.try_to_rfc3339_string().unwrap_or_else(|_| d.to_string()),
        Bson::Null => "null".into(),
        other => other.clone().into_relaxed_extjson().to_string(),
    }
}

async fn run_ch(c: &ClickHouse, sql: &str) -> Result<QueryResult, CoreError> {
    let resp = c
        .http
        .post(&c.base)
        .query(&[("database", c.database.as_str()), ("default_format", "TSVWithNames")])
        .header("X-ClickHouse-User", &c.user)
        .header("X-ClickHouse-Key", &c.password)
        .body(sql.to_string())
        .send()
        .await
        .map_err(db_err)?;
    let status = resp.status();
    let body = resp.text().await.map_err(db_err)?;
    if !status.is_success() {
        return Err(db_err(body.lines().next().unwrap_or("permintaan gagal").to_string()));
    }
    let mut out = empty();
    let mut lines = body.lines();
    if let Some(head) = lines.next() {
        out.columns = head.split('\t').map(tsv_unescape).collect();
        for l in lines {
            if out.rows.len() >= MAX_ROWS { out.truncated = true; break; }
            out.rows.push(l.split('\t').map(|v| if v == "\\N" { None } else { Some(tsv_unescape(v)) }).collect());
        }
    }
    Ok(out)
}

fn tsv_unescape(s: &str) -> String {
    s.replace("\\t", "\t").replace("\\n", "\n").replace("\\\\", "\\")
}

fn empty() -> QueryResult {
    QueryResult { columns: vec![], rows: vec![], affected: None, elapsed_ms: 0, truncated: false }
}

async fn run_pg(c: &tokio_postgres::Client, sql: &str) -> Result<QueryResult, CoreError> {
    use tokio_postgres::SimpleQueryMessage as M;
    let msgs = c.simple_query(sql).await.map_err(db_err)?;
    let mut out = empty();
    let mut current = empty();
    for m in msgs {
        match m {
            M::RowDescription(cols) => {
                current = empty();
                current.columns = cols.iter().map(|c| c.name().to_string()).collect();
            }
            M::Row(row) => {
                if current.columns.is_empty() {
                    current.columns = row.columns().iter().map(|c| c.name().to_string()).collect();
                }
                if current.rows.len() < MAX_ROWS {
                    current.rows.push((0..row.len()).map(|i| row.get(i).map(str::to_string)).collect());
                } else {
                    current.truncated = true;
                }
            }
            M::CommandComplete(n) => {
                if current.columns.is_empty() {
                    if out.columns.is_empty() { out.affected = Some(n); }
                } else {
                    out = std::mem::replace(&mut current, empty());
                }
            }
            _ => {}
        }
    }
    if !current.columns.is_empty() { out = current; }
    Ok(out)
}

async fn run_my(c: &mut mysql_async::Conn, sql: &str) -> Result<QueryResult, CoreError> {
    use mysql_async::prelude::*;
    let mut result = c.query_iter(sql).await.map_err(db_err)?;
    let mut out = empty();
    loop {
        let cols: Vec<String> = result.columns_ref().iter().map(|c| c.name_str().into_owned()).collect();
        let affected = result.affected_rows();
        let rows: Vec<mysql_async::Row> = result.collect().await.map_err(db_err)?;
        if !cols.is_empty() {
            out = empty();
            out.columns = cols;
            out.truncated = rows.len() > MAX_ROWS;
            out.rows = rows.into_iter().take(MAX_ROWS).map(|r| r.unwrap().into_iter().map(my_value).collect()).collect();
        } else if out.columns.is_empty() {
            out.affected = Some(affected);
        }
        if result.is_empty() { break; }
    }
    Ok(out)
}

fn my_value(v: mysql_async::Value) -> Option<String> {
    use mysql_async::Value as V;
    match v {
        V::NULL => None,
        V::Bytes(b) => Some(String::from_utf8_lossy(&b).into_owned()),
        V::Int(i) => Some(i.to_string()),
        V::UInt(u) => Some(u.to_string()),
        V::Float(f) => Some(f.to_string()),
        V::Double(d) => Some(d.to_string()),
        V::Date(y, mo, d, h, mi, s, us) => Some(if us > 0 {
            format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{s:02}.{us:06}")
        } else {
            format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{s:02}")
        }),
        V::Time(neg, d, h, mi, s, _) => Some(format!("{}{:02}:{mi:02}:{s:02}", if neg { "-" } else { "" }, d * 24 + h as u32)),
    }
}

async fn run_ms(c: &mut tiberius::Client<Compat<TcpStream>>, sql: &str) -> Result<QueryResult, CoreError> {
    let results = c.simple_query(sql).await.map_err(db_err)?.into_results().await.map_err(db_err)?;
    let mut out = empty();
    if let Some(rows) = results.into_iter().rev().find(|r| !r.is_empty()) {
        out.columns = rows[0].columns().iter().map(|c| c.name().to_string()).collect();
        out.truncated = rows.len() > MAX_ROWS;
        out.rows = rows
            .into_iter()
            .take(MAX_ROWS)
            .map(|row| row.into_iter().map(ms_value).collect())
            .collect();
    }
    Ok(out)
}

fn ms_value(v: tiberius::ColumnData<'static>) -> Option<String> {
    use tiberius::ColumnData as D;
    match v {
        D::U8(x) => x.map(|v| v.to_string()),
        D::I16(x) => x.map(|v| v.to_string()),
        D::I32(x) => x.map(|v| v.to_string()),
        D::I64(x) => x.map(|v| v.to_string()),
        D::F32(x) => x.map(|v| v.to_string()),
        D::F64(x) => x.map(|v| v.to_string()),
        D::Bit(x) => x.map(|v| if v { "1".into() } else { "0".into() }),
        D::String(x) => x.map(|v| v.into_owned()),
        D::Guid(x) => x.map(|v| v.to_string()),
        D::Binary(x) => x.map(|b| format!("0x{}", b.iter().map(|x| format!("{x:02X}")).collect::<String>())),
        D::Numeric(x) => x.map(|v| v.to_string()),
        D::Xml(x) => x.map(|v| v.to_string()),
        other => {
            let s = format!("{other:?}");
            if s.contains("(None)") { None } else { Some(s) }
        }
    }
}

fn run_lite(c: &rusqlite::Connection, sql: &str) -> Result<QueryResult, CoreError> {
    use rusqlite::types::ValueRef;
    let mut stmt = c.prepare(sql).map_err(db_err)?;
    let mut out = empty();
    if stmt.column_count() == 0 {
        let n = stmt.execute([]).map_err(db_err)?;
        out.affected = Some(n as u64);
        return Ok(out);
    }
    out.columns = stmt.column_names().map(|s| s.to_string()).collect();
    let n = out.columns.len();
    let mut rows = stmt.query([]).map_err(db_err)?;
    while let Some(row) = rows.next().map_err(db_err)? {
        if out.rows.len() >= MAX_ROWS {
            out.truncated = true;
            break;
        }
        let vals = (0..n)
            .map(|i| match row.get_ref(i) {
                Ok(ValueRef::Null) | Err(_) => None,
                Ok(ValueRef::Integer(v)) => Some(v.to_string()),
                Ok(ValueRef::Real(v)) => Some(v.to_string()),
                Ok(ValueRef::Text(t)) => Some(String::from_utf8_lossy(t).into_owned()),
                Ok(ValueRef::Blob(b)) => Some(format!("<blob {} byte>", b.len())),
            })
            .collect();
        out.rows.push(vals);
    }
    Ok(out)
}
