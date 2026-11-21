# Menetapkan versi rilis Zevn Shell dari satu tempat.
#   powershell -ExecutionPolicy Bypass -File scripts\set-version.ps1 -Version 0.1.2
#   powershell -ExecutionPolicy Bypass -File scripts\set-version.ps1 -Check
# Sumber kebenaran: VERSION di akar repo. Yang ikut disamakan:
#   core/Cargo.toml, core/Cargo.lock, apps/windows/installer/ZevnShell.iss,
#   dan default -Version di scripts/build-windows-installer.ps1.
# ZevnShell.csproj tidak menyimpan versi: Directory.Build.props membacanya dari VERSION.
param(
    [string]$Version = "",
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root "VERSION"
$cargoToml = Join-Path $root "core\Cargo.toml"
$cargoLock = Join-Path $root "core\Cargo.lock"
$iss = Join-Path $root "apps\windows\installer\ZevnShell.iss"
$buildScript = Join-Path $root "scripts\build-windows-installer.ps1"

function Get-Text($path) { [System.IO.File]::ReadAllText($path) }
function Set-Text($path, $text) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

$changed = New-Object System.Collections.Generic.List[string]

if (-not $Check) {
    if (-not $Version) { throw "Sebutkan -Version x.y.z, atau pakai -Check untuk memeriksa." }
    $Version = $Version.Trim().TrimStart("v")
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versi '$Version' tidak berbentuk x.y.z (mis. 0.1.2)." }

    Set-Text $versionFile ($Version + "`n")
    $changed.Add("VERSION")

    $t = Get-Text $cargoToml
    $new = ([regex]'(?m)^version\s*=\s*"[^"]*"').Replace($t, ('version = "' + $Version + '"'), 1)
    if ($new -ne $t) { Set-Text $cargoToml $new; $changed.Add("core/Cargo.toml") }

    $l = Get-Text $cargoLock
    $new = ([regex]'(?m)(name = "zevn-core"\r?\nversion = ")[^"]*(")').Replace($l, ('${1}' + $Version + '${2}'), 1)
    if ($new -ne $l) { Set-Text $cargoLock $new; $changed.Add("core/Cargo.lock") }

    $i = Get-Text $iss
    $new = ([regex]'(?m)^(\s*#define AppVersion ")[^"]*(")').Replace($i, ('${1}' + $Version + '${2}'), 1)
    if ($new -ne $i) { Set-Text $iss $new; $changed.Add("apps/windows/installer/ZevnShell.iss") }

    $b = Get-Text $buildScript
    $new = ([regex]'(?m)(\[string\]\$Version = ")[^"]*(")').Replace($b, ('${1}' + $Version + '${2}'), 1)
    if ($new -ne $b) { Set-Text $buildScript $new; $changed.Add("scripts/build-windows-installer.ps1") }

    foreach ($c in $changed) { "diubah: $c" }
}

$pinned = (Get-Text $versionFile).Trim()
$rows = @(
    @{ Name = "VERSION"; Value = $pinned },
    @{ Name = "core/Cargo.toml"; Value = ([regex]::Match((Get-Text $cargoToml), '(?m)^version\s*=\s*"([^"]*)"')).Groups[1].Value },
    @{ Name = "core/Cargo.lock"; Value = ([regex]::Match((Get-Text $cargoLock), '(?m)name = "zevn-core"\r?\nversion = "([^"]*)"')).Groups[1].Value },
    @{ Name = "ZevnShell.iss"; Value = ([regex]::Match((Get-Text $iss), '(?m)#define AppVersion "([^"]*)"')).Groups[1].Value },
    @{ Name = "build script"; Value = ([regex]::Match((Get-Text $buildScript), '(?m)\[string\]\$Version = "([^"]*)"')).Groups[1].Value }
)
$rows | ForEach-Object { "  {0,-26} {1}" -f $_.Name, $_.Value }

$bad = @($rows | Where-Object { $_.Value -ne $pinned })
if ($bad.Count -gt 0) {
    throw ("Versi belum seragam dengan VERSION=$pinned : " + (($bad | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ", ") + ". Jalankan: scripts\set-version.ps1 -Version $pinned")
}
"OK: semua komponen memakai versi $pinned"
