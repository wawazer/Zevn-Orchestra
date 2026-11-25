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
/// Batas file teks yang boleh dibuka di editor (mis. .env).
const MAX_TEXT: u64 = 1024 * 1024;

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

/// Hasil menjalankan satu perintah non-interaktif di server.
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExecResult {
    /// Kode keluar perintah (-1 jika server tidak melaporkannya).
    pub status: i32,
    pub stdout: String,
    pub stderr: String,
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

    /// Baca isi file teks (maks 1 MB). `None` bila file belum ada di server.
    pub async fn read_text(&self, path: String) -> Result<Option<String>, CoreError> {
        let meta = match self.sftp.metadata(path.clone()).await {
            Ok(m) => m,
            Err(_) => return Ok(None),
        };
        if meta.is_dir() {
            return Err(CoreError::Io { msg: format!("{path} adalah folder, bukan file") });
        }
        if meta.size.unwrap_or(0) > MAX_TEXT {
            return Err(CoreError::Io { msg: format!("file lebih besar dari 1 MB: {path}") });
        }
        let mut file = self.sftp.open_with_flags(path, OpenFlags::READ).await.map_err(err)?;
        let mut buf = Vec::new();
        file.read_to_end(&mut buf).await.map_err(err)?;
        Ok(Some(String::from_utf8_lossy(&buf).into_owned()))
    }

    /// Tulis file teks. Bila `backup` dan file sudah ada, isi lama disalin dulu ke `<path>.bak`.
    /// Isi baru ditulis ke `<path>.tmp` lalu diganti nama, jadi file tujuan tidak pernah
    /// setengah tertulis. Mengembalikan path backup (None bila belum ada file lama).
    pub async fn write_text(&self, path: String, content: String, backup: bool) -> Result<Option<String>, CoreError> {
        let old = match self.sftp.metadata(path.clone()).await {
            Ok(m) if m.is_dir() => {
                return Err(CoreError::Io { msg: format!("{path} adalah folder, bukan file") });
            }
            Ok(m) => Some(m),
            Err(_) => None,
        };

        // Izin file lama dipakai lagi setelah isi baru ditulis.
        let had_old = old.is_some();
        let old_mode = old.as_ref().and_then(|m| m.permissions);

        let mut backup_path = None;
        if backup {
            if let Some(meta) = old {
                let bak = format!("{path}.bak");
                let mut src = self.sftp.open_with_flags(path.clone(), OpenFlags::READ).await.map_err(err)?;
                let mut data = Vec::new();
                src.read_to_end(&mut data).await.map_err(err)?;
                let mut dst = self
                    .sftp
                    .open_with_flags(bak.clone(), OpenFlags::CREATE | OpenFlags::TRUNCATE | OpenFlags::WRITE)
                    .await
                    .map_err(err)?;
                dst.write_all(&data).await.map_err(err)?;
                dst.flush().await.map_err(err)?;
                dst.shutdown().await.map_err(err)?;
                if let Some(mode) = meta.permissions {
                    let _ = self.chmod(bak.clone(), mode & 0o7777).await;
                }
                backup_path = Some(bak);
            }
        }

        // Tulis ke file sementara lalu ganti nama: file tujuan tidak pernah setengah
        // tertulis walau koneksi putus di tengah penulisan.
        let tmp = format!("{path}.tmp");
        let mut file = self
            .sftp
            .open_with_flags(
                tmp.clone(),
                OpenFlags::CREATE | OpenFlags::TRUNCATE | OpenFlags::WRITE,
            )
            .await
            .map_err(err)?;
        file.write_all(content.as_bytes()).await.map_err(err)?;
        file.flush().await.map_err(err)?;
        file.shutdown().await.map_err(err)?;

        // Penggantian nama tidak mengubah mode, jadi izin lama dipasang di file sementara.
        if let Some(mode) = old_mode {
            let _ = self.chmod(tmp.clone(), mode & 0o7777).await;
        }

        if let Err(e) = self.sftp.rename(tmp.clone(), path.clone()).await {
            let _ = self.sftp.remove_file(tmp.clone()).await;
            if !had_old {
                return Err(err(e));
            }
            // Server yang menolak menimpa lewat rename: tulis langsung seperti sebelumnya.
            let mut file = self
                .sftp
                .open_with_flags(path, OpenFlags::CREATE | OpenFlags::TRUNCATE | OpenFlags::WRITE)
                .await
                .map_err(err)?;
            file.write_all(content.as_bytes()).await.map_err(err)?;
            file.flush().await.map_err(err)?;
            file.shutdown().await.map_err(err)?;
        }
        Ok(backup_path)
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

    /// Jalankan perintah non-interaktif lewat koneksi yang sama (dipakai deploy).
    /// `timeout_secs` membatasi perintah yang menggantung.
    pub async fn exec(&self, command: String, timeout_secs: u32) -> Result<ExecResult, CoreError> {
        let mut channel = {
            let guard = self.handle.lock().await;
            let h = guard.as_ref().ok_or_else(|| CoreError::Deploy { msg: "koneksi sudah ditutup".into() })?;
            h.channel_open_session().await.map_err(err)?
        };
        channel.exec(true, command).await.map_err(err)?;

        let collect = async {
            let mut out = Vec::new();
            let mut errs = Vec::new();
            let mut status = -1i32;
            while let Some(msg) = channel.wait().await {
                match msg {
                    russh::ChannelMsg::Data { data } => out.extend_from_slice(&data),
                    russh::ChannelMsg::ExtendedData { data, ext } if ext == 1 => errs.extend_from_slice(&data),
                    russh::ChannelMsg::ExitStatus { exit_status } => status = exit_status as i32,
                    _ => {}
                }
            }
            (out, errs, status)
        };
        let limit = std::time::Duration::from_secs(u64::from(timeout_secs.max(1)));
        match tokio::time::timeout(limit, collect).await {
            Err(_) => Err(CoreError::Deploy { msg: format!("perintah melewati batas waktu {timeout_secs} detik") }),
            Ok((out, errs, status)) => Ok(ExecResult {
                status,
                stdout: String::from_utf8_lossy(&out).into_owned(),
                stderr: String::from_utf8_lossy(&errs).into_owned(),
            }),
        }
    }

    /// Unggah isi folder lokal ke `remote_dir` (tanpa membungkusnya dalam satu folder),
    /// melewati nama yang ada di `excludes` (mis. `.git`, `node_modules`).
    pub async fn upload_into(&self, local_dir: String, remote_dir: String, excludes: Vec<String>, progress: Arc<dyn TransferProgress>) -> Result<(), CoreError> {
        let root = PathBuf::from(&local_dir);
        if !tokio::fs::metadata(&root).await.map(|m| m.is_dir()).unwrap_or(false) {
            return Err(CoreError::Io { msg: format!("bukan folder: {local_dir}") });
        }
        let mut files: Vec<(PathBuf, String, u64)> = vec![];
        let mut dirs: Vec<String> = vec![];
        walk_into(&root, &remote_dir, &excludes, &mut files, &mut dirs)?;
        for d in &dirs {
            let _ = self.sftp.create_dir(d.clone()).await;
        }

        let total: u64 = files.iter().map(|f| f.2).sum();
        let mut done = 0u64;
        progress.on_progress(0, total, String::new());
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
        }
        progress.on_progress(total, total, String::new());
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

/// Telusuri isi folder lokal: nama yang dikecualikan (dan .DS_Store) dilewati.
fn walk_into(dir: &Path, remote: &str, excludes: &[String], files: &mut Vec<(PathBuf, String, u64)>, dirs: &mut Vec<String>) -> Result<(), CoreError> {
    for entry in std::fs::read_dir(dir)? {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if name == ".DS_Store" || excludes.iter().any(|e| e == &name) { continue; }
        let rp = join(remote, &name);
        let ft = entry.file_type()?;
        if ft.is_dir() {
            dirs.push(rp.clone());
            walk_into(&entry.path(), &rp, excludes, files, dirs)?;
        } else if ft.is_file() {
            files.push((entry.path(), rp, entry.metadata()?.len()));
        }
    }
    Ok(())
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
