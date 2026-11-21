#!/usr/bin/env bash
# Tes integrasi SFTP memakai sshd bawaan macOS (tanpa root, port 2222, hanya 127.0.0.1).
# Semua file sementara (host key, key klien, known_hosts) ada di folder temp, bukan ~/.ssh.
set -euo pipefail
cd "$(dirname "$0")/../core"
[ -f "$HOME/.cargo/env" ] && . "$HOME/.cargo/env"
T=$(mktemp -d /tmp/zevn-sftp-test.XXXXXX)
PROXY_PID=
trap 'kill $(cat "$T/sshd.pid" 2>/dev/null) $PROXY_PID 2>/dev/null || true; rm -rf "$T"' EXIT
# Proxy tes (SOCKS5 :21080, HTTP :23128) untuk koneksi lewat proxy
python3 ../scripts/test-proxy.py 21080 23128 zevn proxy-pw "$T/proxy.log" & PROXY_PID=$!
ssh-keygen -q -t ed25519 -N "" -f "$T/hostkey"
ssh-keygen -q -t ed25519 -N "" -f "$T/client"
cp "$T/client.pub" "$T/authorized_keys"
cat > "$T/sshd_config" <<CFG
Port 2222
ListenAddress 127.0.0.1
HostKey $T/hostkey
PidFile $T/sshd.pid
AuthorizedKeysFile $T/authorized_keys
UsePAM no
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
Subsystem sftp /usr/libexec/sftp-server
CFG
/usr/sbin/sshd -f "$T/sshd_config" -E "$T/sshd.log"
sleep 1
mkdir -p "$T/home"
RUSTUP_HOME="$HOME/.rustup" CARGO_HOME="$HOME/.cargo" HOME="$T/home" ZEVN_TEST_PORT=2222 ZEVN_TEST_KEY="$T/client" ZEVN_TEST_ROOT="$T" \
  ZEVN_TEST_PROXY_PORTS=21080,23128 ZEVN_TEST_PROXY_LOG="$T/proxy.log" \
  cargo test --release --test sftp_local -- --nocapture
