# Zevn Shell — Rancangan

Dokumen ini menjelaskan apa yang sedang dibangun, mengapa disusun seperti ini, dan apa saja batasannya. Bagian lain yang lebih ringkas untuk pengguna ada di [README.md](README.md).

## 1. Sasaran

Pemilik server biasanya berpindah antara terminal, klien SFTP, alat administrasi database, dan pembuat terowongan hanya untuk satu pekerjaan yang sama. Zevn Shell menyatukan keempatnya dalam satu aplikasi desktop yang sama sekali bukan halaman web terbungkus: antarmuka asli Windows, penyimpanan data asli, dan satu pustaka inti yang bisa dipakai ulang di sistem operasi lain.

Pengguna yang dituju: pengembang, administrator sistem, dan orang yang mengelola satu sampai puluhan VPS.

## 2. Bentuk solusi

Tiga lapisan, dipisah tegas:

1. **Inti (`core/`, Rust).** Semua urusan yang tidak boleh salah: enkripsi vault, protokol SSH/SFTP, koneksi database, pembuatan kunci, terowongan, pembacaan statistik server. Diekspor ke bahasa lain dengan UniFFI (Swift dan C#).
2. **Antarmuka (`apps/windows/`).** WinUI 3 di atas .NET 9. Hanya mengurus tampilan, navigasi, dan panggilan ke inti.
3. **Berkas hasil (`dist/`).** Paket pemasangan per arsitektur, dibuat Inno Setup dari hasil `dotnet publish`.

Konsekuensinya: perbaikan protokol atau kriptografi cukup ditulis sekali di Rust, dan tampilan tiap sistem operasi tetap terasa asli.

## 3. Bagian inti

| Modul | Tugas |
|---|---|
| `vault` | Membuka, menyegel, dan menyimpan catatan terenkripsi |
| `crypto` | Penurunan kunci Argon2id, penyandian XChaCha20-Poly1305 |
| `ssh` | Koneksi, autentikasi, eksekusi perintah, PTY (russh) |
| `sftp` | Daftar direktori, unggah, unduh, ubah izin |
| `db` | Delapan mesin database, kueri, struktur tabel, ekspor |
| `keys` | Pembuatan dan impor kunci Ed25519/RSA/ECDSA |
| `proxy` | SOCKS5 dan HTTP untuk jalur koneksi |
| `server_info` | OS, kernel, CPU, memori, disk, lalu lintas jaringan |
| `model` | Tipe data bersama yang ikut dihasilkan ke C# dan Swift |

Terminal di aplikasi Windows memakai ConPTY, sehingga sesi lokal dan sesi SSH tampil dengan pengalaman yang sama.

## 4. Bentuk data

Semua catatan disimpan dalam satu vault. Tiap entri membawa penanda waktu perubahan dan tanda hapus (tombstone), supaya dua perangkat yang menyunting bersamaan masih bisa digabung tanpa kehilangan data.

Entitas utamanya: host (alamat, port, pengguna, cara autentikasi, grup, warna, catatan), grup sebagai pengelompok host, kunci SSH, snippet perintah, aturan port forwarding, koneksi database, dan daftar fingerprint host yang dikenal.

## 5. Kerahasiaan

- Satu master password menurunkan kunci lewat Argon2id; isi vault disandikan XChaCha20-Poly1305.
- Master password tidak pernah disimpan, dan kunci vault hanya berada di memori selama aplikasi terbuka.
- Pintu masuk cepat disediakan lewat Windows Hello di Windows; kunci pembungkusnya disimpan di penyimpanan kredensial sistem.
- Vault terkunci sendiri setelah tidak dipakai beberapa lama.
- Saat sinkronisasi aktif, yang berpindah ke Google Drive hanyalah ciphertext. Cakupan izinnya `drive.appdata`, jadi aplikasi tidak bisa melihat berkas lain milik pengguna.

## 6. Alur sinkronisasi

Perubahan ditulis ke berkas lokal lebih dulu. Setelah jeda singkat, versi terbaru diunggah ke `appDataFolder` Google Drive. Sebelum menimpa, waktu modifikasi di sisi Drive diperiksa: bila sudah berbeda, versi di sana diambil, entri digabung per record (yang penanda waktunya paling baru dipakai), lalu hasil gabungan diunggah. Login memakai OAuth 2.0 + PKCE dengan browser sistem dan pengalihan ke loopback. Lapisan ini dibuat berpola penyedia (`SyncProvider`) agar Dropbox atau WebDAV bisa ditambahkan kemudian tanpa menyentuh bagian lain.

## 7. Pemasangan dan rilis

- Windows: `dist\Zevn-Shell-<versi>-Windows-<arsitektur>-Setup.exe`, dipasang per pengguna tanpa hak administrator.
- Inti Rust dibangun per arsitektur lebih dulu; DLL-nya ikut dibundel ke folder `Native\<arsitektur>` sebelum aplikasi di-publish.
- Tanpa sertifikat berbayar, peringatan SmartScreen pada Windows dan Gatekeeper pada macOS diatasi lewat "Run anyway" / "Open Anyway".
- Versi ditentukan saat membangun (`-Version`), dan metadata Company/Product di installer mengikuti isi `ZevnShell.csproj` serta `ZevnShell.iss`.

## 8. Urutan pengerjaan

| Tahap | Isi |
|---|---|
| A | Model data, vault terenkripsi, binding, onboarding, pengelola host |
| B | SSH di inti, kunci, known hosts, terminal multi-tab |
| C | SFTP, port forwarding, snippet, panel info server |
| D | Penjelajah database dan koneksi lewat terowongan |
| E | Sinkronisasi Drive, pemeriksa pembaruan, paket ARM64 |
| F | Penyelesaian macOS memakai inti yang sama |

## 9. Yang sengaja tidak dilakukan

- Tidak ada akun layanan milik pembuat aplikasi; sinkronisasi memakai penyimpanan pengguna sendiri.
- Tidak ada telemetri atau iklan.
- Tidak membungkus antarmuka web ke dalam aplikasi desktop sebagai jalan pintas.
- Tidak meminta hak administrator hanya untuk memasang aplikasi.
