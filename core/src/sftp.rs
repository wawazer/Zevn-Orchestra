//! Klien SFTP: jelajah file server, upload/download (file & folder) dengan progress.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use russh_sftp::client::SftpSession;
use russh_sftp::protocol::{FileAttributes, OpenFlags};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::Mutex;

use crate::ssh::{self, Client, SshTarget};
use crate::CoreError;

const CHUNK: usize = 256 * 1024;

#[derive(Debug, Clone, uniffi::Record)]
pub struct RemoteEntry {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub is_symlink: bool,
    pub size: u64,
    /// Detik sejak epoch (0 jika tidak diketahui).
    pub modified: i64,
    /// Bit izin Unix, mis. 0o755.
    pub permissions: u32,
}

/// Laporan progress transfer — diimplementasikan di Swift / C#.
#[uniffi::export(with_foreign)]
pub trait TransferProgress: Send + Sync {
    /// `done` dan `total` dalam byte (total seluruh file dalam folder).
    fn on_progress(&self, done: u64, total: u64, current_file: String);
    /// Kembalikan `true` untuk membatalkan transfer.
    fn is_cancelled(&self) -> bool;
}

#[derive(uniffi::Object)]
pub struct SftpClient {
    sftp: SftpSession,
    handle: Mutex<Option<russh::client::Handle<Client>>>,
}

fn err(e: impl std::fmt::Display) -> CoreError {
    CoreError::Ssh { msg: e.to_string() }
}

fn join(dir: &str, name: &str) -> String {
    if dir.ends_with('/') { format!("{dir}{name}") } else { format!("{dir}/{name}") }
}

#[uniffi::export(async_runtime = "tokio")]
impl SftpClient {
    /// Buka koneksi SSH lalu subsistem SFTP.
    #[uniffi::constructor]
    pub async fn connect(target: SshTarget) -> Result<Arc<Self>, CoreError> {
        let handle = ssh::connect(&target).await?;
        let channel = handle.channel_open_session().await.map_err(err)?;
        channel.request_subsystem(true, "sftp").await.map_err(err)?;
        let sftp = SftpSession::new(channel.into_stream()).await.map_err(err)?;
        Ok(Arc::new(Self { sftp, handle: Mutex::new(Some(handle)) }))
    }

    /// Folder awal (home user di server).
    pub async fn home(&self) -> Result<String, CoreError> {
        self.sftp.canonicalize(".").await.map_err(err)
    }

    pub async fn canonicalize(&self, path: String) -> Result<String, CoreError> {
        self.sftp.canonicalize(path).await.map_err(err)
    }

    /// Isi folder: folder dulu, lalu file, urut nama.
    pub async fn list(&self, path: String) -> Result<Vec<RemoteEntry>, CoreError> {
        let dir = self.sftp.read_dir(path.clone()).await.map_err(err)?;
        let mut out: Vec<RemoteEntry> = dir
            .filter(|e| e.file_name() != "." && e.file_name() != "..")
            .map(|e| {
                let m = e.metadata();
                let name = e.file_name();
                RemoteEntry {
                    path: join(&path, &name),
                    is_dir: m.is_dir(),
                    is_symlink: m.is_symlink(),
                    size: m.size.unwrap_or(0),
                    modified: m.mtime.map(i64::from).unwrap_or(0),
                    permissions: m.permissions.unwrap_or(0) & 0o7777,
                    name,
                }
            })
            .collect();
        out.sort_by(|a, b| b.is_dir.cmp(&a.is_dir).then(a.name.to_lowercase().cmp(&b.name.to_lowercase())));
        Ok(out)
    }

    pub async fn mkdir(&self, path: String) -> Result<(), CoreError> {
        self.sftp.create_dir(path).await.map_err(err)
    }

    pub async fn rename(&self, from: String, to: String) -> Result<(), CoreError> {
        self.sftp.rename(from, to).await.map_err(err)
    }

    pub async fn chmod(&self, path: String, mode: u32) -> Result<(), CoreError> {
        let mut attrs = FileAttributes::empty();
        attrs.permissions = Some(mode & 0o7777);
        self.sftp.set_metadata(path, attrs).await.map_err(err)
    }

    /// Hapus file atau folder (beserta isinya).
    pub async fn remove(&self, path: String) -> Result<(), CoreError> {
        let meta = self.sftp.metadata(path.clone()).await.map_err(err)?;
        if meta.is_dir() {
            for e in self.list(path.clone()).await? {
                Box::pin(self.remove(e.path)).await?;
            }
            self.sftp.remove_dir(path).await.map_err(err)
        } else {
            self.sftp.remove_file(path).await.map_err(err)
        }
    }

