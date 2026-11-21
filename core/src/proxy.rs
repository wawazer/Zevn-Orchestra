//! Koneksi TCP lewat proxy SOCKS5 atau HTTP CONNECT.
//!
//! Dipakai koneksi SSH native (SFTP, tunnel database) dan, lewat [`proxy_stdio`],
//! sebagai `ProxyCommand` untuk terminal OpenSSH.

use std::time::Duration;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;

use crate::CoreError;
use crate::model::{HostProxy, ProxyKind};

fn perr(msg: impl Into<String>) -> CoreError {
    CoreError::Proxy { msg: msg.into() }
}

/// Buka koneksi ke `host:port` melalui proxy. Hasilnya stream yang sudah tersambung ke tujuan.
pub async fn open(p: &HostProxy, host: &str, port: u16) -> Result<TcpStream, CoreError> {
    let connect = async {
        let mut s = TcpStream::connect((p.host.as_str(), p.port))
            .await
            .map_err(|e| perr(format!("tidak bisa terhubung ke proxy {}:{} ({e})", p.host, p.port)))?;
        let _ = s.set_nodelay(true);
        match p.kind {
            ProxyKind::Socks5 => socks5(&mut s, p, host, port).await?,
            ProxyKind::Http => http_connect(&mut s, p, host, port).await?,
        }
        Ok::<_, CoreError>(s)
    };
    tokio::time::timeout(Duration::from_secs(20), connect)
        .await
        .map_err(|_| perr("waktu koneksi ke proxy habis (20 detik)"))?
}

fn creds(p: &HostProxy) -> Option<(&str, &str)> {
    let user = p.username.as_deref().filter(|u| !u.is_empty())?;
    Some((user, p.password.as_deref().unwrap_or("")))
}

/// SOCKS5 (RFC 1928) dengan nama domain diresolusi oleh proxy, plus login user/password (RFC 1929).
async fn socks5(s: &mut TcpStream, p: &HostProxy, host: &str, port: u16) -> Result<(), CoreError> {
    let auth = creds(p);
    let methods: &[u8] = if auth.is_some() { &[0x00, 0x02] } else { &[0x00] };
    let mut hello = vec![0x05, methods.len() as u8];
    hello.extend_from_slice(methods);
    s.write_all(&hello).await?;

    let mut r = [0u8; 2];
    s.read_exact(&mut r).await?;
    if r[0] != 0x05 {
        return Err(perr("server proxy bukan SOCKS5"));
    }
    match r[1] {
        0x00 => {}
        0x02 => {
            let (u, pw) = auth.ok_or_else(|| perr("proxy meminta login"))?;
            if u.len() > 255 || pw.len() > 255 {
                return Err(perr("username/password proxy terlalu panjang"));
            }
            let mut m = vec![0x01, u.len() as u8];
            m.extend_from_slice(u.as_bytes());
            m.push(pw.len() as u8);
            m.extend_from_slice(pw.as_bytes());
            s.write_all(&m).await?;
            let mut a = [0u8; 2];
            s.read_exact(&mut a).await?;
            if a[1] != 0x00 {
                return Err(perr("login proxy ditolak — periksa username/password proxy"));
            }
        }
        0xFF if auth.is_none() => return Err(perr("proxy meminta login — isi username & password proxy")),
        _ => return Err(perr("metode login proxy tidak didukung")),
    }

    // CONNECT ke tujuan: IP langsung, selain itu nama domain
    let mut req = vec![0x05, 0x01, 0x00];
    match host.parse::<std::net::IpAddr>() {
        Ok(std::net::IpAddr::V4(ip)) => {
            req.push(0x01);
            req.extend_from_slice(&ip.octets());
        }
        Ok(std::net::IpAddr::V6(ip)) => {
            req.push(0x04);
            req.extend_from_slice(&ip.octets());
        }
        Err(_) => {
            if host.len() > 255 {
                return Err(perr("nama host terlalu panjang"));
            }
            req.push(0x03);
            req.push(host.len() as u8);
            req.extend_from_slice(host.as_bytes());
        }
    }
    req.extend_from_slice(&port.to_be_bytes());
    s.write_all(&req).await?;

    let mut head = [0u8; 4];
    s.read_exact(&mut head).await?;
    if head[1] != 0x00 {
        let why = match head[1] {
            0x02 => "ditolak aturan proxy",
            0x03 => "jaringan tidak terjangkau",
            0x04 => "host tidak terjangkau",
            0x05 => "koneksi ditolak server tujuan",
            0x06 => "TTL habis",
            0x08 => "jenis alamat tidak didukung",
            _ => "gagal",
        };
        return Err(perr(format!("proxy tidak bisa membuka {host}:{port} ({why})")));
    }
    // Buang alamat bind balasan
    let skip = match head[3] {
        0x01 => 4,
        0x04 => 16,
        0x03 => {
            let mut l = [0u8; 1];
            s.read_exact(&mut l).await?;
            l[0] as usize
        }
        _ => return Err(perr("balasan proxy tidak dikenal")),
    };
    let mut rest = vec![0u8; skip + 2];
    s.read_exact(&mut rest).await?;
    Ok(())
}

