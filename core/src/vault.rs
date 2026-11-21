use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use crate::crypto::{self, Key, SALT_LEN};
use crate::keys;
use crate::model::{DbConnection, Host, PortForward, Record, SessionLog, Snippet, SshKey, SyncOutcome, VaultData, now_ms};
use crate::CoreError;

/// Vault terbuka (sudah di-unlock). Semua perubahan langsung ditulis ke disk terenkripsi.
#[derive(uniffi::Object)]
pub struct Vault {
    path: PathBuf,
    key: Key,
    salt: [u8; SALT_LEN],
    data: Mutex<VaultData>,
}

#[uniffi::export]
pub fn vault_exists(path: String) -> bool {
    Path::new(&path).exists()
}

#[uniffi::export]
impl Vault {
    /// Buat vault baru dengan master password.
    #[uniffi::constructor]
    pub fn create(path: String, password: String) -> Result<Arc<Self>, CoreError> {
        if Path::new(&path).exists() {
            return Err(CoreError::AlreadyExists);
        }
        if password.chars().count() < 8 {
            return Err(CoreError::WeakPassword);
        }
        let salt = crypto::random::<SALT_LEN>();
        let vault = Self {
            path: path.into(),
            key: crypto::derive_key(&password, &salt),
            salt,
            data: Mutex::new(VaultData::default()),
        };
        vault.persist(&vault.data.lock().unwrap())?;
        Ok(Arc::new(vault))
    }

    /// Buka vault dengan master password.
    #[uniffi::constructor]
    pub fn unlock(path: String, password: String) -> Result<Arc<Self>, CoreError> {
        let blob = fs::read(&path)?;
        let salt = crypto::read_salt(&blob)?;
        let key = crypto::derive_key(&password, &salt);
        Self::open_with(path.into(), key, salt, &blob)
    }

    /// Buka vault dengan kunci mentah (disimpan di Keychain, dibuka dengan Touch ID).
    #[uniffi::constructor]
    pub fn unlock_with_key(path: String, key: Vec<u8>) -> Result<Arc<Self>, CoreError> {
        let key: Key = key.try_into().map_err(|_| CoreError::WrongPassword)?;
        let blob = fs::read(&path)?;
        let salt = crypto::read_salt(&blob)?;
        Self::open_with(path.into(), key, salt, &blob)
    }

    /// Kunci mentah untuk disimpan di Keychain (Touch ID). Jangan pernah ditulis ke file biasa.
    pub fn export_key(&self) -> Vec<u8> {
        self.key.to_vec()
    }

    /// Semua host aktif, urut berdasarkan label.
    pub fn hosts(&self) -> Vec<Host> {
        let mut list: Vec<Host> =
            self.data.lock().unwrap().hosts.iter().filter(|h| !h.deleted).cloned().collect();
        list.sort_by_key(|h| h.label.to_lowercase());
        list
    }

    /// Tambah host (jika `id` kosong) atau perbarui host yang ada.
    pub fn upsert_host(&self, mut host: Host) -> Result<Host, CoreError> {
        let mut data = self.data.lock().unwrap();
        host.updated_at = now_ms();
        host.deleted = false;
        if host.id.is_empty() {
            host.id = uuid::Uuid::new_v4().to_string();
            data.hosts.push(host.clone());
        } else {
            let slot = data.hosts.iter_mut().find(|h| h.id == host.id).ok_or(CoreError::NotFound)?;
            *slot = host.clone();
        }
        self.persist(&data)?;
        Ok(host)
    }

    /// Hapus host (tombstone, agar penghapusan ikut tersinkron).
    pub fn delete_host(&self, id: String) -> Result<(), CoreError> {
        let mut data = self.data.lock().unwrap();
        let h = data.hosts.iter_mut().find(|h| h.id == id).ok_or(CoreError::NotFound)?;
        h.deleted = true;
        h.updated_at = now_ms();
        self.persist(&data)
    }

    // ---- Keychain ----

    /// Semua SSH key aktif, urut berdasarkan label.
    pub fn keys(&self) -> Vec<SshKey> {
        let mut list: Vec<SshKey> =
            self.data.lock().unwrap().keys.iter().filter(|k| !k.deleted).cloned().collect();
        list.sort_by_key(|k| k.label.to_lowercase());
        list
    }

    /// Generate key Ed25519 baru dan simpan ke vault.
    pub fn generate_key(&self, label: String) -> Result<SshKey, CoreError> {
        let key = keys::generate_ed25519(&label)?;
        self.push_key(key)
    }

