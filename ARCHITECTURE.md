# Zevn Shell — Arsitektur

Dokumen ini untuk pembacaan ke depan: bagaimana kode disusun, siapa melakukan apa, di mana
data disimpan, dan bagaimana sebuah fitur baru seharusnya ditambahkan. Untuk gambaran produk,
lihat [README.md](README.md); untuk alasan rancangan tingkat tinggi, lihat
[CONCEPT.md](CONCEPT.md).

Terakhir diperbarui: November 2026 (setelah Project + Deploy masuk).

## 1. Prinsip yang dipegang

1. **Satu inti, banyak tampilan.** Semua urusan yang tidak boleh salah (kriptografi vault,
   protokol SSH/SFTP, database, deploy) ada di `core/` (Rust). UI tidak pernah menyentuh
   enkripsi, socket, atau format file vault.
2. **UI hanya menyusun tampilan dan memanggil inti.** Semua data persisten lewat `Vault`.
   Tidak ada salinan data di UI yang bertahan setelah aplikasi ditutup, kecuali pengaturan
   perangkat di `settings.json`.
3. **Record, bukan dokumen besar.** Vault adalah kumpulan record kecil dengan `id`,
   `updated_at`, dan `deleted` (tombstone), supaya dua perangkat bisa menyunting bersamaan
   dan digabung tanpa kehilangan data.
4. **Perangkat berbeda, data berbeda.** Yang ikut vault hanya data yang berguna di semua
   perangkat. Path lokal, riwayat di memori, dan status proses tinggal di perangkat.

## 2. Peta folder

```text
core/                     pustaka Rust zevn-core (cdylib/staticlib)
  src/lib.rs              titik ekspor UniFFI + daftar modul
  src/model.rs            semua record & enum bersama (Host, Project, ...)
  src/vault.rs            CRUD vault, merge sync, persist
  src/crypto.rs           format file vault (Argon2id + XChaCha20-Poly1305)
  src/ssh.rs              koneksi russh, verifikasi known_hosts, SshTarget
  src/sftp.rs             SftpClient: list/upload/download/exec/upload_into
  src/deploy.rs           Deployer: rilis, symlink current, health check, rollback
  src/logs.rs             LogStream: kanal SSH streaming untuk panel log
  src/db.rs               delapan mesin database (query, struktur, filter)
  src/keys.rs             generate/import kunci SSH
  src/proxy.rs            koneksi TCP lewat SOCKS5/HTTP CONNECT + stdio ProxyCommand
  src/server_info.rs      spesifikasi server + sampling grafik
  src/error.rs            CoreError (satu enum untuk semua kegagalan)

apps/windows/ZevnShell/   aplikasi WinUI 3 (.NET 9)
  App.xaml(.cs)           entry aplikasi, handler error global
  MainWindow.xaml(.cs)    kanvas tiga panel (sidebar, utama, inspector)
  Core/AppModel.cs        state aplikasi + operasi vault + navigasi
  Core/Support.cs         Config (path), Settings (JSON lokal), Errors (teks ID)
  Core/DeployModel.cs     jembatan Deployer ↔ UI
  Core/SftpModel.cs       jembatan SftpClient ↔ UI
  Core/DbWorkspace.cs     state penjelajah database
  Core/ServerStats.cs     polling ServerMonitor
  Core/Ui.cs, Theme.cs    helper layout & warna
  Core/Generated/         binding C# hasil UniFFI (gitignored, wajib digenerate)
  Native/x64, arm64/      zevn_core.dll hasil build (gitignored)
  Terminal/               sesi terminal, ConPTY, xterm.js, TunnelManager
  Views/                  satu berkas per halaman + ShellPage (routing)

apps/windows/installer/   skrip Inno Setup
scripts/                  build core, installer, set-version, tes integrasi
dist/                     installer jadi
VERSION                   satu-satunya sumber nomor versi
```

## 3. Lapisan dan batas tanggung jawab

| Lapisan | Bahasa/teknologi | Boleh tahu | Tidak boleh tahu |
|---|---|---|---|
| Tampilan | WinUI 3, XAML/C# | state UI, teks, warna | kunci, ciphertext, socket |
| Jembatan UI | C# (`AppModel`, `*Model`) | record vault, task async | format file vault |
| Inti | Rust (`zevn-core`) | semua protokol & kripto | konsep halaman/tombol |
| Proses luar | `ssh.exe`, ConPTY, WebView2 | argumen & I/O | data vault |

