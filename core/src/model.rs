use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
#[serde(rename_all = "lowercase")]
pub enum AuthMethod {
    Password,
    Key,
    Agent,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct EnvVar {
    pub name: String,
    pub value: String,
}

/// Satu server SSH. `updated_at` + `deleted` dipakai untuk merge saat sync.
///
/// Semua field opsional memakai `#[serde(default)]` agar file vault lama tetap terbaca
/// ketika field baru ditambahkan.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct Host {
    /// Kosongkan saat membuat host baru; core yang mengisi.
    pub id: String,

    // Address & General
    pub hostname: String,
    pub label: String,
    #[serde(default)]
    pub group: Option<String>,
    pub tags: Vec<String>,
    /// Warna kartu, hex `#RRGGBB`.
    pub color: String,

    // SSH & Credentials
    pub port: u16,
    pub username: String,
    pub auth: AuthMethod,
    /// Disimpan di dalam vault terenkripsi.
    #[serde(default)]
    pub password: Option<String>,
    #[serde(default)]
    pub key_id: Option<String>,

    // Lanjutan
    #[serde(default)]
    pub agent_forwarding: bool,
    #[serde(default)]
    pub startup_snippet: Option<String>,
    /// Host lain yang dipakai sebagai jump host (ProxyJump).
    #[serde(default)]
    pub jump_host_id: Option<String>,
    /// Konek lewat proxy SOCKS5 / HTTP (mis. jaringan kantor).
    #[serde(default)]
    pub proxy: Option<HostProxy>,
    #[serde(default)]
    pub env: Vec<EnvVar>,
    #[serde(default = "default_charset")]
    pub charset: String,
    #[serde(default)]
    pub terminal_theme: Option<String>,

    /// Epoch milidetik.
    pub updated_at: i64,
    pub deleted: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
#[serde(rename_all = "lowercase")]
pub enum ProxyKind {
    Socks5,
    Http,
}

/// Proxy yang dipakai untuk mencapai server SSH. Password ikut terenkripsi di vault.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct HostProxy {
    pub kind: ProxyKind,
    pub host: String,
    pub port: u16,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub password: Option<String>,
}

fn default_charset() -> String {
    "UTF-8".into()
}

/// SSH key di Keychain. Private key & passphrase hanya ada di dalam vault terenkripsi.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SshKey {
    pub id: String,
    pub label: String,
    /// mis. `ssh-ed25519`, `ssh-rsa`, `ecdsa-sha2-nistp256`
    pub key_type: String,
    pub public_key: String,
    pub private_key: String,
    pub passphrase: Option<String>,
    /// `SHA256:...`
    pub fingerprint: String,
    pub updated_at: i64,
    pub deleted: bool,
}

/// Perintah tersimpan yang bisa dijalankan ke satu atau banyak host.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct Snippet {
    pub id: String,
    pub label: String,
    pub command: String,
    pub updated_at: i64,
    pub deleted: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
#[serde(rename_all = "lowercase")]
pub enum ForwardKind {
    /// `-L` : port lokal → tujuan lewat server
    Local,
    /// `-R` : port di server → tujuan di sisi lokal
    Remote,
    /// `-D` : proxy SOCKS lokal
    Dynamic,
}

/// Aturan port forwarding.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct PortForward {
    pub id: String,
    pub label: String,
    pub host_id: String,
    pub kind: ForwardKind,
    pub bind_address: String,
    pub bind_port: u16,
    pub dest_host: String,
    pub dest_port: u16,
    pub updated_at: i64,
    pub deleted: bool,
}

/// Riwayat satu sesi SSH.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SessionLog {
    pub id: String,
    pub host_id: String,
    pub host_label: String,
    pub address: String,
    pub started_at: i64,
    pub ended_at: Option<i64>,
    pub exit_code: Option<i32>,
    pub updated_at: i64,
    pub deleted: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
#[serde(rename_all = "lowercase")]
pub enum DbEngine {
    Postgres,
    Mysql,
    Mariadb,
    Sqlserver,
    Sqlite,
    Redis,
    Mongodb,
    Clickhouse,
}

/// Koneksi database tersimpan. Password ada di dalam vault terenkripsi.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DbConnection {
    pub id: String,
    pub label: String,
    pub engine: DbEngine,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub password: Option<String>,
    /// Nama database awal (kosong = default server).
    pub database: String,
    /// Path file untuk SQLite.
    pub file_path: String,
    /// Pakai TLS/SSL (Postgres: coba TLS dulu lalu tanpa TLS jika `false`).
    pub tls: bool,
    /// Host SSH untuk tunnel (None = koneksi langsung).
    pub ssh_host_id: Option<String>,
    pub color: String,
    pub updated_at: i64,
    pub deleted: bool,
}

/// Isi vault yang dienkripsi.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct VaultData {
    #[serde(default)]
    pub hosts: Vec<Host>,
    #[serde(default)]
    pub keys: Vec<SshKey>,
    #[serde(default)]
    pub snippets: Vec<Snippet>,
    #[serde(default)]
    pub forwards: Vec<PortForward>,
    #[serde(default)]
    pub logs: Vec<SessionLog>,
    #[serde(default)]
    pub databases: Vec<DbConnection>,
    #[serde(default)]
    pub account_email: Option<String>,
}

pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// Hasil penggabungan vault lokal dengan salinan di Google Drive.
#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct SyncOutcome {
    /// Ada data baru dari Drive yang masuk ke vault lokal.
    pub local_changed: bool,
    /// Salinan di Drive tertinggal dan perlu di-upload ulang.
    pub remote_outdated: bool,
}

/// Record yang bisa disinkronkan (punya id, waktu ubah, dan tombstone).
pub trait Record: Clone {
    fn id(&self) -> &str;
    fn ts(&self) -> i64;
    fn set_id(&mut self, id: String);
    fn touch(&mut self, deleted: bool);
}

macro_rules! impl_record {
    ($($t:ty),*) => {$(
        impl Record for $t {
            fn id(&self) -> &str { &self.id }
            fn ts(&self) -> i64 { self.updated_at }
            fn set_id(&mut self, id: String) { self.id = id; }
            fn touch(&mut self, deleted: bool) { self.updated_at = now_ms(); self.deleted = deleted; }
        }
    )*};
}
impl_record!(Host, SshKey, Snippet, PortForward, SessionLog, DbConnection);
