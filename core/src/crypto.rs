//! Format file `vault.zevn`:
//!
//! ```text
//! "ZEVN" | ver:u8 | salt:16 | nonce:24 | ciphertext (XChaCha20-Poly1305, AAD = header)
//! ```
//! Kunci 32 byte diturunkan dari master password dengan Argon2id.

use argon2::{Algorithm, Argon2, Params, Version};
use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{XChaCha20Poly1305, XNonce};

use crate::CoreError;

const MAGIC: &[u8; 4] = b"ZEVN";
const VERSION: u8 = 1;
pub const SALT_LEN: usize = 16;
const NONCE_LEN: usize = 24;
const HEADER_LEN: usize = 4 + 1 + SALT_LEN;

pub type Key = [u8; 32];

pub fn random<const N: usize>() -> [u8; N] {
    let mut b = [0u8; N];
    getrandom::fill(&mut b).expect("OS RNG tidak tersedia");
    b
}

pub fn derive_key(password: &str, salt: &[u8; SALT_LEN]) -> Key {
    // 64 MiB, 3 iterasi — sekitar 0,2–0,5 detik di laptop modern.
    let params = Params::new(64 * 1024, 3, 1, Some(32)).expect("parameter argon2 valid");
    let mut key = [0u8; 32];
    Argon2::new(Algorithm::Argon2id, Version::V0x13, params)
        .hash_password_into(password.as_bytes(), salt, &mut key)
        .expect("argon2 gagal");
    key
}

fn header(salt: &[u8; SALT_LEN]) -> Vec<u8> {
    let mut h = Vec::with_capacity(HEADER_LEN);
    h.extend_from_slice(MAGIC);
    h.push(VERSION);
    h.extend_from_slice(salt);
    h
}

pub fn seal(key: &Key, salt: &[u8; SALT_LEN], plaintext: &[u8]) -> Vec<u8> {
    let cipher = XChaCha20Poly1305::new(key.into());
    let nonce_bytes: [u8; NONCE_LEN] = random();
    let aad = header(salt);
    let ct = cipher
        .encrypt(&XNonce::from(nonce_bytes), Payload { msg: plaintext, aad: &aad })
        .expect("enkripsi gagal");
    let mut out = aad;
    out.extend_from_slice(&nonce_bytes);
    out.extend_from_slice(&ct);
    out
}

/// Ambil salt dari file tanpa membuka isinya.
pub fn read_salt(blob: &[u8]) -> Result<[u8; SALT_LEN], CoreError> {
    if blob.len() < HEADER_LEN + NONCE_LEN || &blob[..4] != MAGIC {
        return Err(CoreError::Corrupt { msg: "bukan file vault Zevn Shell".into() });
    }
    if blob[4] != VERSION {
        return Err(CoreError::Corrupt { msg: format!("versi vault {} tidak didukung", blob[4]) });
    }
    Ok(blob[5..HEADER_LEN].try_into().unwrap())
}

pub fn open(key: &Key, blob: &[u8]) -> Result<Vec<u8>, CoreError> {
    read_salt(blob)?;
    let (aad, rest) = blob.split_at(HEADER_LEN);
    let (nonce, ct) = rest.split_at(NONCE_LEN);
    let nonce: [u8; NONCE_LEN] = nonce.try_into().expect("panjang nonce sudah dicek");
    XChaCha20Poly1305::new(key.into())
        .decrypt(&XNonce::from(nonce), Payload { msg: ct, aad })
        .map_err(|_| CoreError::WrongPassword)
}
