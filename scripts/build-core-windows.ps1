# Build zevn-core untuk Windows (ARM64 + x64) dan buat binding C#.
# Jalankan di Windows: powershell -ExecutionPolicy Bypass -File scripts\build-core-windows.ps1
# Bukan "Stop": cargo menulis progres ke stderr, dan PowerShell 5.1 menganggapnya error.
# Kegagalan dicek lewat $LASTEXITCODE.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$core = Join-Path $root "core"
$app = Join-Path $root "apps\windows\ZevnShell"
$cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE ".cargo" }
$cargo = Join-Path $cargoHome "bin\cargo.exe"
if (-not (Test-Path $cargo)) { $cargo = "cargo" }

# crate `ring` butuh clang untuk target ARM64 → pakai LLVM bawaan VS Build Tools.
# clang harus bisa jalan di mesin ini: versi ARM64 di Windows ARM, versi x64 di runner GitHub.
$vsLlvm = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Tools\Llvm\*\bin\clang.exe", "$env:ProgramFiles\Microsoft Visual Studio\*\*\VC\Tools\Llvm\*\bin\clang.exe" -ErrorAction SilentlyContinue |
    Sort-Object { if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { $_.FullName -notmatch "ARM64" } else { $_.FullName -match "ARM64" } } |
    Select-Object -First 1
if ($vsLlvm) { $env:Path = $vsLlvm.DirectoryName + ";" + $env:Path }
elseif (-not (Get-Command clang -ErrorAction SilentlyContinue)) { throw "clang tidak ditemukan: pasang komponen VS 'C++ Clang tools' (VC.Llvm.Clang)" }

# aws-lc-sys (x64) butuh NASM → pakai objek NASM prebuilt bawaan crate
$env:AWS_LC_SYS_PREBUILT_NASM = "1"

Push-Location $core
foreach ($t in @("aarch64-pc-windows-msvc", "x86_64-pc-windows-msvc")) {
    & $cargo build --release --lib --target $t
    if ($LASTEXITCODE -ne 0) { throw "cargo build $t gagal" }
}
Pop-Location

# Binding C# (uniffi-bindgen-cs harus sesuai versi uniffi di core: 0.32)
$bindgen = Join-Path $cargoHome "bin\uniffi-bindgen-cs.exe"
if (-not (Test-Path $bindgen)) {
    & $cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag "v0.12.0+v0.32.0"
    if ($LASTEXITCODE -ne 0) { throw "install uniffi-bindgen-cs gagal" }
}
$gen = Join-Path $app "Core\Generated"
New-Item -ItemType Directory -Force $gen | Out-Null
# Dijalankan dari folder core: bindgen membaca `cargo metadata` untuk konfigurasi crate
Push-Location $core
& $bindgen --library (Join-Path $core "target\aarch64-pc-windows-msvc\release\zevn_core.dll") --out-dir $gen
$bindgenExit = $LASTEXITCODE
Pop-Location
if ($bindgenExit -ne 0) { throw "uniffi-bindgen-cs gagal" }

# DLL native per arsitektur
foreach ($pair in @(@("arm64", "aarch64-pc-windows-msvc"), @("x64", "x86_64-pc-windows-msvc"))) {
    $dst = Join-Path $app ("Native\" + $pair[0]); New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $core ("target\" + $pair[1] + "\release\zevn_core.dll")) $dst -Force
}
Write-Output "OK: zevn_core.dll + binding C# siap"
