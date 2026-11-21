//! Detail server (OS, CPU, RAM, storage, uptime) lewat satu perintah shell di koneksi SSH terpisah.

use std::time::Duration;

use russh::ChannelMsg;

use crate::ssh::{self, SshTarget};
use crate::CoreError;

/// Satu partisi / filesystem.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct DiskInfo {
    pub mount: String,
    pub total_bytes: u64,
    pub used_bytes: u64,
}

/// Spesifikasi & pemakaian server.
#[derive(Debug, Clone, PartialEq, Default, uniffi::Record)]
pub struct ServerInfo {
    pub hostname: String,
    /// mis. "Ubuntu 24.04.1 LTS" atau "macOS 15.2"
    pub os: String,
    /// mis. "Linux 6.8.0-45-generic"
    pub kernel: String,
    pub arch: String,
    pub cpu_model: String,
    pub cpu_cores: u32,
    pub mem_total_bytes: u64,
    pub mem_used_bytes: u64,
    pub swap_total_bytes: u64,
    pub swap_used_bytes: u64,
    pub disks: Vec<DiskInfo>,
    pub uptime_secs: Option<u64>,
    /// Load average 1/5/15 menit, mis. "0.12 0.08 0.05"
    pub load: String,
}

/// Skrip POSIX sh: keluaran `kunci=nilai` per baris. Linux lewat /proc, macOS lewat sysctl.
pub(crate) const SCRIPT: &str = r#"export LC_ALL=C
echo "hostname=$(uname -n 2>/dev/null)"
if [ -r /etc/os-release ]; then . /etc/os-release; echo "os=${PRETTY_NAME:-$NAME $VERSION}";
elif command -v sw_vers >/dev/null 2>&1; then echo "os=$(sw_vers -productName) $(sw_vers -productVersion)";
else echo "os=$(uname -s)"; fi
echo "kernel=$(uname -sr)"
echo "arch=$(uname -m)"
if [ -r /proc/cpuinfo ]; then
  m=$(grep -m1 -i -E '^(model name|hardware|cpu model)' /proc/cpuinfo | cut -d: -f2- | sed 's/^ *//')
  [ -z "$m" ] && command -v lscpu >/dev/null 2>&1 && m=$(lscpu | grep -m1 'Model name' | cut -d: -f2- | sed 's/^ *//')
  [ -z "$m" ] && [ -r /sys/firmware/devicetree/base/model ] && m=$(tr -d '\0' < /sys/firmware/devicetree/base/model)
  echo "cpu=$m"
  echo "cores=$(grep -c '^processor' /proc/cpuinfo)"
else
  echo "cpu=$(sysctl -n machdep.cpu.brand_string 2>/dev/null)"
  echo "cores=$(sysctl -n hw.ncpu 2>/dev/null)"
fi
if [ -r /proc/meminfo ]; then
  awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} /^MemFree:/{f=$2} /^SwapTotal:/{st=$2} /^SwapFree:/{sf=$2}
       END{ if (a=="") a=f; printf "mem_total=%.0f\nmem_used=%.0f\nswap_total=%.0f\nswap_used=%.0f\n", t*1024, (t-a)*1024, st*1024, (st-sf)*1024 }' /proc/meminfo
else
  echo "mem_total=$(sysctl -n hw.memsize 2>/dev/null)"
  ps=$(sysctl -n hw.pagesize 2>/dev/null)
  vm_stat 2>/dev/null | awk -v ps="$ps" '/Pages active/{a=$3} /Pages wired/{w=$4} /occupied by compressor/{c=$5} END{gsub(/\./,"",a);gsub(/\./,"",w);gsub(/\./,"",c); printf "mem_used=%.0f\n", (a+w+c)*ps}'