Dua jalur koneksi memang hidup berdampingan dan itu disengaja:

- **Terminal interaktif** memakai `ssh.exe` di dalam ConPTY, dirender xterm.js di WebView2
  (`Terminal/TerminalSession.cs`, `Terminal/SshCommand.cs`, `Terminal/PtyProcess.cs`).
  Ini memberi pengalaman shell yang sama dengan terminal asli (agent, jump host, prompt,
  Ctrl+C, dsb.) tanpa menulis ulang emulasi terminal.
- **Operasi terprogram** (SFTP, deploy, info server, tunnel database) memakai russh di
  `core/`, karena butuh kontrol per-kanal, timeout, dan hasil yang bisa diolah.
- **Port forwarding** dijalankan `TunnelManager` sebagai proses `ssh -N` (`-L`/`-R`/`-D`),
  dengan status "Starting" selama 2,5 detik pertama lalu "Running".

Konsekuensinya: perubahan pada `SshTarget` atau cara auth harus dicek di dua tempat —
`SshCommand` (terminal) dan `core/src/ssh.rs` (SFTP/deploy/db/info).

## 4. Alur data

### 4.1 Vault

```text
UI (C#)
  └─ AppModel.Save(host) / Save(project) / ...
       └─ Vault.upsert_*()            (Rust, satu-satunya penulis)
            ├─ validasi + isi id/timestamp
            ├─ persist() → vault.zevn (seluruh isi, terenkripsi)
            └─ AppModel.Reload() → Raise("data") → halaman render ulang
```

- Lokasi: `%APPDATA%\Zevn Shell\vault.zevn`.
- Format: `"ZEVN" | versi:u8 | salt:16 | nonce:24 | ciphertext`, AAD = header.
  Kunci 32 byte dari Argon2id (64 MiB, 3 iterasi), cipher XChaCha20-Poly1305.
- `Vault` menyimpan `VaultData` di `Mutex`; setiap perubahan langsung ditulis ke disk.
- `unlock_with_key` dipakai Windows Hello: kunci mentah disimpan di penyimpanan kredensial
  sistem, bukan master password.

`VaultData` (semua `#[serde(default)]`, jadi vault lama tetap terbaca):

| Koleksi | Isi |
|---|---|
| `hosts` | server: alamat, auth, grup, warna, proxy, jump host, tema |
| `keys` | kunci SSH (private key ikut terenkripsi di vault) |
| `snippets` | perintah tersimpan |
| `forwards` | aturan port forwarding |
| `logs` | riwayat sesi (mulai, selesai, kode keluar) |
| `databases` | koneksi database tersimpan |
| `projects` | project: host, auto tunnel, konfigurasi deploy |
| `account_email` | email Google untuk sync (bukan rahasia) |

### 4.2 Sinkronisasi

`merge_remote(blob)` membaca vault dari Drive, memastikan salt sama (jika beda →
`KeyMismatch`), lalu menggabungkan **per record**: `updated_at` terbaru menang, termasuk
tombstone. Hasilnya `SyncOutcome { local_changed, remote_outdated }`. UI memanggil
`Reload()` bila lokal berubah dan mengunggah ulang bila Drive tertinggal.

Aturan penting saat menambah koleksi baru: tambahkan juga di `merge_remote` dan di
`impl_record!`, atau record itu akan hilang setiap sync.

### 4.3 Pengaturan perangkat

`Settings` (`apps/windows/ZevnShell/Core/Support.cs`) adalah JSON di
`%LOCALAPPDATA%\Zevn Shell\settings.json`. Kunci yang sudah dipakai:

| Kunci | Arti |
|---|---|
| `project.active` | id project yang sedang aktif (null = semua host) |
| `deploy.local.<projectId>` | folder lokal sumber deploy di PC ini |
| `appearance`, `hideAddresses`, `hostLayout`, `terminal.autocomplete` | preferensi tampilan |
| `db.showTree`, `terminal.localShell`, `profile.name`, `profile.picture` | preferensi lain |

