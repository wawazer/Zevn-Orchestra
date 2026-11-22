# Zevn Shell

Satu jendela untuk mengurus server: sesi SSH, pemindahan berkas, penjelajah database, terowongan port, dan kumpulan perintah favorit. Antarmukanya asli Windows (WinUI 3 di atas .NET 9), sementara seluruh pekerjaan berat — enkripsi vault, protokol SSH/SFTP, koneksi database, pembuatan kunci — berjalan di satu pustaka Rust (`zevn_core.dll`) lewat binding UniFFI.

Tersedia untuk Windows 10/11 pada dua arsitektur, x64 dan ARM64. Versi rilis disimpan di satu tempat, berkas [`VERSION`](VERSION).

## Tampilan

Ruang kerja disusun sebagai tiga panel mengambang di atas kanvas gelap, bukan satu jendela bertumpuk dialog:

- **Sidebar** — kotak pencarian menu, daftar bagian (Hosts, SFTP, Database, Port Forwarding, Snippets, Known Hosts, Logs, Tentang, dan akun), serta status di bagian bawah.
- **Panel utama** — isi bagian yang sedang dibuka, dengan breadcrumb dan judul di kepala halaman.
- **Inspector** — panel kanan untuk detail konteks dari apa yang sedang dipilih.

Menu bisa berpindah tanpa memuat ulang jendela, jadi sesi terminal yang sedang berjalan tidak ikut tertutup.

## Isi aplikasi

**Sesi server**
Keyboard tersambung langsung ke shell server lewat ConPTY. Beberapa host bisa dibuka bersamaan, masing-masing di tabnya sendiri, dan daftar host bisa disusun per grup, diberi warna, atau disaring dengan pencarian. Autentikasi menerima password, berkas private key, maupun agen SSH. Untuk jaringan berlapis tersedia jump host, proxy SOCKS5/HTTP, penerusan agen, variabel lingkungan, dan perintah pembuka yang dijalankan otomatis. Tab terminal lokal (PowerShell, CMD, WSL) berdampingan dengan tab SSH.

**Berkas**
Halaman SFTP menampilkan dua sisi: penyimpanan komputer dan isi direktori server. Unggah dan unduh berjalan dengan bilah kemajuan, taksiran kecepatan, serta tombol pembatalan. Membuat folder, mengganti nama, menghapus, dan mengatur izin file juga dilakukan dari sini.

**Database**
Delapan jenis mesin bisa dibuka: PostgreSQL, MySQL, MariaDB, SQL Server, SQLite, Redis, MongoDB, dan ClickHouse. Tiap koneksi boleh langsung, memakai TLS, atau menumpang terowongan SSH. Di dalamnya ada penjelajahan isi tabel dengan halaman, tampilan struktur, jendela SQL beriwayat, penyaringan kolom bergaya phpMyAdmin (`=`, `LIKE`, `IN`, `BETWEEN`, `IS NULL`, dan sejenisnya), pencarian di semua kolom sekaligus, serta ekspor CSV.

**Project, deploy, dan log**
Satu project mengumpulkan server untuk satu aplikasi dan bisa diaktifkan untuk mempersempit daftar host, SFTP, dan tunnel. Deploy satu klik mengunggah folder lokal ke `releases/<waktu>`, memindahkan symlink `current`, menjalankan restart serta health check, dan mengembalikan rilis sebelumnya bila gagal. Halaman Live Logs mengikuti keluaran service di server (`journalctl`, `docker logs`, atau `tail -F`) sesuai perintah yang disimpan di project. Halaman Overview merangkum project aktif dalam satu tempat — server beserta tombol sambung, tunnel dengan sakelarnya, status deploy, dan Live Log — sehingga pekerjaan sehari-hari tidak perlu berpindah-pindah menu.

**Perkakas pendukung**
Kunci SSH Ed25519 dapat dibuat di dalam aplikasi atau diimpor dari `~/.ssh` (Ed25519, RSA, ECDSA, termasuk yang berpassphrase). Port forwarding tersedia dalam tiga bentuk — local, remote, dan SOCKS dinamis — dan dihidupkan cukup dengan sakelar. Ada juga panel info server (OS, kernel, CPU, waktu aktif, ditambah grafik CPU, RAM, jaringan, dan disk yang bergerak langsung), daftar fingerprint host yang dikenal, catatan sesi, dan tema terminal bergantian.