fi
if [ -r /proc/uptime ]; then echo "uptime=$(cut -d. -f1 /proc/uptime)";
else b=$(sysctl -n kern.boottime 2>/dev/null | sed -n 's/^{ sec = \([0-9]*\),.*/\1/p'); [ -n "$b" ] && echo "uptime=$(( $(date +%s) - b ))"; fi
if [ -r /proc/loadavg ]; then echo "load=$(cut -d' ' -f1-3 /proc/loadavg)"; else echo "load=$(sysctl -n vm.loadavg 2>/dev/null | tr -d '{}' | sed 's/^ *//;s/ *$//')"; fi
df -P -k 2>/dev/null | awk 'NR>1 && $2>0 && $1 !~ /^(tmpfs|devtmpfs|overlay|udev|none|shm|map|devfs|run|efivarfs)/ && $6 !~ /^\/(dev|proc|sys|run|snap|etc|boot\/efi|System\/Volumes\/(VM|Preboot|Update|xarts|iSCPreboot|Hardware)|Library\/Developer|Volumes\/dmg|private\/var\/folders)/ && $6 !~ /OrbStack/ {printf "disk=%s|%.0f|%.0f\n", $6, $2*1024, $3*1024}'
"#;

/// Sampel ringan untuk grafik (diambil berkala).
#[derive(Debug, Clone, PartialEq, Default, uniffi::Record)]
pub struct ServerSample {
    /// Pemakaian CPU 0–100 (rata-rata semua core).
    pub cpu_percent: f32,
    pub mem_total_bytes: u64,
    pub mem_used_bytes: u64,
    pub load1: f32,
    /// Total byte diterima/dikirim semua interface (kecuali loopback) sejak boot; selisih antar sampel = kecepatan.
    pub net_rx_bytes: u64,
    pub net_tx_bytes: u64,
}

/// Skrip sampel: CPU dari selisih /proc/stat (0,5 detik), RAM, load, dan counter jaringan.
pub(crate) const SAMPLE_SCRIPT: &str = r#"export LC_ALL=C
if [ -r /proc/stat ]; then
  read c a b d e f g h i r < /proc/stat; sleep 0.5; read c a2 b2 d2 e2 f2 g2 h2 i2 r < /proc/stat
  idle=$(( (e2 + f2) - (e + f) )); total=$(( (a2+b2+d2+e2+f2+g2+h2+i2) - (a+b+d+e+f+g+h+i) ))
  [ "$total" -gt 0 ] && echo "cpu=$(( 1000 * (total - idle) / total ))" || echo "cpu=0"
else
  n=$(sysctl -n hw.ncpu 2>/dev/null); ps -A -o %cpu= 2>/dev/null | awk -v n="${n:-1}" '{s+=$1} END{printf "cpu=%.0f\n", s*10/n}'
fi
if [ -r /proc/meminfo ]; then
  awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} /^MemFree:/{f=$2} END{ if (a=="") a=f; printf "mem_total=%.0f\nmem_used=%.0f\n", t*1024, (t-a)*1024 }' /proc/meminfo
else
  echo "mem_total=$(sysctl -n hw.memsize 2>/dev/null)"
  ps=$(sysctl -n hw.pagesize 2>/dev/null)
  vm_stat 2>/dev/null | awk -v ps="$ps" '/Pages active/{a=$3} /Pages wired/{w=$4} /occupied by compressor/{c=$5} END{gsub(/\./,"",a);gsub(/\./,"",w);gsub(/\./,"",c); printf "mem_used=%.0f\n", (a+w+c)*ps}'
fi
if [ -r /proc/loadavg ]; then echo "load=$(cut -d' ' -f1 /proc/loadavg)"; else echo "load=$(sysctl -n vm.loadavg 2>/dev/null | tr -d '{}' | awk '{print $1}')"; fi
if [ -r /proc/net/dev ]; then
  awk -F'[: ]+' 'NR>2 { i=($1==""?2:1); if ($i!="lo") { rx+=$(i+1); tx+=$(i+9) } } END{printf "rx=%.0f\ntx=%.0f\n", rx, tx}' /proc/net/dev
else
  netstat -ib 2>/dev/null | awk '$1!~/^lo/ && $3~/Link/ {rx+=$7; tx+=$10} END{printf "rx=%.0f\ntx=%.0f\n", rx, tx}'
fi
"#;

