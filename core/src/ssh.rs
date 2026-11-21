//! Koneksi SSH native (russh) — dipakai SFTP sekarang, dan nanti terminal & tunnel database.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use russh::client::{self, Handle};
use russh::keys::agent::client::AgentClient;
use russh::keys::{PrivateKeyWithHashAlg, PublicKeyOrCertificate};

use crate::CoreError;

/// Data yang dibutuhkan untuk membuka sesi SSH ke satu host.
#[derive(Debug, Clone, uniffi::Record)]
pub struct SshTarget {
    pub hostname: String,
    pub port: u16,
    pub username: String,
    pub password: Option<String>,
    /// Private key format OpenSSH (isi file), jika login dengan key.
    pub private_key: Option<String>,
    pub passphrase: Option<String>,
    /// Coba SSH agent sistem (SSH_AUTH_SOCK).
    pub use_agent: bool,
    /// Sambung lewat proxy SOCKS5 / HTTP.
    pub proxy: Option<crate::model::HostProxy>,
}

/// Handler russh: memverifikasi kunci server terhadap ~/.ssh/known_hosts.
/// Host baru dicatat otomatis (seperti `StrictHostKeyChecking=accept-new`);
/// kunci yang berubah ditolak.
pub struct Client {
    host: String,
    port: u16,
    pub(crate) key_changed: Arc<std::sync::atomic::AtomicBool>,
}

impl client::Handler for Client {
    type Error = russh::Error;

    async fn check_server_key(&mut self, server_key: &PublicKeyOrCertificate) -> Result<bool, Self::Error> {
        let key = match server_key {
            PublicKeyOrCertificate::PublicKey { key, .. } => key,
            // Sertifikat host: terima (diverifikasi CA-nya oleh admin server).
            PublicKeyOrCertificate::Certificate(_) => return Ok(true),
        };
        match russh::keys::check_known_hosts(&self.host, self.port, key) {
            Ok(true) => Ok(true),
            Ok(false) => {
                learn_host(&self.host, self.port, key);
                Ok(true)
            }
            Err(russh::keys::Error::KeyChanged { .. }) => {
                self.key_changed.store(true, std::sync::atomic::Ordering::SeqCst);
                Ok(false)
            }
            // known_hosts tidak ada / tidak terbaca → catat host baru
            Err(_) => {
                learn_host(&self.host, self.port, key);
                Ok(true)
            }
        }
    }
}

fn known_hosts_path() -> Option<PathBuf> {
    // Windows tidak punya $HOME: pakai %USERPROFILE% (sama dengan OpenSSH Windows)
    std::env::var_os("HOME")
        .or_else(|| std::env::var_os("USERPROFILE"))
        .map(|h| PathBuf::from(h).join(".ssh").join("known_hosts"))
}

/// Tambah baris ke known_hosts dengan format yang sama seperti OpenSSH.
fn learn_host(host: &str, port: u16, key: &russh::keys::PublicKey) {
    let Some(path) = known_hosts_path() else { return };
    let Ok(line) = key.to_openssh() else { return };
    let name = if port == 22 { host.to_string() } else { format!("[{host}]:{port}") };
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
        let _ = writeln!(f, "{name} {line}");
    }
}

fn ssh_err(e: impl std::fmt::Display) -> CoreError {
    CoreError::Ssh { msg: e.to_string() }
}

