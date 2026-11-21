//! zevn-core — inti bersama Zevn Shell untuk macOS (Swift) dan Windows (C#).
//!
//! Diekspor lewat UniFFI. Modul:
//! - [`model`]  : struktur data (Host, AuthMethod, ...)
//! - [`vault`]  : penyimpanan terenkripsi (Argon2id + XChaCha20-Poly1305)
//! - [`crypto`] : primitif enkripsi & format file

mod crypto;
mod db;
mod deploy;
mod error;
mod keys;
mod logs;
mod model;
mod proxy;
mod server_info;
mod sftp;
mod ssh;
mod vault;

pub use error::CoreError;
pub use db::{DbFilter, DbSession, DbTable, QueryResult};
pub use deploy::{DeployConfig, DeployOutcome, DeployProgress, Deployer, Release};
pub use logs::{LogSink, LogStream};
pub use model::{AuthMethod, DbConnection, DbEngine, EnvVar, ForwardKind, Host, HostProxy, PortForward, Project, ProxyKind, SessionLog, Snippet, SshKey, SyncOutcome};
pub use proxy::proxy_stdio;
pub use server_info::{DiskInfo, ServerInfo, ServerMonitor, ServerSample};
pub use sftp::{ExecResult, RemoteEntry, SftpClient, TransferProgress};
pub use ssh::SshTarget;
pub use vault::{Vault, vault_exists};

uniffi::setup_scaffolding!();

/// Versi core, ditampilkan di Settings / About.
#[uniffi::export]
pub fn core_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}
