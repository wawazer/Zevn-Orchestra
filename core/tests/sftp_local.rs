//! Tes integrasi SFTP terhadap sshd lokal. Dijalankan lewat scripts/test-sftp.sh
//! (butuh env ZEVN_TEST_PORT, ZEVN_TEST_KEY, ZEVN_TEST_ROOT).

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;

use zevn_core::{HostProxy, ProxyKind, SftpClient, SshTarget, TransferProgress};

struct P { last: AtomicU64, cancel: AtomicBool }
impl TransferProgress for P {
    fn on_progress(&self, done: u64, _total: u64, _f: String) { self.last.store(done, Ordering::SeqCst); }
    fn is_cancelled(&self) -> bool { self.cancel.load(Ordering::SeqCst) }
}

#[tokio::test]
async fn sftp_roundtrip() {
    let Ok(port) = std::env::var("ZEVN_TEST_PORT") else { eprintln!("skip: ZEVN_TEST_PORT tidak di-set"); return };
    let key = std::fs::read_to_string(std::env::var("ZEVN_TEST_KEY").unwrap()).unwrap();
    let root = std::env::var("ZEVN_TEST_ROOT").unwrap();
    let target = SshTarget {
        hostname: "127.0.0.1".into(), port: port.parse().unwrap(), username: std::env::var("USER").unwrap(),
        password: None, private_key: Some(key), passphrase: None, use_agent: false, proxy: None,
    };

    // Password salah & tanpa key → AuthFailed
    let mut bad = target.clone();
    bad.private_key = None; bad.password = Some("salah".into());
    assert!(matches!(SftpClient::connect(bad).await, Err(zevn_core::CoreError::AuthFailed)));

    // Lewat proxy SOCKS5 & HTTP (scripts/test-proxy.py), termasuk login proxy yang salah
    if let Ok(pp) = std::env::var("ZEVN_TEST_PROXY_PORTS") {
        let (socks, http) = pp.split_once(',').unwrap();
        for (kind, port) in [(ProxyKind::Socks5, socks), (ProxyKind::Http, http)] {
            let mut via = target.clone();
            via.proxy = Some(HostProxy { kind, host: "127.0.0.1".into(), port: port.parse().unwrap(),
                                         username: Some("zevn".into()), password: Some("proxy-pw".into()) });
            let c = SftpClient::connect(via.clone()).await.unwrap_or_else(|e| panic!("{kind:?}: {e}"));
            assert!(!c.home().await.unwrap().is_empty());
            via.proxy.as_mut().unwrap().password = Some("salah".into());
            match SftpClient::connect(via).await {
                Err(zevn_core::CoreError::Proxy { msg }) => assert!(msg.contains("login") || msg.contains("ditolak"), "{msg}"),
                other => panic!("{kind:?}: harusnya error proxy, dapat {:?}", other.map(|_| ())),
            }
        }
        let log = std::fs::read_to_string(std::env::var("ZEVN_TEST_PROXY_LOG").unwrap()).unwrap();
        eprintln!("proxy ok:\n{log}");
        assert!(log.contains(&format!("socks 127.0.0.1:{port}")) && log.contains(&format!("http 127.0.0.1:{port}")), "{log}");
    }

    // Panel Info server: info lengkap + 2 sampel grafik lewat satu koneksi
    let mon = zevn_core::ServerMonitor::connect(target.clone()).await.expect("monitor");
    let info = mon.info().await.expect("info");
    assert!(!info.os.is_empty() && !info.kernel.is_empty() && info.mem_total_bytes > 0 && !info.disks.is_empty(), "{info:?}");
    let s1 = mon.sample().await.expect("sample");
    let s2 = mon.sample().await.expect("sample 2");
    assert!(s1.mem_total_bytes > 0 && s2.cpu_percent >= 0.0 && s2.net_rx_bytes >= s1.net_rx_bytes, "{s1:?} {s2:?}");
    eprintln!("info server: {} · {} · {} core · RAM {} · disk {}", info.os, info.cpu_model, info.cpu_cores, info.mem_total_bytes, info.disks.len());
    mon.close().await;

    let c = SftpClient::connect(target).await.expect("connect");
    assert!(!c.home().await.unwrap().is_empty());

    // Siapkan folder lokal berisi file kecil + file 3 MB
    let local = format!("{root}/local/proyek");
    std::fs::create_dir_all(format!("{local}/sub")).unwrap();
    std::fs::write(format!("{local}/halo.txt"), "halo zevn").unwrap();
    std::fs::write(format!("{local}/sub/besar.bin"), vec![7u8; 3 * 1024 * 1024]).unwrap();

    let remote = format!("{root}/remote");
    c.mkdir(remote.clone()).await.unwrap();
    let p = Arc::new(P { last: AtomicU64::new(0), cancel: AtomicBool::new(false) });
    c.upload(local.clone(), remote.clone(), p.clone()).await.unwrap();
    assert_eq!(p.last.load(Ordering::SeqCst), 3 * 1024 * 1024 + 9);

    let names: Vec<String> = c.list(format!("{remote}/proyek")).await.unwrap().into_iter().map(|e| e.name).collect();
    assert_eq!(names, vec!["sub", "halo.txt"]); // folder dulu

    c.rename(format!("{remote}/proyek/halo.txt"), format!("{remote}/proyek/hai.txt")).await.unwrap();
    c.chmod(format!("{remote}/proyek/hai.txt"), 0o600).await.unwrap();
    let e = c.list(format!("{remote}/proyek")).await.unwrap().into_iter().find(|e| e.name == "hai.txt").unwrap();
    assert_eq!(e.permissions, 0o600);

    // Download folder → isi identik
    let dl = format!("{root}/dl"); std::fs::create_dir_all(&dl).unwrap();
    c.download(format!("{remote}/proyek"), dl.clone(), p.clone()).await.unwrap();
    assert_eq!(std::fs::read_to_string(format!("{dl}/proyek/hai.txt")).unwrap(), "halo zevn");
    assert_eq!(std::fs::metadata(format!("{dl}/proyek/sub/besar.bin")).unwrap().len(), 3 * 1024 * 1024);

    // Batal di tengah jalan
    let cancel = Arc::new(P { last: AtomicU64::new(0), cancel: AtomicBool::new(true) });
    assert!(matches!(c.download(format!("{remote}/proyek/sub/besar.bin"), dl.clone(), cancel).await, Err(zevn_core::CoreError::Cancelled)));

    c.remove(format!("{remote}/proyek")).await.unwrap();
    assert!(c.list(remote.clone()).await.unwrap().is_empty());
    c.close().await;
}