pub(crate) fn parse_sample(out: &str) -> ServerSample {
    let mut s = ServerSample::default();
    let num = |v: &str| v.trim().parse::<f64>().ok().map(|x| x.max(0.0)).unwrap_or(0.0);
    for line in out.lines() {
        let Some((k, v)) = line.split_once('=') else { continue };
        match k {
            "cpu" => s.cpu_percent = (num(v) / 10.0).min(100.0) as f32,
            "mem_total" => s.mem_total_bytes = num(v) as u64,
            "mem_used" => s.mem_used_bytes = num(v) as u64,
            "load" => s.load1 = num(v) as f32,
            "rx" => s.net_rx_bytes = num(v) as u64,
            "tx" => s.net_tx_bytes = num(v) as u64,
            _ => {}
        }
    }
    s
}

/// Ubah keluaran skrip menjadi [`ServerInfo`].
pub(crate) fn parse(out: &str) -> ServerInfo {
    let mut s = ServerInfo::default();
    let num = |v: &str| v.trim().parse::<f64>().ok().map(|x| x.max(0.0) as u64).unwrap_or(0);
    for line in out.lines() {
        let Some((k, v)) = line.split_once('=') else { continue };
        let v = v.trim();
        match k {
            "hostname" => s.hostname = v.into(),
            "os" => s.os = v.trim_matches('"').into(),
            "kernel" => s.kernel = v.into(),
            "arch" => s.arch = v.into(),
            "cpu" => s.cpu_model = v.split_whitespace().collect::<Vec<_>>().join(" "),
            "cores" => s.cpu_cores = num(v) as u32,
            "mem_total" => s.mem_total_bytes = num(v),
            "mem_used" => s.mem_used_bytes = num(v),
            "swap_total" => s.swap_total_bytes = num(v),
            "swap_used" => s.swap_used_bytes = num(v),
            "uptime" => s.uptime_secs = v.parse().ok(),
            "load" => s.load = v.split_whitespace().take(3).collect::<Vec<_>>().join(" "),
            "disk" => {
                let p: Vec<&str> = v.rsplitn(3, '|').collect();
                if p.len() == 3 {
                    let d = DiskInfo { mount: p[2].into(), total_bytes: num(p[1]), used_bytes: num(p[0]) };
                    if !s.disks.iter().any(|x| x.mount == d.mount) {
                        s.disks.push(d);
                    }
                }
            }
            _ => {}
        }
    }
    // macOS: "/" hanya volume sistem yang disegel; pemakaian sebenarnya ada di volume Data
    if let Some(data) = s.disks.iter().position(|d| d.mount == "/System/Volumes/Data") {
        let d = s.disks.remove(data);
        if let Some(root) = s.disks.iter_mut().find(|x| x.mount == "/") {
            root.used_bytes = root.used_bytes.saturating_add(d.used_bytes).min(root.total_bytes);
        }
    }
    // Root dulu, lalu partisi terbesar
    s.disks.sort_by(|a, b| (b.mount == "/").cmp(&(a.mount == "/")).then(b.total_bytes.cmp(&a.total_bytes)));
    s.disks.truncate(6);
    s
}

/// Koneksi SSH yang tetap terbuka untuk panel Info server: info lengkap sekali, lalu sampel berkala untuk grafik.
#[derive(uniffi::Object)]
pub struct ServerMonitor {
    handle: tokio::sync::Mutex<Option<russh::client::Handle<ssh::Client>>>,
}

impl ServerMonitor {
    async fn exec(&self, script: &str) -> Result<String, CoreError> {
        let guard = self.handle.lock().await;
        let Some(h) = guard.as_ref() else { return Err(CoreError::Ssh { msg: "koneksi info server sudah ditutup".into() }) };
        let run = async {
            let mut ch = h.channel_open_session().await.map_err(|e| CoreError::Ssh { msg: e.to_string() })?;
            ch.exec(true, script).await.map_err(|e| CoreError::Ssh { msg: e.to_string() })?;
            let mut out = Vec::new();
            while let Some(msg) = ch.wait().await {
                match msg {
                    ChannelMsg::Data { data } => out.extend_from_slice(&data),
                    ChannelMsg::Eof | ChannelMsg::Close => break,
                    _ => {}
                }
            }
            Ok::<_, CoreError>(String::from_utf8_lossy(&out).into_owned())
        };
        tokio::time::timeout(Duration::from_secs(20), run)
            .await
            .map_err(|_| CoreError::Ssh { msg: "server tidak menjawab (20 detik)".into() })?
    }
}

