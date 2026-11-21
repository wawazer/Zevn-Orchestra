//! Keychain SSH: generate Ed25519, import private key OpenSSH, fingerprint.

use ssh_key::private::Ed25519Keypair;
use ssh_key::{HashAlg, LineEnding, PrivateKey};

use crate::crypto;
use crate::model::{SshKey, now_ms};
use crate::CoreError;

/// Buat pasangan key Ed25519 baru.
pub fn generate_ed25519(label: &str) -> Result<SshKey, CoreError> {
    let seed: [u8; 32] = crypto::random();
    let mut private = PrivateKey::from(Ed25519Keypair::from_seed(&seed));
    private.set_comment(comment_for(label));
    to_record(label, &private, None)
}

/// Import private key format OpenSSH (`-----BEGIN OPENSSH PRIVATE KEY-----`).
/// Key yang ber-passphrase tetap bisa diimport; passphrase disimpan di vault.
pub fn import(label: &str, private_key: &str, passphrase: Option<String>) -> Result<SshKey, CoreError> {
    let private = PrivateKey::from_openssh(private_key.trim())
        .map_err(|e| CoreError::InvalidKey { msg: e.to_string() })?;
    if private.is_encrypted() && passphrase.as_deref().unwrap_or("").is_empty() {
        return Err(CoreError::InvalidKey { msg: "key ini memakai passphrase, isi passphrase-nya".into() });
    }
    to_record(label, &private, passphrase.filter(|p| !p.is_empty()))
}

fn comment_for(label: &str) -> String {
    let clean: String = label.chars().filter(|c| c.is_ascii_graphic()).collect();
    if clean.is_empty() { "zevnshell".into() } else { format!("{clean}@zevnshell") }
}

fn to_record(label: &str, private: &PrivateKey, passphrase: Option<String>) -> Result<SshKey, CoreError> {
    let public = private.public_key();
    let pem = private
        .to_openssh(LineEnding::LF)
        .map_err(|e| CoreError::InvalidKey { msg: e.to_string() })?;
    let public_key = public.to_openssh().map_err(|e| CoreError::InvalidKey { msg: e.to_string() })?;
    Ok(SshKey {
        id: uuid::Uuid::new_v4().to_string(),
        label: if label.trim().is_empty() { "SSH key".into() } else { label.trim().into() },
        key_type: public.algorithm().as_str().to_string(),
        public_key,
        private_key: pem.to_string(),
        passphrase,
        fingerprint: public.fingerprint(HashAlg::Sha256).to_string(),
        updated_at: now_ms(),
        deleted: false,
    })
}
