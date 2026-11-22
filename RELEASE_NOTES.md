# Zevn Shell 0.1.2

Rilis ini menutup dua kebutuhan harian seorang pemilik server: mengubah konfigurasi
`.env` tanpa membuka terminal, dan tahu kondisi kode lokal sebelum menekan tombol
deploy.

## Yang baru

### Editor `.env` per project

Halaman **Env** mengedit file `.env` project langsung di server:

- Membaca dan menulis lewat SFTP, batas 1 MB; file yang belum ada ditampilkan sebagai
  "file baru" dan dibuat saat disimpan.
- Isi lama otomatis dibackup ke `<file>.bak` — berikut izin file yang sama — sebelum
  file utama ditimpa, dan path backup ditampilkan setelah menyimpan.
- Baris komentar, baris kosong, dan awalan `export ` dipertahankan apa adanya; hanya
  nilai `KEY=value` yang diedit. Urutan baris tidak berubah.
- Nilai sensitif (nama kunci mengandung `SECRET`, `TOKEN`, `PASSWORD`, `API_KEY`,
  `PRIVATE`, dan sejenisnya) tampil tersamar dengan tombol intip, plus sakelar
  "Tampilkan nilai" untuk membuka semuanya.
- Baris variabel bisa ditambah dan dihapus; berlaku setelah **Simpan**.
- Path `.env` disimpan per perangkat (bawaan `<folder rilis>/.env`), jadi tidak
  mengotori vault atau ikut tersinkron ke mesin lain.

### Status Git sebelum deploy

Halaman **Deploy** dan **Overview** kini menampilkan branch, hash pendek, dan subjek
commit terakhir dari folder lokal yang akan diunggah:

- Badge **BERSIH** / **KOTOR** beserta jumlah berkas yang belum di-commit.
- Overview memberi peringatan oranye bila working tree kotor.
- Menekan **Deploy sekarang** saat working tree kotor memunculkan konfirmasi, karena
  yang dikirim ke server adalah isi folder apa adanya — termasuk perubahan yang belum
  di-commit.
- Folder yang bukan repositori Git, Git yang tidak terpasang, atau repo milik user lain
  hanya menghasilkan keterangan; deploy tetap bisa dijalankan.

### Perbaikan

- Sidebar tidak lagi membuat ulang elemen menu saat panel dirender ulang, sehingga
  crash "Element is already the child of another element" tidak muncul lagi.

## Unduhan

| Berkas | Arsitektur |
| --- | --- |
| `Zevn-Shell-0.1.2-Windows-x64-Setup.exe` | Windows x64 (Intel/AMD) |
| `Zevn-Shell-0.1.2-Windows-arm64-Setup.exe` | Windows on ARM |

Persyaratan: Windows 10 versi 2004 (build 19041) atau lebih baru, x64 atau ARM64.
Terminal SSH memakai OpenSSH Client bawaan Windows; bila belum ada, aplikasi
menampilkan petunjuk pemasangannya.

Installer belum ditandatangani secara digital, jadi Windows SmartScreen bisa
menampilkan peringatan "Unknown publisher" saat pertama dijalankan. Pilih
**More info → Run anyway** bila Anda sudah mengunduh berkas dari halaman rilis resmi
repo ini.

## Meningkatkan dari 0.1.1

Pasang installer di atas versi lama; vault, host, project, dan pengaturan perangkat
tetap dipakai. Tidak ada migrasi data yang perlu dijalankan. Aplikasi memeriksa rilis
terbaru di GitHub, sehingga versi 0.1.1 akan menawarkan pembaruan ke 0.1.2.

## Catatan

- Editor `.env` dan deploy sudah lulus build serta tes unit, tetapi belum diuji
  end-to-end ke server SSH nyata pada rilis ini.
- Riwayat deploy tetap dibaca dari server; tidak ada riwayat lokal baru.

Lisensi MIT — dibuat oleh Wawazer.
