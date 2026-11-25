//! Server SSH + SFTP russh di loopback untuk tes halaman Env.
//!
//! Mesin pengembangan tidak selalu punya sshd, jadi server dibuat di dalam proses
//! tes. Yang diuji tetap jalur sungguhan: autentikasi password, subsistem `sftp`,
//! buka/baca/tulis file, dan chmod.
//!
//! Isi file disimpan di folder sementara, sedangkan izin file dicatat di memori
//! (`FsState`) karena Windows tidak mengenal mode Unix. Dengan begitu tes tetap bisa
//! memastikan klien mengirim chmod dan mempertahankan izin file lama.

use std::collections::HashMap;
use std::io::SeekFrom;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, UNIX_EPOCH};

use russh::keys::ssh_key::{PrivateKey, private::Ed25519Keypair};
use russh::server::{Auth, ChannelOpenHandle, Msg, Server as _, Session};
use russh::{Channel, ChannelId};
use russh_sftp::protocol::{
    Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version,
};
use tokio::io::{AsyncReadExt, AsyncSeekExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// Izin bawaan file baru di server tes ini.
const DEFAULT_MODE: u32 = 0o644;
/// Bit tipe pada mode Unix: folder dan file biasa.
const MODE_DIR: u32 = 0o40000;
const MODE_REG: u32 = 0o100000;

/// Keadaan yang dicatat server tes: izin file (path remote → mode), riwayat rename
/// dan penghapusan, serta path yang penulisannya sengaja digagalkan — supaya tes
/// bisa membuktikan penulisan memakai file sementara + rename dan membersihkannya
/// saat gagal.
#[derive(Clone, Default)]
pub struct FsState {
    modes: Arc<Mutex<HashMap<String, u32>>>,
    renames: Arc<Mutex<Vec<(String, String)>>>,
    removes: Arc<Mutex<Vec<String>>>,
    fail_write: Arc<Mutex<Vec<String>>>,
}

impl FsState {
    pub fn set(&self, path: &str, mode: u32) {
        self.modes.lock().unwrap().insert(path.to_string(), mode);
    }

    pub fn get(&self, path: &str) -> Option<u32> {
        self.modes.lock().unwrap().get(path).copied()
    }

    /// Semua rename yang diminta klien, berurutan: (dari, ke).
    pub fn renames(&self) -> Vec<(String, String)> {
        self.renames.lock().unwrap().clone()
    }

    fn renamed(&self, from: &str, to: &str) {
        self.renames
            .lock()
            .unwrap()
            .push((from.to_string(), to.to_string()));
    }

    /// Semua file yang dihapus klien, berurutan.
    pub fn removes(&self) -> Vec<String> {
        self.removes.lock().unwrap().clone()
    }

    fn removed(&self, path: &str) {
        self.modes.lock().unwrap().remove(path);
        self.removes.lock().unwrap().push(path.to_string());
    }

    /// Semua permintaan tulis ke `path` gagal, untuk menguji pembersihan `.tmp`.
    pub fn fail_write(&self, path: &str) {
        self.fail_write.lock().unwrap().push(path.to_string());
    }

    fn write_fails(&self, path: &str) -> bool {
        self.fail_write.lock().unwrap().iter().any(|p| p == path)
    }

    fn of(&self, path: &str) -> u32 {
        self.get(path).unwrap_or(DEFAULT_MODE)
    }
}

/// Server yang sedang berjalan: cukup port-nya, tugasnya berhenti bersama proses tes.
pub struct Serving {
    pub port: u16,
}

pub async fn serve(root: PathBuf, modes: FsState) -> Serving {
    let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let config = Arc::new(russh::server::Config {
        keys: vec![PrivateKey::from(Ed25519Keypair::from_seed(&[9u8; 32]))],
        auth_rejection_time: Duration::ZERO,
        auth_rejection_time_initial: Some(Duration::ZERO),
        inactivity_timeout: Some(Duration::from_secs(60)),
        ..Default::default()
    });
    let mut server = Ssh {
        root,
        modes,
        channels: HashMap::new(),
    };
    tokio::spawn(async move {
        if let Err(e) = server.run_on_socket(config, &listener).await {
            eprintln!("server tes berhenti: {e}");
        }
    });
    Serving { port }
}

struct Ssh {
    root: PathBuf,
    modes: FsState,
    channels: HashMap<ChannelId, Channel<Msg>>,
}

impl russh::server::Server for Ssh {
    type Handler = Self;

    fn new_client(&mut self, _: Option<SocketAddr>) -> Self {
        Self {
            root: self.root.clone(),
            modes: self.modes.clone(),
            channels: HashMap::new(),
        }
    }
}

impl russh::server::Handler for Ssh {
    type Error = russh::Error;

    async fn auth_password(&mut self, _user: &str, _password: &str) -> Result<Auth, Self::Error> {
        Ok(Auth::Accept)
    }

    async fn channel_open_session(
        &mut self,
        channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.channels.insert(channel.id(), channel);
        reply.accept().await;
        Ok(())
    }

    async fn subsystem_request(
        &mut self,
        id: ChannelId,
        name: &str,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        if name != "sftp" {
            session.channel_failure(id)?;
            return Ok(());
        }
        let Some(channel) = self.channels.remove(&id) else {
            session.channel_failure(id)?;
            return Ok(());
        };
        session.channel_success(id)?;
        let fs = Fs {
            root: self.root.clone(),
            modes: self.modes.clone(),
            open: HashMap::new(),
            next: 0,
        };
        russh_sftp::server::run(channel.into_stream(), fs).await;
        Ok(())
    }
}

struct Fs {
    root: PathBuf,
    modes: FsState,
    /// handle → (path lokal, path remote, file terbuka)
    open: HashMap<String, (PathBuf, String, tokio::fs::File)>,
    next: u64,
}

impl Fs {
    fn local(&self, remote: &str) -> PathBuf {
        self.root.join(remote.trim_start_matches('/'))
    }

    fn attrs(&self, remote: &str, meta: std::fs::Metadata) -> FileAttributes {
        let mut a = FileAttributes::empty();
        let kind = if meta.is_dir() { MODE_DIR } else { MODE_REG };
        let mode = if meta.is_dir() { 0o755 } else { self.modes.of(remote) };
        a.size = Some(meta.len());
        a.permissions = Some(kind | mode);
        a.mtime = meta
            .modified()
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_secs() as u32);
        a
    }

    fn attrs_of(&self, remote: &str) -> Result<FileAttributes, StatusCode> {
        let meta = std::fs::metadata(self.local(remote)).map_err(|_| StatusCode::NoSuchFile)?;
        Ok(self.attrs(remote, meta))
    }
}