    /// Download file atau folder `remote` ke folder lokal `local_dir`.
    pub async fn download(&self, remote: String, local_dir: String, progress: Arc<dyn TransferProgress>) -> Result<(), CoreError> {
        let meta = self.sftp.metadata(remote.clone()).await.map_err(err)?;
        let name = Path::new(&remote).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or("download".into());
        let target = unique_local(&PathBuf::from(&local_dir).join(&name), meta.is_dir());

        // Kumpulkan daftar file dulu agar total progress diketahui
        let mut files: Vec<(String, PathBuf, u64)> = vec![];
        if meta.is_dir() {
            self.walk_remote(&remote, &target, &mut files).await?;
        } else {
            files.push((remote.clone(), target.clone(), meta.size.unwrap_or(0)));
        }
        if meta.is_dir() {
            tokio::fs::create_dir_all(&target).await?;
        }
        let total: u64 = files.iter().map(|f| f.2).sum();
        let mut done = 0u64;
        progress.on_progress(0, total, name.clone());

        for (rpath, lpath, _) in files {
            if let Some(parent) = lpath.parent() {
                tokio::fs::create_dir_all(parent).await?;
            }
            let mut src = self.sftp.open(rpath.clone()).await.map_err(err)?;
            let mut dst = tokio::fs::File::create(&lpath).await?;
            let mut buf = vec![0u8; CHUNK];
            let fname = Path::new(&rpath).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            loop {
                if progress.is_cancelled() {
                    drop(dst);
                    let _ = tokio::fs::remove_file(&lpath).await;
                    return Err(CoreError::Cancelled);
                }
                let n = src.read(&mut buf).await?;
                if n == 0 { break; }
                dst.write_all(&buf[..n]).await?;
                done += n as u64;
                progress.on_progress(done, total, fname.clone());
            }
            dst.flush().await?;
        }
        progress.on_progress(total, total, name);
        Ok(())
    }

    /// Upload file atau folder lokal ke folder server `remote_dir`.
    pub async fn upload(&self, local: String, remote_dir: String, progress: Arc<dyn TransferProgress>) -> Result<(), CoreError> {
        let lpath = PathBuf::from(&local);
        let name = lpath.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or("upload".into());
        let target = join(&remote_dir, &name);
        let is_dir = tokio::fs::metadata(&lpath).await?.is_dir();

        let mut files: Vec<(PathBuf, String, u64)> = vec![];
        let mut dirs: Vec<String> = vec![];
        if is_dir {
            dirs.push(target.clone());
            walk_local(&lpath, &target, &mut files, &mut dirs)?;
        } else {
            files.push((lpath.clone(), target.clone(), std::fs::metadata(&lpath)?.len()));
        }
        for d in &dirs {
            // abaikan error "sudah ada"
            let _ = self.sftp.create_dir(d.clone()).await;
        }
        let total: u64 = files.iter().map(|f| f.2).sum();
        let mut done = 0u64;
        progress.on_progress(0, total, name.clone());

        for (lp, rp, _) in files {
            let mut src = tokio::fs::File::open(&lp).await?;
            let mut dst = self
                .sftp
                .open_with_flags(rp.clone(), OpenFlags::CREATE | OpenFlags::TRUNCATE | OpenFlags::WRITE)
                .await
                .map_err(err)?;
            let mut buf = vec![0u8; CHUNK];
            let fname = lp.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            loop {
                if progress.is_cancelled() {
                    drop(dst);
                    let _ = self.sftp.remove_file(rp.clone()).await;
                    return Err(CoreError::Cancelled);
                }
                let n = src.read(&mut buf).await?;
                if n == 0 { break; }
                dst.write_all(&buf[..n]).await?;
                done += n as u64;
                progress.on_progress(done, total, fname.clone());
            }
            dst.flush().await?;
            dst.shutdown().await?;
            // Pertahankan izin eksekusi file lokal
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                if let Ok(m) = std::fs::metadata(&lp) {
                    let _ = self.chmod(rp.clone(), m.permissions().mode()).await;
                }
            }
        }
        progress.on_progress(total, total, name);
        Ok(())
    }

    /// Tutup sesi SFTP dan koneksi SSH.
    pub async fn close(&self) {
        let _ = self.sftp.close().await;
        if let Some(h) = self.handle.lock().await.take() {
            let _ = h.disconnect(russh::Disconnect::ByApplication, "", "en").await;
        }
    }
}

impl SftpClient {
    async fn walk_remote(&self, dir: &str, local: &Path, out: &mut Vec<(String, PathBuf, u64)>) -> Result<(), CoreError> {
        for e in self.list(dir.to_string()).await? {
            let lp = local.join(&e.name);
            if e.is_dir {
                Box::pin(self.walk_remote(&e.path, &lp, out)).await?;
            } else {
                out.push((e.path, lp, e.size));
            }
        }
        Ok(())
    }
}

fn walk_local(dir: &Path, remote: &str, files: &mut Vec<(PathBuf, String, u64)>, dirs: &mut Vec<String>) -> Result<(), CoreError> {
    for entry in std::fs::read_dir(dir)? {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if name == ".DS_Store" { continue; }
        let rp = join(remote, &name);
        let ft = entry.file_type()?;
        if ft.is_dir() {
            dirs.push(rp.clone());
            walk_local(&entry.path(), &rp, files, dirs)?;
        } else if ft.is_file() {
            files.push((entry.path(), rp, entry.metadata()?.len()));
        }
    }
    Ok(())
}

/// Hindari menimpa file lokal: "nama (1).ext".
fn unique_local(path: &Path, _is_dir: bool) -> PathBuf {
    if !path.exists() { return path.to_path_buf(); }
    let stem = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let ext = path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    for i in 1.. {
        let p = path.with_file_name(format!("{stem} ({i}){ext}"));
        if !p.exists() { return p; }
    }
    unreachable!()
}
