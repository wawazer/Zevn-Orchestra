//! Tes integrasi Live Logs terhadap server SSH russh di loopback.
//!
//! Mesin pengembangan tidak selalu punya sshd, Docker, atau WSL, jadi server
//! dibuat di dalam proses tes. Yang diuji tetap jalur sungguhan: autentikasi,
//! kanal `exec`, potongan stdout/stderr, exit status, `stop`, dan `close`.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use russh::keys::ssh_key::{PrivateKey, private::Ed25519Keypair};
use russh::server::{Auth, ChannelOpenHandle, Msg, Server as _, Session};
use russh::{Channel, ChannelId};
use tokio::net::TcpListener;
use zevn_core::{CoreError, LogSink, LogStream, SshTarget};

#[derive(Default)]
struct Sink {
    lines: Mutex<Vec<String>>,
    end: Mutex<Option<i32>>,
}

impl LogSink for Sink {
    fn on_line(&self, line: String) {
        self.lines.lock().unwrap().push(line);
    }
    fn on_end(&self, status: i32) {
        *self.end.lock().unwrap() = Some(status);
    }
}

impl Sink {
    fn lines(&self) -> Vec<String> {
        self.lines.lock().unwrap().clone()
    }
    fn end(&self) -> Option<i32> {
        *self.end.lock().unwrap()
    }
}

#[derive(Clone)]
struct TestServer;

impl russh::server::Server for TestServer {
    type Handler = TestServer;
    fn new_client(&mut self, _: Option<std::net::SocketAddr>) -> Self {
        TestServer
    }
}

impl russh::server::Handler for TestServer {
    type Error = russh::Error;

    async fn auth_password(&mut self, _user: &str, _password: &str) -> Result<Auth, Self::Error> {
        Ok(Auth::Accept)
    }

    async fn channel_open_session(
        &mut self,
        _channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        reply.accept().await;
        Ok(())
    }

    async fn exec_request(
        &mut self,
        channel: ChannelId,
        data: &[u8],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        let command = String::from_utf8_lossy(data).trim().to_string();
        let _ = session.channel_success(channel);
        let handle = session.handle();
        tokio::spawn(async move {
            match command.as_str() {
                "stream" => {
                    // Potongan pertama memotong baris; potongan berikutnya memotong
                    // karakter UTF-8 "—" di tengah.
                    let _ = handle.data(channel, b"baris sa".to_vec()).await;
                    let _ = handle.data(channel, b"tu\r\nbaris dua\nlog \xe2\x80".to_vec()).await;
                    let _ = handle.data(channel, b"\x94 selesai\n".to_vec()).await;
                    let _ = handle
                        .extended_data(channel, 1, b"galat: berkas hilang\n".to_vec())
                        .await;
                    let _ = handle.exit_status_request(channel, 3).await;
                    let _ = handle.eof(channel).await;
                    let _ = handle.close(channel).await;
                }
                "hang" => {
                    for i in 0u32.. {
                        if handle.data(channel, format!("tick {i}\n").into_bytes()).await.is_err() {
                            break;
                        }
                        tokio::time::sleep(Duration::from_millis(20)).await;
                    }
                }
                other => {
                    let _ = handle.data(channel, format!("tidak dikenal: {other}\n").into_bytes()).await;
                    let _ = handle.exit_status_request(channel, 127).await;
                    let _ = handle.eof(channel).await;
                    let _ = handle.close(channel).await;
                }
            }
        });
        Ok(())
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn log_stream_loopback() {
    // known_hosts tes ditulis ke folder sementara, bukan ~/.ssh milik pengguna.
    let home = std::env::temp_dir().join(format!("zevn-log-test-{}", std::process::id()));
    std::fs::create_dir_all(&home).unwrap();
    unsafe { std::env::set_var("HOME", &home) };

    let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let host_key = PrivateKey::from(Ed25519Keypair::from_seed(&[7u8; 32]));
    let config = Arc::new(russh::server::Config {
        keys: vec![host_key],
        auth_rejection_time: Duration::ZERO,
        auth_rejection_time_initial: Some(Duration::ZERO),
        inactivity_timeout: Some(Duration::from_secs(30)),
        ..Default::default()
    });
    let mut server = TestServer;
    let running = server.run_on_socket(config, &listener);
    let server_handle = running.handle();

    let body = async {
        let target = SshTarget {
            hostname: "127.0.0.1".into(),
            port,
            username: "tester".into(),
            password: Some("rahasia".into()),
            private_key: None,
            passphrase: None,
            use_agent: false,
            proxy: None,
        };
        let stream = LogStream::connect(target).await.expect("connect");

        // Host baru dicatat ke known_hosts di HOME sementara.
        let known =
            std::fs::read_to_string(home.join(".ssh").join("known_hosts")).unwrap_or_default();
        assert!(known.contains(&format!("[127.0.0.1]:{port}")), "known_hosts: {known:?}");

        // Perintah kosong ditolak sebelum membuka kanal.
        let empty = Arc::new(Sink::default());
        assert!(matches!(
            stream.start("   ".into(), empty).await,
            Err(CoreError::Log { .. })
        ));

        // Baris terpotong, CRLF, UTF-8 terpotong, stderr, dan exit status.
        let sink = Arc::new(Sink::default());
        stream.start("stream".into(), sink.clone()).await.expect("start");
        assert_eq!(
            sink.lines(),
            vec!["baris satu", "baris dua", "log — selesai", "galat: berkas hilang"]
        );
        assert_eq!(sink.end(), Some(3));

        // stop() saat perintah masih mengalir: start kembali dengan status -1.
        let hang = Arc::new(Sink::default());
        let run = {
            let stream = stream.clone();
            let sink = hang.clone();
            tokio::spawn(async move { stream.start("hang".into(), sink).await })
        };
        for _ in 0..100 {
            if !hang.lines().is_empty() {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(!hang.lines().is_empty(), "server tidak sempat mengirim baris");
        stream.stop().await;
        tokio::time::timeout(Duration::from_secs(5), run)
            .await
            .expect("stop tidak menghentikan start")
            .unwrap()
            .expect("start setelah stop");
        assert_eq!(hang.end(), Some(-1));

        // Koneksi boleh dipakai ulang untuk perintah berikutnya.
        let again = Arc::new(Sink::default());
        stream.start("stream".into(), again.clone()).await.expect("start ulang");
        assert_eq!(again.end(), Some(3));

        // Setelah close, kanal tidak bisa dibuka lagi.
        stream.close().await;
        assert!(matches!(
            stream.start("stream".into(), Arc::new(Sink::default())).await,
            Err(CoreError::Log { msg }) if msg.contains("ditutup")
        ));

        server_handle.shutdown("tes selesai".to_string());
    };

    let (server_result, ()) = tokio::join!(running, body);
    server_result.expect("server SSH tes");
    let _ = std::fs::remove_dir_all(&home);
}