    /// Import private key OpenSSH (isi file, mis. dari `~/.ssh/id_ed25519`).
    pub fn import_key(&self, label: String, private_key: String, passphrase: Option<String>) -> Result<SshKey, CoreError> {
        let key = keys::import(&label, &private_key, passphrase)?;
        self.push_key(key)
    }

    pub fn rename_key(&self, id: String, label: String) -> Result<(), CoreError> {
        let mut data = self.data.lock().unwrap();
        let k = data.keys.iter_mut().find(|k| k.id == id).ok_or(CoreError::NotFound)?;
        k.label = label;
        k.updated_at = now_ms();
        self.persist(&data)
    }

    /// Hapus key (tombstone). Host yang memakai key ini dikembalikan ke auth password.
    pub fn delete_key(&self, id: String) -> Result<(), CoreError> {
        let mut data = self.data.lock().unwrap();
        let now = now_ms();
        let k = data.keys.iter_mut().find(|k| k.id == id).ok_or(CoreError::NotFound)?;
        k.deleted = true;
        k.updated_at = now;
        for h in data.hosts.iter_mut().filter(|h| h.key_id.as_deref() == Some(id.as_str())) {
            h.key_id = None;
            h.updated_at = now;
        }
        self.persist(&data)
    }

    // ---- Sync ----

    /// Isi file vault terenkripsi apa adanya — ini yang di-upload ke Google Drive.
    pub fn encrypted_blob(&self) -> Result<Vec<u8>, CoreError> {
        Ok(fs::read(&self.path)?)
    }

    /// Gabungkan salinan vault dari Google Drive ke vault lokal.
    ///
    /// Per record (host / key): versi dengan `updated_at` terbaru menang, termasuk tombstone
    /// penghapusan. Salinan Drive harus dibuat dengan master password yang sama (salt sama).
    pub fn merge_remote(&self, blob: Vec<u8>) -> Result<SyncOutcome, CoreError> {
        let salt = crypto::read_salt(&blob)?;
        if salt != self.salt {
            return Err(CoreError::KeyMismatch);
        }
        let plain = crypto::open(&self.key, &blob).map_err(|_| CoreError::KeyMismatch)?;
        let remote: VaultData = serde_json::from_slice(&plain)?;

        let mut data = self.data.lock().unwrap();
        let results = [
            merge_records(&mut data.hosts, remote.hosts),
            merge_records(&mut data.keys, remote.keys),
            merge_records(&mut data.snippets, remote.snippets),
            merge_records(&mut data.forwards, remote.forwards),
            merge_records(&mut data.logs, remote.logs),
            merge_records(&mut data.databases, remote.databases),
        ];
        let outcome = SyncOutcome {
            local_changed: results.iter().any(|r| r.0),
            remote_outdated: results.iter().any(|r| r.1),
        };
        if outcome.local_changed {
            self.persist(&data)?;
        }
        Ok(outcome)
    }

    pub fn account_email(&self) -> Option<String> {
        self.data.lock().unwrap().account_email.clone()
    }

    pub fn set_account_email(&self, email: Option<String>) -> Result<(), CoreError> {
        let mut data = self.data.lock().unwrap();
        data.account_email = email;
        self.persist(&data)
    }
}

/// Snippet, port forwarding, dan log sesi.
#[uniffi::export]
impl Vault {
    pub fn snippets(&self) -> Vec<Snippet> {
        let mut v = active(&self.data.lock().unwrap().snippets);
        v.sort_by_key(|s| s.label.to_lowercase());
        v
    }

    pub fn upsert_snippet(&self, snippet: Snippet) -> Result<Snippet, CoreError> {
        self.mutate(|d| upsert(&mut d.snippets, snippet))
    }

    pub fn delete_snippet(&self, id: String) -> Result<(), CoreError> {
        self.mutate(|d| remove(&mut d.snippets, &id))
    }

    pub fn forwards(&self) -> Vec<PortForward> {
        let mut v = active(&self.data.lock().unwrap().forwards);
        v.sort_by_key(|f| f.label.to_lowercase());
        v
    }

    pub fn upsert_forward(&self, forward: PortForward) -> Result<PortForward, CoreError> {
        self.mutate(|d| upsert(&mut d.forwards, forward))
    }

    pub fn delete_forward(&self, id: String) -> Result<(), CoreError> {
        self.mutate(|d| remove(&mut d.forwards, &id))
    }

