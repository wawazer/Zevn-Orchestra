//! Deploy satu klik: unggah rilis baru, pindahkan symlink `current`, jalankan perintah
//! restart + health check, dan kembalikan ke rilis sebelumnya bila gagal.
//!
//! Tata letak di server:
//! ```text
//! <base>/releases/<YYYYMMDD-HHMMSS>/   isi folder lokal
//! <base>/current -> releases/<...>     symlink yang dipakai service
//! ```

use std::sync::Arc;

use crate::model::now_ms;
use crate::sftp::{ExecResult, SftpClient, TransferProgress};
use crate::ssh::SshTarget;
use crate::CoreError;

/// Langkah deploy yang dilaporkan ke UI.
#[uniffi::export(with_foreign)]
pub trait DeployProgress: Send + Sync {
    /// `step` singkat (mis. "Unggah"), `detail` kalimat yang bisa ditampilkan apa adanya.
    fn on_step(&self, step: String, detail: String);
    /// Kembalikan `true` untuk membatalkan deploy.
    fn is_cancelled(&self) -> bool;
}

/// Satu folder rilis di server.
#[derive(Debug, Clone, uniffi::Record)]
pub struct Release {
    pub name: String,
    pub path: String,
    /// Detik sejak epoch (0 jika server tidak melaporkannya).
    pub created_at: i64,
    pub is_current: bool,
}

/// Pengaturan satu kali deploy.
#[derive(Debug, Clone, uniffi::Record)]
pub struct DeployConfig {
    /// Folder dasar di server, mis. `/srv/toko`.
    pub base_path: String,
    /// Folder lokal yang isinya diunggah.
    pub local_dir: String,
    /// Nama yang tidak ikut diunggah (mis. `.git`, `node_modules`).
    pub excludes: Vec<String>,
    /// Dijalankan di dalam folder rilis sebelum `current` dipindah.
    pub build_command: Option<String>,
    /// Dijalankan setelah `current` dipindah.
    pub restart_command: Option<String>,
    /// Kode keluar 0 = sehat; selain itu deploy dianggap gagal.
    pub health_command: Option<String>,
    /// Jumlah rilis terbaru yang disimpan (minimal 1).
    pub keep_releases: u32,
}

#[derive(Debug, Clone, uniffi::Record)]
pub struct DeployOutcome {
    pub release: String,
    pub previous: Option<String>,
    pub rolled_back: bool,
    /// Catatan langkah + keluaran perintah, siap ditampilkan.
    pub log: Vec<String>,
}

#[derive(uniffi::Object)]
pub struct Deployer {
    sftp: Arc<SftpClient>,
}

/// Jembatan progress upload ke progress deploy.
struct UploadSteps(Arc<dyn DeployProgress>);

impl TransferProgress for UploadSteps {
    fn on_progress(&self, done: u64, total: u64, current_file: String) {
        let pct = if total == 0 { 0 } else { done.saturating_mul(100) / total };
        let detail = if current_file.is_empty() {
            format!("{pct}%")
        } else {
            format!("{pct}%  {current_file}")
        };
        self.0.on_step("Unggah".into(), detail);
    }

    fn is_cancelled(&self) -> bool {
        self.0.is_cancelled()
    }
}

#[uniffi::export(async_runtime = "tokio")]
impl Deployer {
    /// Buka koneksi SSH/SFTP untuk deploy.
    #[uniffi::constructor]
    pub async fn connect(target: SshTarget) -> Result<Arc<Self>, CoreError> {
        Ok(Arc::new(Self { sftp: SftpClient::connect(target).await? }))
    }

    /// Daftar rilis, terbaru di atas.
    pub async fn releases(&self, base_path: String) -> Result<Vec<Release>, CoreError> {
        self.list_releases(&normalize(&base_path)).await
    }