Jangan taruh path atau status proses di vault: nilainya berbeda antar perangkat.

## 5. Model record

Setiap record yang disinkronkan wajib punya:

```rust
pub id: String,
pub updated_at: i64,   // milidetik epoch, diisi Vault saat upsert
pub deleted: bool,     // tombstone
```

`model.rs` menyediakan trait `Record` + makro `impl_record!` yang mengisi perilaku
`id()/ts()/set_id()/touch()`. Helper generik `upsert`, `remove`, `active`, dan
`merge_records` di `vault.rs` bekerja untuk semua koleksi yang mengimplementasikannya.

`Project` sengaja hanya menyimpan `host_ids`, bukan salinan `Host`. Dengan begitu
perubahan host (ganti port, password, key) langsung berlaku di semua project dan tidak ada
data ganda yang bisa berbeda. `deploy_host_id` juga referensi ke host yang sama.

## 6. Lapisan UI

- `MainWindow` menampung tiga panel mengambang di kanvas gelap.
- `ShellPage` adalah kerangka: sidebar (pencarian + daftar `Section`), panel utama, inspector.
  Routing halaman ada di satu `switch` (`ShellPage.cs`, sekitar baris 490) dan badge jumlah
  di fungsi terpisah. Menambah halaman berarti menyentuh keduanya.
- Setiap halaman turunan `PageBase` mendaftarkan kata kunci perubahan yang ia pedulikan
  (mis. `new DeployPage() : base("deploy", "data")`). `AppModel.Raise("data")` memicu
  render ulang halaman yang berlangganan; tidak ada binding dua arah otomatis.
- Semua operasi lambat memakai `async` + `Task.Run` di sisi inti; hasil dikembalikan ke UI
  lewat `DispatcherQueue.TryEnqueue` (lihat `DeployModel.Post`).
- Satu `json`-like enum `Section` menentukan judul, ikon, dan warna sidebar
  (`SectionInfo` di `AppModel.cs`).
- `Errors.Text(Exception)` menerjemahkan `CoreException` ke bahasa Indonesia. Setiap varian
  error baru di Rust harus ditambahkan di sini, kalau tidak pengguna melihat pesan mentah.

## 7. Orkestrasi project

Project adalah konteks kerja, bukan wadah data:

- `AppModel.ActiveProjectId` dibaca dari `settings.json`; `SetActiveProject` menghentikan
  tunnel otomatis project lama lalu menyalakan yang baru.
- `ProjectHosts`, `ProjectForwards`, dan penyaringan halaman Hosts/SFTP/Forwarding/Snippets
  memakai `ActiveProject.HostIds`.
- `_autoTunnels` mencatat tunnel yang **dinyalakan oleh project**. Tunnel yang dinyalakan
  manual tidak pernah dimatikan otomatis. Menyalakan/mematikan tunnel manual menghapus id
  dari himpunan ini.
- Project yang dihapus di perangkat lain dibersihkan dari `settings.json` saat `Reload()`.

## 8. Deploy

### 8.1 Tata letak di server

```text
<base_path>/                 mis. /srv/toko
  releases/
    20261121-120501/         isi folder lokal (satu rilis)
    20261121-093000/
  current -> releases/20261121-120501
```

Nama rilis = waktu UTC `YYYYMMDD-HHMMSS` (tanpa dependency `chrono`, memakai algoritma
civil-from-days), sehingga urutan leksikografis = urutan waktu. Jika dua deploy terjadi di
detik yang sama, nama diberi akhiran `-2`, `-3`, dst.

### 8.2 Alur `Deployer.deploy`

```text
1. mkdir -p <base>/releases <base>
2. nama rilis unik; mkdir <base>/releases/<nama>
3. upload_into(folder lokal → folder rilis, excludes, progress)
      gagal → hapus folder rilis, berhenti
4. build_command di dalam folder rilis (opsional)
      gagal → hapus folder rilis, berhenti
5. catat rilis yang sedang aktif (readlink current)
6. ln -sfn <rilis baru> <base>/current
7. restart_command, lalu health_command (kode keluar ≠ 0 = gagal)
      gagal → symlink dikembalikan ke rilis sebelumnya + restart lagi
8. prune: sisakan 5 rilis terbaru, jangan pernah hapus rilis aktif
```