    /// Riwayat sesi, terbaru di atas (maksimal 500 entri disimpan).
    pub fn logs(&self) -> Vec<SessionLog> {
        let mut v = active(&self.data.lock().unwrap().logs);
        v.sort_by_key(|l| std::cmp::Reverse(l.started_at));
        v
    }

    /// Catat sesi baru dimulai. Kembalikan id log untuk ditutup nanti.
    pub fn start_log(&self, host_id: String, host_label: String, address: String) -> Result<String, CoreError> {
        let log = SessionLog {
            id: String::new(), host_id, host_label, address,
            started_at: now_ms(), ended_at: None, exit_code: None, updated_at: 0, deleted: false,
        };
        self.mutate(|d| {
            let saved = upsert(&mut d.logs, log)?;
            // Batasi ukuran: buang log tertua (tombstone) jika lebih dari 500
            let mut alive: Vec<(i64, String)> =
                d.logs.iter().filter(|l| !l.deleted).map(|l| (l.started_at, l.id.clone())).collect();
            if alive.len() > 500 {
                alive.sort();
                for (_, id) in alive.iter().take(alive.len() - 500) {
                    remove(&mut d.logs, id)?;
                }
            }
            Ok(saved.id)
        })
    }

    pub fn finish_log(&self, id: String, exit_code: Option<i32>) -> Result<(), CoreError> {
        self.mutate(|d| {
            let l = d.logs.iter_mut().find(|l| l.id == id).ok_or(CoreError::NotFound)?;
            l.ended_at = Some(now_ms());
            l.exit_code = exit_code;
            l.touch(false);
            Ok(())
        })
    }

    pub fn databases(&self) -> Vec<DbConnection> {
        let mut v = active(&self.data.lock().unwrap().databases);
        v.sort_by_key(|d| d.label.to_lowercase());
        v
    }

    pub fn upsert_database(&self, conn: DbConnection) -> Result<DbConnection, CoreError> {
        self.mutate(|d| upsert(&mut d.databases, conn))
    }

    pub fn delete_database(&self, id: String) -> Result<(), CoreError> {
        self.mutate(|d| remove(&mut d.databases, &id))
    }

    pub fn clear_logs(&self) -> Result<(), CoreError> {
        self.mutate(|d| {
            for l in d.logs.iter_mut().filter(|l| !l.deleted) {
                l.touch(true);
            }
            Ok(())
        })
    }
}

fn active<T: Record>(list: &[T]) -> Vec<T>
where
    T: HasDeleted,
{
    list.iter().filter(|x| !x.is_deleted()).cloned().collect()
}

pub trait HasDeleted { fn is_deleted(&self) -> bool; }
macro_rules! has_deleted { ($($t:ty),*) => {$( impl HasDeleted for $t { fn is_deleted(&self) -> bool { self.deleted } } )*}; }
has_deleted!(Snippet, PortForward, SessionLog, DbConnection);

/// Tambah (id kosong) atau ganti record.
fn upsert<T: Record>(list: &mut Vec<T>, mut item: T) -> Result<T, CoreError> {
    item.touch(false);
    if item.id().is_empty() {
        item.set_id(uuid::Uuid::new_v4().to_string());
        list.push(item.clone());
    } else {
        let slot = list.iter_mut().find(|x| x.id() == item.id()).ok_or(CoreError::NotFound)?;
        *slot = item.clone();
    }
    Ok(item)
}

fn remove<T: Record>(list: &mut [T], id: &str) -> Result<(), CoreError> {
    list.iter_mut().find(|x| x.id() == id).ok_or(CoreError::NotFound)?.touch(true);
    Ok(())
}

impl Vault {
    /// Ubah data lalu langsung simpan terenkripsi.
    fn mutate<R>(&self, f: impl FnOnce(&mut VaultData) -> Result<R, CoreError>) -> Result<R, CoreError> {
        let mut data = self.data.lock().unwrap();
        let out = f(&mut data)?;
        self.persist(&data)?;
        Ok(out)
    }

    fn push_key(&self, key: SshKey) -> Result<SshKey, CoreError> {
        let mut data = self.data.lock().unwrap();
        if data.keys.iter().any(|k| !k.deleted && k.fingerprint == key.fingerprint) {
            return Err(CoreError::InvalidKey { msg: "key ini sudah ada di Keychain".into() });
        }
        data.keys.push(key.clone());
        self.persist(&data)?;
        Ok(key)
    }

