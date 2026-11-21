//! Log service langsung dari server: buka satu kanal SSH, jalankan perintah
//! (mis. `journalctl -u toko -f`), dan alirkan baris keluarannya ke UI.
//!
//! Berbeda dengan [`crate::sftp::SftpClient::exec`], perintah di sini boleh
//! berjalan terus: `start` menunggu sampai perintah selesai atau `stop` dipanggil.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use tokio::sync::{watch, Mutex};

use crate::ssh::{self, Client, SshTarget};
use crate::CoreError;

/// Panjang maksimum satu baris yang dikirim ke UI (sisanya dipotong).
const MAX_LINE_CHARS: usize = 4000;
/// Batas buffer bila server mengirim keluaran tanpa newline sama sekali.
const MAX_BUFFER_BYTES: usize = 64 * 1024;

/// Penerima baris log — diimplementasikan di C#.
#[uniffi::export(with_foreign)]
pub trait LogSink: Send + Sync {
    /// Satu baris log tanpa newline di akhir.
    fn on_line(&self, line: String);
    /// Perintah berhenti; `status` adalah kode keluar (-1 jika tidak dilaporkan).
    fn on_end(&self, status: i32);
}

/// Satu sesi log ke server. Boleh dipakai ulang: `start` setelah `stop` akan
/// membuka kanal baru pada koneksi yang sama.
#[derive(uniffi::Object)]
pub struct LogStream {
    handle: Mutex<Option<russh::client::Handle<Client>>>,
    stop: Mutex<Option<watch::Sender<bool>>>,
    running: AtomicBool,
}

fn err(msg: impl Into<String>) -> CoreError {
    CoreError::Log { msg: msg.into() }
}

#[uniffi::export(async_runtime = "tokio")]
impl LogStream {
    /// Buka koneksi SSH untuk sesi log.
    #[uniffi::constructor]
    pub async fn connect(target: SshTarget) -> Result<Arc<Self>, CoreError> {
        let handle = ssh::connect(&target).await?;
        Ok(Arc::new(Self {
            handle: Mutex::new(Some(handle)),
            stop: Mutex::new(None),
            running: AtomicBool::new(false),
        }))
    }

    /// Jalankan `command` dan alirkan keluarannya ke `sink` sampai perintah
    /// selesai atau [`LogStream::stop`] dipanggil.
    pub async fn start(&self, command: String, sink: Arc<dyn LogSink>) -> Result<(), CoreError> {
        if command.trim().is_empty() {
            return Err(err("perintah log belum diisi"));
        }
        if self.running.swap(true, Ordering::SeqCst) {
            return Err(err("log sudah berjalan"));
        }

        let (tx, mut rx) = watch::channel(false);
        *self.stop.lock().await = Some(tx);

        let result = self.run(command.trim(), &sink, &mut rx).await;

        *self.stop.lock().await = None;
        self.running.store(false, Ordering::SeqCst);

        match result {
            Ok(status) => {
                sink.on_end(status);
                Ok(())
            }
            Err(e) => {
                sink.on_end(-1);
                Err(e)
            }
        }
    }

    /// Hentikan perintah yang sedang berjalan (jika ada).
    pub async fn stop(&self) {
        if let Some(tx) = self.stop.lock().await.as_ref() {
            let _ = tx.send(true);
        }
    }

    /// Tutup kanal log dan koneksi SSH.
    pub async fn close(&self) {
        self.stop().await;
        if let Some(h) = self.handle.lock().await.take() {
            let _ = h.disconnect(russh::Disconnect::ByApplication, "", "en").await;
        }
    }
}

impl LogStream {
    async fn run(
        &self,
        command: &str,
        sink: &Arc<dyn LogSink>,
        stop: &mut watch::Receiver<bool>,
    ) -> Result<i32, CoreError> {
        let mut channel = {
            let guard = self.handle.lock().await;
            let h = guard.as_ref().ok_or_else(|| err("koneksi sudah ditutup"))?;
            h.channel_open_session()
                .await
                .map_err(|e| CoreError::Ssh { msg: e.to_string() })?
        };
        channel
            .exec(true, command.to_string())
            .await
            .map_err(|e| CoreError::Ssh { msg: e.to_string() })?;

        let mut out = LineBuffer::default();
        let mut errs = LineBuffer::default();
        let mut status = -1i32;
        let mut stopped = false;

        loop {
            tokio::select! {
                changed = stop.changed() => {
                    // Pengirim dihapus (start selesai) atau stop diminta.
                    if changed.is_err() || *stop.borrow() {
                        stopped = true;
                        break;
                    }
                }
                msg = channel.wait() => {
                    let Some(msg) = msg else { break };
                    match msg {
                        russh::ChannelMsg::Data { data } => out.push(&data, |l| sink.on_line(l)),
                        russh::ChannelMsg::ExtendedData { data, ext } if ext == 1 => {
                            errs.push(&data, |l| sink.on_line(l))
                        }
                        russh::ChannelMsg::ExitStatus { exit_status } => status = exit_status as i32,
                        _ => {}
                    }
                }
            }
        }

        out.flush(|l| sink.on_line(l));
        errs.flush(|l| sink.on_line(l));
        if stopped {
            status = -1;
        }
        let _ = channel.close().await;
        Ok(status)
    }
}