/// Buka koneksi SSH dan autentikasi: key → agent → password → keyboard-interactive.
/// Coba semua identitas di SSH agent. `true` jika salah satu diterima server.
async fn agent_auth<S>(h: &mut Handle<Client>, user: &str, mut agent: AgentClient<S>) -> Result<bool, CoreError>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let Ok(ids) = agent.request_identities().await else { return Ok(false) };
    let hash = h.best_supported_rsa_hash().await.map_err(ssh_err)?.flatten();
    for id in ids {
        let pk = id.public_key().into_owned();
        if let Ok(res) = h.authenticate_publickey_with(user, pk, hash, &mut agent).await {
            if res.success() {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

pub async fn connect(t: &SshTarget) -> Result<Handle<Client>, CoreError> {
    let config = Arc::new(client::Config {
        inactivity_timeout: None,
        keepalive_interval: Some(Duration::from_secs(30)),
        keepalive_max: 4,
        ..Default::default()
    });
    let changed = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let handler = Client { host: t.hostname.clone(), port: t.port, key_changed: changed.clone() };

    let connecting = async {
        match &t.proxy {
            Some(p) => {
                let stream = crate::proxy::open(p, &t.hostname, t.port).await.map_err(Err)?;
                client::connect_stream(config, stream, handler).await.map_err(Ok)
            }
            None => client::connect(config, (t.hostname.as_str(), t.port), handler).await.map_err(Ok),
        }
    };
    let mut h = match tokio::time::timeout(Duration::from_secs(30), connecting).await {
        Err(_) => return Err(CoreError::Ssh { msg: "waktu koneksi habis (30 detik)".into() }),
        // Gagal di proxy: teruskan pesan proxy apa adanya
        Ok(Err(Err(e))) => return Err(e),
        Ok(Err(Ok(e))) => {
            if changed.load(std::sync::atomic::Ordering::SeqCst) {
                return Err(CoreError::HostKeyChanged);
            }
            return Err(ssh_err(e));
        }
        Ok(Ok(h)) => h,
    };

    let user = t.username.clone();

    // 1. Private key dari Keychain Zevn Shell
    if let Some(pem) = t.private_key.as_deref().filter(|p| !p.trim().is_empty()) {
        let key = russh::keys::decode_secret_key(pem, t.passphrase.as_deref().filter(|p| !p.is_empty()))
            .map_err(|e| CoreError::InvalidKey { msg: e.to_string() })?;
        let hash = h.best_supported_rsa_hash().await.map_err(ssh_err)?.flatten();
        let res = h
            .authenticate_publickey(&user, PrivateKeyWithHashAlg::new(Arc::new(key), hash))
            .await
            .map_err(ssh_err)?;
        if res.success() {
            return Ok(h);
        }
    }

    // 2. SSH agent sistem
    if t.use_agent {
        // macOS/Linux: $SSH_AUTH_SOCK
        #[cfg(unix)]
        if let Ok(agent) = AgentClient::connect_env().await {
            if agent_auth(&mut h, &user, agent).await? {
                return Ok(h);
            }
        }
        // Windows: agent OpenSSH (named pipe), lalu Pageant (PuTTY)
        #[cfg(windows)]
        {
            if let Ok(agent) = AgentClient::connect_named_pipe(r"\\.\pipe\openssh-ssh-agent").await {
                if agent_auth(&mut h, &user, agent).await? {
                    return Ok(h);
                }
            }
            if let Ok(agent) = AgentClient::connect_pageant().await {
                if agent_auth(&mut h, &user, agent).await? {
                    return Ok(h);
                }
            }
        }
    }

    // 3. Password, lalu keyboard-interactive dengan password yang sama
    if let Some(pw) = t.password.as_deref().filter(|p| !p.is_empty()) {
        if h.authenticate_password(&user, pw).await.map_err(ssh_err)?.success() {
            return Ok(h);
        }
        use russh::client::KeyboardInteractiveAuthResponse as K;
        let mut resp = h.authenticate_keyboard_interactive_start(&user, None::<String>).await.map_err(ssh_err)?;
        for _ in 0..3 {
            match resp {
                K::Success => return Ok(h),
                K::Failure { .. } => break,
                K::InfoRequest { prompts, .. } => {
                    let answers = prompts.iter().map(|_| pw.to_string()).collect();
                    resp = h.authenticate_keyboard_interactive_respond(answers).await.map_err(ssh_err)?;
                }
            }
        }
    }

    Err(CoreError::AuthFailed)
}