    fn open_with(path: PathBuf, key: Key, salt: [u8; SALT_LEN], blob: &[u8]) -> Result<Arc<Self>, CoreError> {
        let plain = crypto::open(&key, blob)?;
        let data: VaultData = serde_json::from_slice(&plain)?;
        Ok(Arc::new(Self { path, key, salt, data: Mutex::new(data) }))
    }

    /// Tulis atomik: file sementara lalu rename.
    fn persist(&self, data: &VaultData) -> Result<(), CoreError> {
        if let Some(dir) = self.path.parent() {
            fs::create_dir_all(dir)?;
        }
        let blob = crypto::seal(&self.key, &self.salt, &serde_json::to_vec(data)?);
        let tmp = self.path.with_extension("tmp");
        fs::write(&tmp, blob)?;
        fs::rename(&tmp, &self.path)?;
        Ok(())
    }
}

/// Gabungkan `remote` ke `local` (last-write-wins per id).
/// Mengembalikan (lokal berubah, remote tertinggal).
fn merge_records<T: Record>(local: &mut Vec<T>, remote: Vec<T>) -> (bool, bool) {
    let remote_ts: std::collections::HashMap<String, i64> =
        remote.iter().map(|r| (r.id().to_string(), r.ts())).collect();
    let mut local_changed = false;
    for r in remote {
        match local.iter_mut().find(|l| l.id() == r.id()) {
            Some(l) if r.ts() > l.ts() => {
                *l = r;
                local_changed = true;
            }
            Some(_) => {}
            None => {
                local.push(r);
                local_changed = true;
            }
        }
    }
    // Remote tertinggal jika ada record lokal yang belum ada di remote atau lebih baru.
    let remote_outdated = local
        .iter()
        .any(|l| remote_ts.get(l.id()).is_none_or(|&t| l.ts() > t));
    (local_changed, remote_outdated)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::AuthMethod;

    fn tmp_path(name: &str) -> String {
        let p = std::env::temp_dir().join(format!("zevn-test-{name}-{}.zevn", uuid::Uuid::new_v4()));
        p.to_string_lossy().into_owned()
    }

    fn sample() -> Host {
        Host {
            id: String::new(),
            label: "prod".into(),
            hostname: "10.0.0.1".into(),
            group: None,
            port: 22,
            username: "root".into(),
            auth: AuthMethod::Password,
            password: Some("s3cret".into()),
            key_id: None,
            agent_forwarding: false,
            startup_snippet: None,
            jump_host_id: None,
            proxy: None,
            env: vec![],
            charset: "UTF-8".into(),
            terminal_theme: None,
            tags: vec!["production".into()],
            color: "#3fd0b4".into(),
            updated_at: 0,
            deleted: false,
        }
    }

    #[test]
    fn roundtrip_and_wrong_password() {
        let path = tmp_path("rt");
        let v = Vault::create(path.clone(), "rahasia123".into()).unwrap();
        let saved = v.upsert_host(sample()).unwrap();
        assert!(!saved.id.is_empty());

        let v2 = Vault::unlock(path.clone(), "rahasia123".into()).unwrap();
        assert_eq!(v2.hosts(), vec![saved.clone()]);

        assert!(matches!(Vault::unlock(path.clone(), "salah-salah".into()), Err(CoreError::WrongPassword)));

        let v3 = Vault::unlock_with_key(path.clone(), v2.export_key()).unwrap();
        v3.delete_host(saved.id).unwrap();
        assert!(Vault::unlock(path.clone(), "rahasia123".into()).unwrap().hosts().is_empty());
        fs::remove_file(path).ok();
    }

    #[test]
    fn keychain_generate_import_delete() {
        let path = tmp_path("keys");
        let v = Vault::create(path.clone(), "rahasia123".into()).unwrap();
        let k = v.generate_key("laptop".into()).unwrap();
        assert_eq!(k.key_type, "ssh-ed25519");
        assert!(k.public_key.starts_with("ssh-ed25519 "));
        assert!(k.fingerprint.starts_with("SHA256:"));

        // import key yang sama → ditolak sebagai duplikat
        assert!(v.import_key("dup".into(), k.private_key.clone(), None).is_err());
        assert!(v.import_key("x".into(), "bukan key".into(), None).is_err());

        let mut h = sample();
        h.auth = AuthMethod::Key;
        h.key_id = Some(k.id.clone());
        let h = v.upsert_host(h).unwrap();
        v.delete_key(k.id).unwrap();
        let v2 = Vault::unlock(path.clone(), "rahasia123".into()).unwrap();
        assert!(v2.keys().is_empty());
        assert_eq!(v2.hosts().into_iter().find(|x| x.id == h.id).unwrap().key_id, None);
        fs::remove_file(path).ok();
    }

    #[test]
    fn sync_merge_last_write_wins() {
        let a_path = tmp_path("sync-a");
        let b_path = tmp_path("sync-b");
        let a = Vault::create(a_path.clone(), "rahasia123".into()).unwrap();
        let h1 = a.upsert_host(sample()).unwrap();

        // Perangkat B memulihkan vault dari "Drive" (salinan file A)
        fs::write(&b_path, a.encrypted_blob().unwrap()).unwrap();
        let b = Vault::unlock(b_path.clone(), "rahasia123".into()).unwrap();
        assert_eq!(b.merge_remote(a.encrypted_blob().unwrap()).unwrap(),
                   SyncOutcome { local_changed: false, remote_outdated: false });

        // B mengubah host & menambah host baru; A menambah host lain
        std::thread::sleep(std::time::Duration::from_millis(3));
        let mut edited = h1.clone();
        edited.label = "prod-baru".into();
        b.upsert_host(edited).unwrap();
        let mut extra = sample();
        extra.label = "dari-b".into();
        b.upsert_host(extra).unwrap();
        let mut other = sample();
        other.label = "dari-a".into();
        a.upsert_host(other).unwrap();

        // A menerima perubahan B
        let out = a.merge_remote(b.encrypted_blob().unwrap()).unwrap();
        assert!(out.local_changed && out.remote_outdated);
        let labels: Vec<String> = a.hosts().into_iter().map(|h| h.label).collect();
        assert_eq!(labels, vec!["dari-a", "dari-b", "prod-baru"]);

        // B menerima hasil gabungan A → identik, tidak perlu upload lagi
        let out = b.merge_remote(a.encrypted_blob().unwrap()).unwrap();
        assert_eq!(out, SyncOutcome { local_changed: true, remote_outdated: false });
        assert_eq!(b.hosts(), a.hosts());

        // Penghapusan ikut tersinkron
        let gone = a.hosts()[0].id.clone();
        a.delete_host(gone.clone()).unwrap();
        b.merge_remote(a.encrypted_blob().unwrap()).unwrap();
        assert!(b.hosts().iter().all(|h| h.id != gone));

        // Vault dengan password lain ditolak
        let c_path = tmp_path("sync-c");
        let c = Vault::create(c_path.clone(), "passwordlain".into()).unwrap();
        assert!(matches!(a.merge_remote(c.encrypted_blob().unwrap()), Err(CoreError::KeyMismatch)));
        for p in [a_path, b_path, c_path] { fs::remove_file(p).ok(); }
    }

    #[test]
    fn snippets_forwards_logs() {
        use crate::model::ForwardKind;
        let path = tmp_path("extras");
        let v = Vault::create(path.clone(), "rahasia123".into()).unwrap();
        let s = v.upsert_snippet(Snippet { id: String::new(), label: "disk".into(), command: "df -h".into(), updated_at: 0, deleted: false }).unwrap();
        let f = v.upsert_forward(PortForward {
            id: String::new(), label: "db".into(), host_id: "h".into(), kind: ForwardKind::Local,
            bind_address: "127.0.0.1".into(), bind_port: 5433, dest_host: "localhost".into(), dest_port: 5432,
            updated_at: 0, deleted: false,
        }).unwrap();
        let log = v.start_log("h".into(), "prod".into(), "root@x".into()).unwrap();
        v.finish_log(log.clone(), Some(0)).unwrap();

        let v2 = Vault::unlock(path.clone(), "rahasia123".into()).unwrap();
        assert_eq!(v2.snippets(), vec![s.clone()]);
        assert_eq!(v2.forwards(), vec![f]);
        assert_eq!(v2.logs()[0].exit_code, Some(0));
        v2.delete_snippet(s.id).unwrap();
        v2.clear_logs().unwrap();
        assert!(v2.snippets().is_empty() && v2.logs().is_empty());
        fs::remove_file(path).ok();
    }

    #[test]
    fn rejects_weak_password_and_existing() {
        let path = tmp_path("weak");
        assert!(matches!(Vault::create(path.clone(), "123".into()), Err(CoreError::WeakPassword)));
        Vault::create(path.clone(), "cukupkuat".into()).unwrap();
        assert!(matches!(Vault::create(path.clone(), "cukupkuat".into()), Err(CoreError::AlreadyExists)));
        fs::remove_file(path).ok();
    }
}