- Semua perintah dijalankan lewat `SftpClient.exec` (kanal SSH, timeout 1800 detik untuk
  deploy) dengan path dikutip POSIX (`q()`), jadi spasi dan tanda kutip aman.
- Kegagalan dilaporkan sebagai `CoreError::Deploy { msg }` berisi langkah + baris terakhir
  stderr/stdout. Log lengkap (`$ perintah`, keluaran, keputusan) dikembalikan di
  `DeployOutcome.log`.
- `rollback(base, release, restart, health)` mengaktifkan kembali rilis lama; bila
  restart/health gagal, `current` dikembalikan ke keadaan sebelumnya.
- `keep_releases` minimal 1. UI saat ini memakai 5 (`DeployModel.Config`).

### 8.3 Sisi UI

- `DeployModel` menyimpan satu `Deployer` per kombinasi `project|host|port|user`; ganti
  project/host akan menutup koneksi lama.
- `DeployBridge` mengimplementasikan `DeployProgress` (foreign trait UniFFI): langkah dan
  detail dikirim ke UI lewat `DispatcherQueue`, dan tombol batal membalik flag `Cancelled`.
- Folder lokal dipilih di halaman Deploy dan disimpan per perangkat
  (`deploy.local.<projectId>`); definisi project (host, path, perintah) tetap di vault.
- Riwayat deploy tidak disimpan lokal: daftar rilis selalu dibaca dari server
  (`Deployer.releases`), dan log hanya hidup selama sesi halaman.
- Eksklusi default: `.git`, `.gitignore`, `.gitattributes`, `node_modules`, `.venv`,
  `venv`, `__pycache__`, `.idea`, `.vscode`, `.DS_Store`.

### 8.4 Yang belum diverifikasi

Deploy sudah lulus unit test (penamaan rilis, tabrakan nama, kutipan shell) dan kompilasi,
tetapi **belum diuji end-to-end ke server SSH nyata**. Saat menguji pertama kali, periksa:
upload bersarang, symlink relatif/absolut, `sudo systemctl restart` tanpa TTY, health check
yang benar-benar menunggu service siap, dan rollback saat restart sengaja digagalkan.

## 9. Build, binding, dan rilis

```text
core/ (Rust)  --cargo build --release-->  zevn_core.dll
      │                                        │
      │ uniffi-bindgen-cs                      │ disalin ke Native/<arsitektur>
      ▼                                        ▼
Core/Generated/*.cs  ─────────────►  dotnet publish  ──►  Inno Setup  ──►  dist/
```

- Binding C# dan DLL **tidak masuk git** (`Core/Generated/`, `Native/`). Setiap perubahan
  API Rust wajib diikuti build core + generate ulang binding sebelum `dotnet build`.
- `scripts/build-core-windows.ps1` membangun kedua arsitektur dan menyalin DLL.
- Nomor versi hanya ditulis di `VERSION`; `scripts/set-version.ps1` menyamakan
  `core/Cargo.toml`, `core/Cargo.lock`, `ZevnShell.iss`, dan default skrip installer.
  Skrip installer menolak berjalan bila versi tidak seragam.
- Installer memakai Inno Setup 6, dipasang per pengguna, tanpa hak administrator.
- Di balik layar, `Directory.Build.props` membaca `VERSION` supaya `dotnet publish` tanpa
  `-p:Version` tetap benar.

Perintah yang biasa dipakai saat mengembangkan (dari akar repo):

```powershell
# tes inti (NASM tidak terpasang di mesin ini)
cd core; $env:AWS_LC_SYS_PREBUILT_NASM=1; cargo test --lib

# tes integrasi Live Logs (server SSH russh di loopback, tanpa sshd/Docker)
cd core; cargo test --test logs_local

# build core release + salin DLL
powershell -ExecutionPolicy Bypass -File scripts\build-core-windows.ps1

# regenerate binding C#
~/.cargo/bin/uniffi-bindgen-cs.exe --library target/x86_64-pc-windows-msvc/release/zevn_core.dll `
  --out-dir ../apps/windows/ZevnShell/Core/Generated

# build aplikasi
dotnet build apps/windows/ZevnShell/ZevnShell.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

## 10. Resep menambah fitur