    pub async fn deploy(&self, cfg: DeployConfig, progress: Arc<dyn DeployProgress>) -> Result<DeployOutcome, CoreError> {
        let base = normalize(&cfg.base_path);
        if base.is_empty() {
            return Err(err("folder dasar server belum diisi"));
        }
        if cfg.local_dir.trim().is_empty() {
            return Err(err("folder lokal belum dipilih"));
        }
        if progress.is_cancelled() {
            return Err(CoreError::Cancelled);
        }

        let mut log = Vec::new();
        let rel_root = format!("{base}/releases");
        self.run(&format!("mkdir -p {} {}", q(&rel_root), q(&base)), &progress, "Siapkan folder", &mut log).await?;

        let existing = self.list_releases(&base).await?;
        let name = unique_name(&existing, &release_name(now_ms()));
        let release_path = format!("{rel_root}/{name}");
        self.run(&format!("mkdir -p {}", q(&release_path)), &progress, "Rilis baru", &mut log).await?;

        progress.on_step("Unggah".into(), format!("{} → {}", cfg.local_dir, release_path));
        if let Err(e) = self
            .sftp
            .upload_into(cfg.local_dir.clone(), release_path.clone(), cfg.excludes.clone(), Arc::new(UploadSteps(progress.clone())))
            .await
        {
            let _ = self.sftp.remove(release_path.clone()).await;
            return Err(e);
        }

        if let Some(cmd) = non_empty(&cfg.build_command) {
            if let Err(e) = self
                .run(&format!("cd {} && {}", q(&release_path), cmd), &progress, "Bangun", &mut log)
                .await
            {
                let _ = self.sftp.remove(release_path.clone()).await;
                return Err(e);
            }
        }

        let previous = self.current_name(&base).await;
        self.run(
            &format!("ln -sfn {} {}", q(&release_path), q(&format!("{base}/current"))),
            &progress,
            "Pindah current",
            &mut log,
        )
        .await?;

        if let Err(e) = self.apply_and_check(&cfg, &progress, &mut log).await {
            let rolled = self.rollback_to(&base, previous.as_deref(), &cfg, &progress, &mut log).await;
            let reason = strip_prefix(&e.to_string());
            return Err(err(&if rolled {
                format!("{reason}; sudah dikembalikan ke rilis sebelumnya")
            } else {
                format!("{reason}; rollback tidak tersedia")
            }));
        }

        self.prune(&base, cfg.keep_releases, &name, &progress, &mut log).await;
        progress.on_step("Selesai".into(), format!("Rilis {name} aktif"));
        Ok(DeployOutcome { release: name, previous, rolled_back: false, log })
    }

    /// Aktifkan kembali rilis lama. Jika restart/health gagal, `current` dikembalikan.
    pub async fn rollback(
        &self,
        base_path: String,
        release: String,
        restart_command: Option<String>,
        health_command: Option<String>,
        progress: Arc<dyn DeployProgress>,
    ) -> Result<DeployOutcome, CoreError> {
        let base = normalize(&base_path);
        if base.is_empty() {
            return Err(err("folder dasar server belum diisi"));
        }
        let path = format!("{base}/releases/{release}");
        let mut log = Vec::new();
        self.run(&format!("test -d {}", q(&path)), &progress, "Periksa rilis", &mut log).await?;

        let previous = self.current_name(&base).await;
        self.run(
            &format!("ln -sfn {} {}", q(&path), q(&format!("{base}/current"))),
            &progress,
            "Pindah current",
            &mut log,
        )
        .await?;

        let cfg = DeployConfig {
            base_path: base.clone(),
            local_dir: String::new(),
            excludes: vec![],
            build_command: None,
            restart_command,
            health_command,
            keep_releases: 0,
        };
        if let Err(e) = self.apply_and_check(&cfg, &progress, &mut log).await {
            if previous.as_deref() != Some(release.as_str()) {
                self.rollback_to(&base, previous.as_deref(), &cfg, &progress, &mut log).await;
            }
            return Err(e);
        }

        progress.on_step("Selesai".into(), format!("Rilis {release} aktif kembali"));
        Ok(DeployOutcome { release, previous, rolled_back: false, log })
    }

    /// Tutup koneksi deploy.
    pub async fn close(&self) {
        self.sftp.close().await;
    }
}

impl Deployer {
    async fn apply_and_check(
        &self,
        cfg: &DeployConfig,
        progress: &Arc<dyn DeployProgress>,
        log: &mut Vec<String>,
    ) -> Result<(), CoreError> {
        if let Some(cmd) = non_empty(&cfg.restart_command) {
            self.run(cmd, progress, "Restart", log).await?;
        }
        if let Some(cmd) = non_empty(&cfg.health_command) {
            self.run(cmd, progress, "Health check", log).await?;
        }
        Ok(())
    }

    async fn rollback_to(
        &self,
        base: &str,
        previous: Option<&str>,
        cfg: &DeployConfig,
        progress: &Arc<dyn DeployProgress>,
        log: &mut Vec<String>,
    ) -> bool {
        let Some(prev) = previous else { return false };
        let path = format!("{base}/releases/{prev}");
        let switch = format!("ln -sfn {} {}", q(&path), q(&format!("{base}/current")));
        if self.run(&switch, progress, "Rollback", log).await.is_err() {
            return false;
        }
        if let Some(cmd) = non_empty(&cfg.restart_command) {
            let _ = self.run(cmd, progress, "Restart (rollback)", log).await;
        }
        true
    }

    /// Jalankan perintah; kode keluar selain 0 dianggap gagal dan pesannya diambil dari stderr.
    async fn run(
        &self,
        cmd: &str,
        progress: &Arc<dyn DeployProgress>,
        step: &str,
        log: &mut Vec<String>,
    ) -> Result<ExecResult, CoreError> {
        if progress.is_cancelled() {
            return Err(CoreError::Cancelled);
        }
        progress.on_step(step.to_string(), cmd.to_string());
        log.push(format!("$ {cmd}"));
        let r = self.sftp.exec(cmd.to_string(), 1800).await?;
        if r.status != 0 {
            let detail = last_line(&r.stderr)
                .or_else(|| last_line(&r.stdout))
                .unwrap_or_else(|| format!("kode keluar {}", r.status));
            log.push(detail.clone());
            return Err(CoreError::Deploy { msg: format!("{step}: {detail}") });
        }
        for line in r.stdout.lines().filter(|l| !l.trim().is_empty()).take(6) {
            log.push(line.to_string());
        }
        Ok(r)
    }

