# Build Zevn Shell (Release) dan buat installer .exe per arsitektur.
# Syarat: zevn_core.dll + binding C# sudah dibuat (scripts\build-core-windows.ps1), .NET 9 SDK, Inno Setup 6.3+.
#   powershell -ExecutionPolicy Bypass -File scripts\build-windows-installer.ps1 [-Version x.y.z] [-Arch ARM64,x64]
# Versi default dibaca dari VERSION di akar repo; ubah lewat scripts\set-version.ps1.
# Hasil: dist\Zevn-Shell-<versi>-Windows-<arch>-Setup.exe
param(
    [string]$Version = "0.1.1",
    [ValidateSet("ARM64", "x64")] [string[]]$Arch = @("ARM64", "x64")
)
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$win = Join-Path $root "apps\windows"
$proj = Join-Path $win "ZevnShell\ZevnShell.csproj"
$dist = Join-Path $root "dist"
$versionFile = Join-Path $root "VERSION"
if (-not $Version) { $Version = (Get-Content $versionFile -Raw).Trim() }
$Version = $Version.Trim().TrimStart("v")
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versi '$Version' tidak berbentuk x.y.z" }

# Aplikasi, installer, dan isi zevn_core.dll harus memakai versi yang sama.
# Kalau tidak, halaman Tentang akan melaporkan dua versi berbeda seperti sebelumnya.
$pinned = (Get-Content $versionFile -Raw).Trim()
$coreVersion = (Select-String -Path (Join-Path $root "core\Cargo.toml") -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
if ($pinned -ne $Version -or $coreVersion -ne $Version) {
    throw "Versi tidak seragam: VERSION=$pinned, core/Cargo.toml=$coreVersion, -Version=$Version. Jalankan scripts\set-version.ps1 -Version $Version lalu bangun ulang zevn_core.dll."
}

New-Item -ItemType Directory -Force $dist | Out-Null

$iscc = @((Get-Command iscc.exe -ErrorAction SilentlyContinue).Source) +
        @(Get-ChildItem "${env:ProgramFiles(x86)}\Inno Setup *\ISCC.exe", "$env:ProgramFiles\Inno Setup *\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup *\ISCC.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName) |
        Where-Object { $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe (Inno Setup 6.3+) tidak ditemukan" }

foreach ($a in $Arch) {
    $rid = if ($a -eq "ARM64") { "win-arm64" } else { "win-x64" }
    $out = Join-Path $win "publish\$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $proj -c Release -p:Platform=$a -r $rid -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $a gagal" }

    $innoArch = $a.ToLower()
    & $iscc /Q "/DAppVersion=$Version" "/DArch=$innoArch" "/DSourceDir=$out" "/DOutputDir=$dist" (Join-Path $win "installer\ZevnShell.iss")
    if ($LASTEXITCODE -ne 0) { throw "iscc $a gagal" }
}
Get-ChildItem $dist -Filter "*-Setup.exe" | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