/// HTTP CONNECT dengan Basic auth opsional.
async fn http_connect(s: &mut TcpStream, p: &HostProxy, host: &str, port: u16) -> Result<(), CoreError> {
    let target = if host.contains(':') { format!("[{host}]:{port}") } else { format!("{host}:{port}") };
    let mut req = format!("CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nUser-Agent: ZevnShell\r\n");
    if let Some((u, pw)) = creds(p) {
        req += &format!("Proxy-Authorization: Basic {}\r\n", base64(format!("{u}:{pw}").as_bytes()));
    }
    req += "\r\n";
    s.write_all(req.as_bytes()).await?;

    // Baca header balasan byte demi byte supaya tidak ada data SSH yang ikut terbaca
    let mut buf = Vec::with_capacity(256);
    let mut b = [0u8; 1];
    while !buf.ends_with(b"\r\n\r\n") {
        if s.read(&mut b).await? == 0 {
            return Err(perr("proxy menutup koneksi"));
        }
        buf.push(b[0]);
        if buf.len() > 16 * 1024 {
            return Err(perr("balasan proxy terlalu panjang"));
        }
    }
    let head = String::from_utf8_lossy(&buf);
    let status = head.lines().next().unwrap_or("");
    let code = status.split_whitespace().nth(1).unwrap_or("");
    match code {
        c if c.starts_with('2') => Ok(()),
        "407" => Err(perr("proxy meminta login — periksa username & password proxy")),
        _ => Err(perr(format!("proxy menolak: {}", status.trim()))),
    }
}

fn base64(data: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for c in data.chunks(3) {
        let n = (c[0] as u32) << 16 | (*c.get(1).unwrap_or(&0) as u32) << 8 | *c.get(2).unwrap_or(&0) as u32;
        for i in 0..4 {
            if i <= c.len() {
                out.push(T[(n >> (18 - 6 * i) & 63) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

/// Mode `ProxyCommand` untuk OpenSSH: sambungkan ke `host:port` lewat proxy, lalu salurkan
/// stdin/stdout proses ini ke koneksi tersebut sampai salah satu sisi ditutup.
#[uniffi::export]
pub fn proxy_stdio(proxy: HostProxy, host: String, port: u16) -> Result<(), CoreError> {
    let rt = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .map_err(|e| perr(e.to_string()))?;
    rt.block_on(async move {
        let stream = open(&proxy, &host, port).await?;
        let (mut rd, mut wr) = stream.into_split();
        let up = async {
            let mut stdin = tokio::io::stdin();
            let _ = tokio::io::copy(&mut stdin, &mut wr).await;
            let _ = wr.shutdown().await;
        };
        let down = async {
            let mut stdout = tokio::io::stdout();
            let _ = tokio::io::copy(&mut rd, &mut stdout).await;
            let _ = stdout.flush().await;
        };
        // Selesai saat server menutup koneksi (down), atau ssh menutup stdin lalu server ikut menutup
        tokio::select! {
            _ = down => {}
            _ = async { up.await; std::future::pending::<()>().await } => {}
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::base64;

    #[test]
    fn base64_matches_rfc4648() {
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"f"), "Zg==");
        assert_eq!(base64(b"fo"), "Zm8=");
        assert_eq!(base64(b"foo"), "Zm9v");
        assert_eq!(base64(b"user:pass"), "dXNlcjpwYXNz");
    }
}