    async fn list_releases(&self, base: &str) -> Result<Vec<Release>, CoreError> {
        if base.is_empty() {
            return Ok(vec![]);
        }
        let rel_root = format!("{base}/releases");
        let entries = match self.sftp.list(rel_root).await {
            Ok(e) => e,
            // Folder releases belum ada: belum pernah deploy.
            Err(_) => return Ok(vec![]),
        };
        let current = self.current_name(base).await;
        let mut out: Vec<Release> = entries
            .into_iter()
            .filter(|e| e.is_dir)
            .map(|e| Release {
                is_current: current.as_deref() == Some(e.name.as_str()),
                name: e.name,
                path: e.path,
                created_at: e.modified,
            })
            .collect();
        out.sort_by(|a, b| b.name.cmp(&a.name));
        Ok(out)
    }

    /// Nama rilis yang sedang ditunjuk `current`.
    async fn current_name(&self, base: &str) -> Option<String> {
        let r = self
            .sftp
            .exec(format!("readlink {}", q(&format!("{base}/current"))), 20)
            .await
            .ok()?;
        if r.status != 0 {
            return None;
        }
        let target = r.stdout.trim();
        if target.is_empty() {
            None
        } else {
            target.rsplit('/').next().map(str::to_string)
        }
    }

    /// Simpan hanya `keep` rilis terbaru; rilis yang sedang aktif tidak pernah dihapus.
    async fn prune(&self, base: &str, keep: u32, active: &str, progress: &Arc<dyn DeployProgress>, log: &mut Vec<String>) {
        let keep = keep.max(1) as usize;
        let Ok(releases) = self.list_releases(base).await else { return };
        for r in releases.into_iter().skip(keep) {
            if r.is_current || r.name == active {
                continue;
            }
            progress.on_step("Bersihkan".into(), format!("hapus rilis lama {}", r.name));
            match self.sftp.remove(r.path).await {
                Ok(()) => log.push(format!("hapus rilis lama {}", r.name)),
                Err(e) => log.push(format!("gagal hapus {}: {e}", r.name)),
            }
        }
    }
}

fn normalize(base: &str) -> String {
    base.trim().trim_end_matches('/').to_string()
}

fn err(msg: &str) -> CoreError {
    CoreError::Deploy { msg: msg.to_string() }
}

fn strip_prefix(s: &str) -> String {
    s.strip_prefix("Deploy: ").unwrap_or(s).to_string()
}

fn non_empty(v: &Option<String>) -> Option<&str> {
    v.as_deref().map(str::trim).filter(|s| !s.is_empty())
}

/// Kutip satu argumen untuk shell POSIX.
fn q(s: &str) -> String {
    format!("'{}'", s.replace('\'', "'\\''"))
}

fn last_line(s: &str) -> Option<String> {
    s.lines().map(str::trim).filter(|l| !l.is_empty()).last().map(str::to_string)
}

fn unique_name(existing: &[Release], base: &str) -> String {
    if existing.iter().all(|r| r.name != base) {
        return base.to_string();
    }
    for i in 2..1000 {
        let candidate = format!("{base}-{i}");
        if existing.iter().all(|r| r.name != candidate) {
            return candidate;
        }
    }
    format!("{base}-{}", now_ms())
}

/// Nama rilis dari waktu UTC, mis. `20261121-120501` (urut secara leksikografis).
fn release_name(ms: i64) -> String {
    let secs = ms.div_euclid(1000);
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let (y, m, d) = civil_from_days(days);
    format!("{y:04}{m:02}{d:02}-{:02}{:02}{:02}", rem / 3600, (rem % 3600) / 60, rem % 60)
}

/// Hari sejak 1970-01-01 → (tahun, bulan, hari), algoritma Howard Hinnant.
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_name_is_utc_and_sortable() {
        assert_eq!(release_name(0), "19700101-000000");
        assert_eq!(release_name(1_735_689_600_000), "20250101-000000");
        assert_eq!(release_name(1_735_689_600_000 + 3_661_000), "20250101-010101");
    }

    #[test]
    fn release_name_handles_leap_day() {
        // 2024-02-29T12:34:56Z
        assert_eq!(release_name(1_709_210_096_000), "20240229-123456");
    }

    #[test]
    fn unique_name_avoids_collision() {
        let r = |n: &str| Release { name: n.into(), path: format!("/x/{n}"), created_at: 0, is_current: false };
        assert_eq!(unique_name(&[], "20261121-120501"), "20261121-120501");
        assert_eq!(
            unique_name(&[r("20261121-120501")], "20261121-120501"),
            "20261121-120501-2"
        );
        assert_eq!(
            unique_name(&[r("20261121-120501"), r("20261121-120501-2")], "20261121-120501"),
            "20261121-120501-3"
        );
    }

    #[test]
    fn shell_quoting_escapes_single_quotes() {
        assert_eq!(q("/srv/it's"), "'/srv/it'\\''s'");
    }
}