#[uniffi::export(async_runtime = "tokio")]
impl ServerMonitor {
    /// Sambung ke host (password/key/agent/proxy sesuai target).
    #[uniffi::constructor]
    pub async fn connect(target: SshTarget) -> Result<std::sync::Arc<Self>, CoreError> {
        let handle = ssh::connect(&target).await?;
        Ok(std::sync::Arc::new(Self { handle: tokio::sync::Mutex::new(Some(handle)) }))
    }

    /// Spesifikasi lengkap: OS, CPU, RAM, storage, uptime.
    pub async fn info(&self) -> Result<ServerInfo, CoreError> {
        let info = parse(&self.exec(SCRIPT).await?);
        if info.kernel.is_empty() && info.os.is_empty() {
            return Err(CoreError::Ssh { msg: "server tidak mengembalikan info (shell bukan POSIX?)".into() });
        }
        Ok(info)
    }

    /// Sampel ringan untuk grafik (sekitar 0,5 detik di server).
    pub async fn sample(&self) -> Result<ServerSample, CoreError> {
        Ok(parse_sample(&self.exec(SAMPLE_SCRIPT).await?))
    }

    pub async fn close(&self) {
        if let Some(h) = self.handle.lock().await.take() {
            let _ = h.disconnect(russh::Disconnect::ByApplication, "", "en").await;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_linux_output() {
        let out = "hostname=web-1\nos=Ubuntu 24.04.1 LTS\nkernel=Linux 6.8.0-45-generic\narch=x86_64\n\
                   cpu=Intel(R) Xeon(R)   CPU @ 2.20GHz\ncores=4\nmem_total=8340000768\nmem_used=2100000000\n\
                   swap_total=0\nswap_used=0\nuptime=93784\nload=0.12 0.08 0.05\n\
                   disk=/boot|1000000000|200000000\ndisk=/|80000000000|31000000000\ndisk=/data|500000000000|1000\n";
        let s = parse(out);
        assert_eq!(s.os, "Ubuntu 24.04.1 LTS");
        assert_eq!(s.cpu_model, "Intel(R) Xeon(R) CPU @ 2.20GHz");
        assert_eq!(s.cpu_cores, 4);
        assert_eq!(s.uptime_secs, Some(93784));
        assert_eq!(s.disks[0].mount, "/");
        assert_eq!(s.disks[1].mount, "/data");
        assert_eq!(s.disks[0].used_bytes, 31000000000);
    }

    #[test]
    fn parses_sample_and_macos_data_volume() {
        let s = parse_sample("cpu=473\nmem_total=1000\nmem_used=250\nload=0.42\nrx=123\ntx=456\n");
        assert!((s.cpu_percent - 47.3).abs() < 0.01);
        assert_eq!((s.mem_used_bytes, s.net_rx_bytes, s.net_tx_bytes), (250, 123, 456));
        let i = parse("os=macOS 15\nkernel=Darwin\ndisk=/|500|13\ndisk=/System/Volumes/Data|500|400\n");
        assert_eq!(i.disks.len(), 1);
        assert_eq!(i.disks[0].used_bytes, 413);
    }

    #[test]
    fn mount_with_spaces_and_missing_fields() {
        let s = parse("os=macOS 15.2\nkernel=Darwin 24.2.0\ndisk=/Volumes/Data Disk|100|40\ngarbage\n");
        assert_eq!(s.disks[0].mount, "/Volumes/Data Disk");
        assert_eq!(s.mem_total_bytes, 0);
        assert_eq!(s.uptime_secs, None);
    }
}