fn ok(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: "Ok".to_string(),
        language_tag: "en-US".to_string(),
    }
}

impl russh_sftp::server::Handler for Fs {
    type Error = StatusCode;

    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }

    async fn init(
        &mut self,
        _version: u32,
        _extensions: HashMap<String, String>,
    ) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn open(
        &mut self,
        id: u32,
        filename: String,
        pflags: OpenFlags,
        _attrs: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        let local = self.local(&filename);
        let mut opts = tokio::fs::OpenOptions::new();
        opts.read(pflags.contains(OpenFlags::READ));
        opts.write(pflags.contains(OpenFlags::WRITE) || pflags.contains(OpenFlags::APPEND));
        opts.create(pflags.contains(OpenFlags::CREATE));
        opts.truncate(pflags.contains(OpenFlags::TRUNCATE));
        opts.append(pflags.contains(OpenFlags::APPEND));
        let file = opts.open(&local).await.map_err(|_| StatusCode::NoSuchFile)?;
        self.next += 1;
        let handle = format!("h{}", self.next);
        self.open.insert(handle.clone(), (local, filename, file));
        Ok(Handle { id, handle })
    }

    async fn read(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        len: u32,
    ) -> Result<Data, Self::Error> {
        let Some((_, _, file)) = self.open.get_mut(&handle) else {
            return Err(StatusCode::Failure);
        };
        file.seek(SeekFrom::Start(offset)).await.map_err(|_| StatusCode::Failure)?;
        let mut buf = vec![0u8; len as usize];
        let n = file.read(&mut buf).await.map_err(|_| StatusCode::Failure)?;
        if n == 0 {
            return Err(StatusCode::Eof);
        }
        buf.truncate(n);
        Ok(Data { id, data: buf })
    }

    async fn write(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        data: Vec<u8>,
    ) -> Result<Status, Self::Error> {
        let Some((_, remote, _)) = self.open.get(&handle) else {
            return Err(StatusCode::Failure);
        };
        let remote = remote.clone();
        if self.modes.write_fails(&remote) {
            return Err(StatusCode::Failure);
        }
        let Some((_, _, file)) = self.open.get_mut(&handle) else {
            return Err(StatusCode::Failure);
        };
        file.seek(SeekFrom::Start(offset)).await.map_err(|_| StatusCode::Failure)?;
        file.write_all(&data).await.map_err(|_| StatusCode::Failure)?;
        Ok(ok(id))
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        self.open.remove(&handle);
        Ok(ok(id))
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs { id, attrs: self.attrs_of(&path)? })
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs { id, attrs: self.attrs_of(&path)? })
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        let Some((_, remote, _)) = self.open.get(&handle) else {
            return Err(StatusCode::Failure);
        };
        let remote = remote.clone();
        Ok(Attrs { id, attrs: self.attrs_of(&remote)? })
    }

    async fn rename(
        &mut self,
        id: u32,
        oldpath: String,
        newpath: String,
    ) -> Result<Status, Self::Error> {
        std::fs::rename(self.local(&oldpath), self.local(&newpath))
            .map_err(|_| StatusCode::Failure)?;
        // Izin yang dicatat menempel pada file, jadi ikut pindah ke path baru.
        if let Some(mode) = self.modes.get(&oldpath) {
            self.modes.set(&newpath, mode);
        }
        self.modes.renamed(&oldpath, &newpath);
        Ok(ok(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        std::fs::remove_file(self.local(&filename)).map_err(|_| StatusCode::Failure)?;
        self.modes.removed(&filename);
        Ok(ok(id))
    }

    async fn setstat(
        &mut self,
        id: u32,
        path: String,
        attrs: FileAttributes,
    ) -> Result<Status, Self::Error> {
        if let Some(mode) = attrs.permissions {
            self.modes.set(&path, mode & 0o7777);
        }
        Ok(ok(id))
    }

    async fn fsetstat(
        &mut self,
        id: u32,
        handle: String,
        attrs: FileAttributes,
    ) -> Result<Status, Self::Error> {
        let Some((_, remote, _)) = self.open.get(&handle) else {
            return Err(StatusCode::Failure);
        };
        let remote = remote.clone();
        if let Some(mode) = attrs.permissions {
            self.modes.set(&remote, mode & 0o7777);
        }
        Ok(ok(id))
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let shown = if path.is_empty() || path == "." { "/".to_string() } else { path };
        Ok(Name { id, files: vec![File::dummy(shown)] })
    }
}