### Menambah field ke record yang sudah ada

1. Tambah field dengan `#[serde(default)]` di `core/src/model.rs`.
2. Sesuaikan konstruktor di UI (`HostExt.Blank()`, `HostExt.BlankProject()`, `BlankForward`
   di `Core/Support.cs`).
3. Perbarui editor/form yang relevan.
4. Build core + regenerate binding + `dotnet build`.

Field baru otomatis ikut sync karena seluruh `VaultData` diserialisasi.

### Menambah koleksi baru di vault

1. Struct di `model.rs` + `impl_record!` + field baru di `VaultData`.
2. Aksesor `xxx()`, `upsert_xxx()`, `delete_xxx()` di `impl Vault` (`vault.rs`).
3. Tambahkan `merge_records(...)` di `merge_remote` — jangan dilewatkan.
4. Ekspor tipe di `lib.rs` bila dipakai UI.
5. Tes CRUD + sync seperti `projects_crud_and_sync`.
6. Regenerate binding, lalu sambungkan ke `AppModel` (`Reload`, `DataChanged`).

### Menambah halaman/section

1. `Section` + `SectionInfo.Title/Glyph/Tint` di `AppModel.cs`.
2. `Section.Main` (sidebar) bila halaman utama.
3. Route di `ShellPage.cs` dan badge jumlah bila perlu.
4. Halaman baru turunan `PageBase` dengan kata kunci perubahan yang tepat.
5. `AppModel.Navigate(Section.X)` dari tempat lain.

### Menambah operasi jarak jauh

- Butuh kanal perintah → `SftpClient.exec` (atau `Deployer` bila bagian dari rilis).
- Butuh unggah massal → `SftpClient.upload_into` dengan daftar `excludes`.
- Beri progress lewat trait `TransferProgress`/`DeployProgress` dan dukung pembatalan.
- Semua kegagalan dikembalikan sebagai `CoreError`, jangan `panic!`.

## 11. Batasan yang diketahui

- **Panel log per project** sudah ada di halaman Live Logs (`LogStream` + `LogModel`):
  perintah disimpan di project (`log_host_id`, `log_command`), keluaran dibatasi 2000 baris,
  dan sesi dihentikan saat project berganti atau vault dikunci. Alur SSH-nya diuji otomatis
  terhadap server russh di loopback (`core/tests/logs_local.rs`: baris terpotong, stderr,
  exit status, `stop`, dan koneksi dipakai ulang), tetapi belum diuji ke sshd/OpenSSH nyata.
  Belum ada penanda warna per tingkat log, pencarian di dalam log, atau ekspor.
- **Tunnel per project** baru sebatas auto-start; belum ada preset atau status detail
  (port, lokal/remote, proses mati karena apa).
- **Deploy** belum menyimpan riwayat lokal, belum mendukung rsync/delta, dan belum
  menangani service yang butuh TTY.
- **Terminal** bergantung pada `ssh.exe` yang tersedia di Windows; kunci sesi sementara
  ditulis di `%LOCALAPPDATA%\Zevn Shell\session-keys` dan dihapus saat sesi berakhir.
- Binding C# harus selalu diregenerasi setelah perubahan API; build yang gagal dengan
  "method not found" biasanya berarti langkah ini terlewat.

## 12. Di mana mencari sesuatu

| Pertanyaan | Berkas |
|---|---|
| Bentuk data / field record | `core/src/model.rs` |
| Cara data disimpan & digabung | `core/src/vault.rs`, `core/src/crypto.rs` |
| Perilaku koneksi SSH & known_hosts | `core/src/ssh.rs` |
| Perintah SFTP/exec/upload | `core/src/sftp.rs` |
| Streaming log service | `core/src/logs.rs`, `Core/LogModel.cs` |
| Alur deploy & rollback | `core/src/deploy.rs` |
| State aplikasi & navigasi | `apps/windows/ZevnShell/Core/AppModel.cs` |
| Path data & terjemahan error | `apps/windows/ZevnShell/Core/Support.cs` |
| Routing halaman | `apps/windows/ZevnShell/Views/ShellPage.cs` |
| Terminal & tunnel | `apps/windows/ZevnShell/Terminal/` |
| Versi & installer | `VERSION`, `scripts/` |