## Soal kerahasiaan

Seluruh isi vault disandikan memakai kunci turunan Argon2id dan cipher XChaCha20-Poly1305. Master password tidak ditulis ke disk dan tidak dikirim ke mana pun. Membuka vault bisa memakai Windows Hello, dan vault terkunci sendiri setelah tidak dipakai beberapa lama. Untuk fingerprint server, kunci yang berubah dari catatan `known_hosts` akan ditolak.

Bila sinkronisasi diaktifkan, isi vault disimpan sebagai ciphertext di folder tersembunyi `appDataFolder` milik Google Drive pengguna sendiri — tidak ada server milik pembuat aplikasi yang menyimpan data siapa pun. Tidak ada telemetri.

## Memasang

1. Unduh `Zevn-Shell-<versi>-Windows-x64-Setup.exe`, atau varian `arm64` bila memakai perangkat Snapdragon/ARM.
2. Bila SmartScreen menampilkan peringatan, pilih **More info** lalu **Run anyway**.
3. Jalankan pemasang (tidak perlu hak administrator), kemudian buka **Zevn Shell** dari Start Menu.

Pemasangan berjalan per pengguna di `%LOCALAPPDATA%\Programs\Zevn Shell`; vault dan pengaturan disimpan di `%APPDATA%\Zevn Shell` dan tidak dihapus saat aplikasi dicopot.

## Membangun sendiri

Yang perlu terpasang: Rust toolchain, .NET SDK 9, Visual Studio Build Tools dengan komponen C++ (serta Clang/NASM untuk build Rust Windows), dan Inno Setup 6.3+.

Membangun inti Rust (menghasilkan `Native\x64\zevn_core.dll` dan `Native\arm64\zevn_core.dll`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-core-windows.ps1
```

Membangun aplikasi beserta installernya ke `dist\`:

```powershell
powershell -ExecutionPolicy Bypass -Command "& './scripts/build-windows-installer.ps1' -Arch ARM64,x64"
```

Menjalankan build Debug untuk pengembangan:

```powershell
dotnet build apps/windows/ZevnShell/ZevnShell.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

## Mengatur versi rilis

[`VERSION`](VERSION) di akar repo adalah satu-satunya tempat versi ditulis dengan tangan. Berkas itu dibaca oleh `Directory.Build.props` (jadi `dotnet publish` tanpa `-p:Version` pun memakai versi yang benar) dan oleh skrip installer. Untuk menaikkan versi:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\set-version.ps1 -Version 0.1.2
```

Skrip itu menyamakan `core/Cargo.toml`, `core/Cargo.lock`, `apps/windows/installer/ZevnShell.iss`, dan default `-Version` di skrip installer. Sesudahnya bangun ulang inti Rust agar `zevn-core` di halaman Tentang ikut berubah, lalu bangun installer. Untuk memeriksa apakah semuanya masih seragam:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\set-version.ps1 -Check
```

Skrip installer menolak berjalan bila `VERSION`, `core/Cargo.toml`, dan `-Version` tidak sama, supaya rilis tidak mencampur versi aplikasi dengan isi `zevn_core.dll`.

## Uji

- Tes unit Rust: `cd core && cargo test`
- `scripts/test-sftp.sh` — menjalankan sshd sementara di loopback, termasuk jalur proxy.
- `scripts/test-db.sh` — memakai container Docker sementara.

## Peta folder

```
core/         pustaka Rust: vault, SSH, SFTP, database, kunci, proxy, info server
apps/windows/ aplikasi WinUI 3, termasuk folder Native berisi DLL inti per arsitektur
scripts/      skrip build inti & installer, penyelaras versi, plus skrip tes
dist/         hasil installer siap dibagikan
VERSION       versi rilis tunggal
```

Rancangan teknis yang lebih rinci ada di [CONCEPT.md](CONCEPT.md). Peta kode, alur data, dan panduan menambah fitur ada di [ARCHITECTURE.md](ARCHITECTURE.md).

## Lisensi

Kode ini dirilis di bawah [MIT License](LICENSE) — bebas dipakai, diubah, dan disebarkan, dengan tetap menyertakan pemberitahuan hak cipta. Dibuat oleh Wawazer.
