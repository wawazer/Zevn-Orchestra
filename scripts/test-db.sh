#!/usr/bin/env bash
# Tes integrasi database memakai container Docker sementara (image harus sudah ada di lokal)
# dan sshd bawaan macOS untuk tunnel. Semua container & file sementara dibersihkan di akhir.
set -euo pipefail
cd "$(dirname "$0")/../core"
[ -f "$HOME/.cargo/env" ] && . "$HOME/.cargo/env"
T=$(mktemp -d /tmp/zevn-db-test.XXXXXX)
NAMES="zevn-test-pg zevn-test-my zevn-test-maria zevn-test-redis"
cleanup() { docker rm -f $NAMES >/dev/null 2>&1 || true; kill $(cat "$T/sshd.pid" 2>/dev/null) 2>/dev/null || true; rm -rf "$T"; }
trap cleanup EXIT
docker run -d --rm --name zevn-test-pg -e POSTGRES_PASSWORD=rahasia -p 127.0.0.1:55432:5432 postgres:17-alpine >/dev/null
docker run -d --rm --name zevn-test-my -e MYSQL_ROOT_PASSWORD=rahasia -e MYSQL_DATABASE=zevn -p 127.0.0.1:53306:3306 mysql:8.0 >/dev/null
docker run -d --rm --name zevn-test-maria -e MARIADB_ROOT_PASSWORD=rahasia -e MARIADB_DATABASE=zevn -p 127.0.0.1:53307:3306 mariadb:10.4 >/dev/null
docker run -d --rm --name zevn-test-redis -p 127.0.0.1:56379:6379 redis:7-alpine redis-server --requirepass rahasia >/dev/null
ssh-keygen -q -t ed25519 -N "" -f "$T/hostkey"; ssh-keygen -q -t ed25519 -N "" -f "$T/client"; cp "$T/client.pub" "$T/authorized_keys"
printf 'Port 2224\nListenAddress 127.0.0.1\nHostKey %s/hostkey\nPidFile %s/sshd.pid\nAuthorizedKeysFile %s/authorized_keys\nUsePAM no\nStrictModes no\nPasswordAuthentication no\nKbdInteractiveAuthentication no\nAllowTcpForwarding yes\n' "$T" "$T" "$T" > "$T/sshd_config"
/usr/sbin/sshd -f "$T/sshd_config" -E "$T/sshd.log"
echo "menunggu database siap…"
for i in $(seq 1 60); do
  docker exec zevn-test-pg pg_isready -U postgres >/dev/null 2>&1 \
  && docker exec zevn-test-my mysqladmin ping -prahasia --silent >/dev/null 2>&1 \
  && docker exec zevn-test-maria mysqladmin ping -prahasia --silent >/dev/null 2>&1 && break
  sleep 2
done
sleep 3
mkdir -p "$T/home"
RUSTUP_HOME="$HOME/.rustup" CARGO_HOME="$HOME/.cargo" HOME="$T/home" ZEVN_DB_TEST=1 ZEVN_DB_TMP="$T" ZEVN_TEST_KEY="$T/client" \
  cargo test --release --test db_local -- --nocapture