/// Menyusun potongan byte menjadi baris utuh. Chunk SSH bisa memotong di
/// tengah karakter UTF-8 atau di tengah baris, jadi konversi dilakukan setelah
/// baris lengkap terkumpul.
#[derive(Default)]
struct LineBuffer {
    buf: Vec<u8>,
}

impl LineBuffer {
    fn push(&mut self, data: &[u8], mut emit: impl FnMut(String)) {
        self.buf.extend_from_slice(data);
        self.drain(&mut emit);
    }

    fn drain(&mut self, emit: &mut impl FnMut(String)) {
        while let Some(pos) = self.buf.iter().position(|b| *b == b'\n') {
            let mut line: Vec<u8> = self.buf.drain(..=pos).collect();
            line.pop(); // newline
            emit(clean(&line));
        }
        // Tanpa newline sama sekali: jangan biarkan buffer tumbuh tanpa batas.
        if self.buf.len() > MAX_BUFFER_BYTES {
            let line = std::mem::take(&mut self.buf);
            emit(clean(&line));
        }
    }

    fn flush(&mut self, mut emit: impl FnMut(String)) {
        if self.buf.is_empty() {
            return;
        }
        let line = std::mem::take(&mut self.buf);
        emit(clean(&line));
    }
}

fn clean(bytes: &[u8]) -> String {
    let text = String::from_utf8_lossy(bytes);
    let text = text.trim_end_matches('\r');
    if text.chars().count() <= MAX_LINE_CHARS {
        return text.to_string();
    }
    let mut out: String = text.chars().take(MAX_LINE_CHARS).collect();
    out.push('…');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn take(buf: &mut LineBuffer) -> Vec<String> {
        let mut lines = vec![];
        buf.flush(|l| lines.push(l));
        lines
    }

    #[test]
    fn splits_lines_across_chunks() {
        let mut b = LineBuffer::default();
        let mut lines = vec![];
        b.push(b"halo du", |l| lines.push(l));
        b.push(b"nia\ndua\r\n", |l| lines.push(l));
        assert_eq!(lines, vec!["halo dunia", "dua"]);
        assert!(take(&mut b).is_empty());
    }

    #[test]
    fn keeps_incomplete_line_until_flush() {
        let mut b = LineBuffer::default();
        let mut lines = vec![];
        b.push(b"setengah", |l| lines.push(l));
        assert!(lines.is_empty());
        assert_eq!(take(&mut b), vec!["setengah"]);
    }

    #[test]
    fn utf8_split_across_chunks_survives() {
        let mut b = LineBuffer::default();
        let mut lines = vec![];
        let text = "log — selesai\n";
        let bytes = text.as_bytes();
        b.push(&bytes[..5], |l| lines.push(l)); // memotong karakter "—"
        b.push(&bytes[5..], |l| lines.push(l));
        assert_eq!(lines, vec!["log — selesai"]);
    }

    #[test]
    fn caps_very_long_line() {
        let mut b = LineBuffer::default();
        let mut lines = vec![];
        let long = "x".repeat(MAX_LINE_CHARS + 10);
        b.push(format!("{long}\n").as_bytes(), |l| lines.push(l));
        assert_eq!(lines.len(), 1);
        assert_eq!(lines[0].chars().count(), MAX_LINE_CHARS + 1);
        assert!(lines[0].ends_with('…'));
    }

    #[test]
    fn flushes_line_without_newline_when_buffer_too_big() {
        let mut b = LineBuffer::default();
        let mut lines = vec![];
        b.push(&vec![b'a'; MAX_BUFFER_BYTES + 1], |l| lines.push(l));
        assert_eq!(lines.len(), 1);
        // Buffer dipotong lebih dulu, lalu barisnya tetap dibatasi MAX_LINE_CHARS.
        assert_eq!(lines[0].chars().count(), MAX_LINE_CHARS + 1);
        assert!(lines[0].ends_with('…'));
    }
}
