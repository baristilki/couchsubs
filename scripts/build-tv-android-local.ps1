# SPDX-License-Identifier: AGPL-3.0-only
# Fork: Android TV APK'sını GitHub Actions olmadan bu bilgisayarda, kendi imza anahtarınla derler ve masaüstü
# uygulamasının TV paketleri klasörlerine koyar. Şifre yalnız bu pencerede sorulur; hiçbir yere yazılmaz.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-tv-android-local.ps1
param(
    [string]$Keystore = "$env:USERPROFILE\Documents\CouchSubs-imza-anahtari\couchsubs-tv-release.keystore",
    [string]$Alias = 'couchsubs-tv'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tv = Join-Path $root 'tv-android'

$gradle = Get-ChildItem "$env:USERPROFILE\.gradle\wrapper\dists\gradle-8.14.3-all\*\gradle-8.14.3\bin\gradle.bat" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gradle) { $gradle = Get-Command gradle -ErrorAction SilentlyContinue }
if (-not $gradle) { throw 'Gradle bulunamadı (8.14.3 beklenir).' }
if (-not (Test-Path $Keystore)) { throw "İmza anahtarı yok: $Keystore" }
if (-not (Test-Path (Join-Path $tv 'local.properties'))) {
    "sdk.dir=$($env:LOCALAPPDATA -replace '\\','/')/Android/Sdk" | Set-Content -Encoding ascii (Join-Path $tv 'local.properties')
}

$version = (Select-String -Path (Join-Path $tv 'app\build.gradle') -Pattern "versionName '([^']+)'").Matches[0].Groups[1].Value
Write-Host "Android TV $version derleniyor (anahtar: $Keystore)"

# Şifre anahtarın yanındaki BENI-OKU-sifre.txt'nin "Şifre : ..." satırından okunur (takma ad da "Takma ad : ...");
# dosya yoksa sorulur. Şifre ekrana yazılmaz.
$plain = $null
$note = Join-Path (Split-Path $Keystore -Parent) 'BENI-OKU-sifre.txt'
if (Test-Path $note) {
    foreach ($line in [IO.File]::ReadAllLines($note, [Text.Encoding]::UTF8)) {
        if ($line -match '^\s*(.+?)\s*:\s*(\S+)\s*$') {
            $label = $Matches[1].Trim(); $value = $Matches[2]
            if ($label -match '^.ifre$') { $plain = $value }
            elseif ($label -eq 'Takma ad' -and -not $PSBoundParameters.ContainsKey('Alias')) { $Alias = $value }
        }
    }
}
if (-not $plain) {
    $secure = Read-Host 'Imza anahtari sifresi' -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
try {
    $env:TV_KEYSTORE_FILE = $Keystore
    $env:TV_KEYSTORE_PASSWORD = $plain
    $env:TV_KEY_ALIAS = $Alias
    Push-Location $tv
    & $gradle.FullName assembleSideloadRelease --no-daemon -q
    if ($LASTEXITCODE -ne 0) { throw "Gradle derlemesi başarısız ($LASTEXITCODE)." }
}
finally {
    Pop-Location
    Remove-Item Env:TV_KEYSTORE_PASSWORD, Env:TV_KEYSTORE_FILE, Env:TV_KEY_ALIAS -ErrorAction SilentlyContinue
    $plain = $null
}

$apk = Get-ChildItem (Join-Path $tv 'app\build\outputs\apk\sideload\release\*.apk') | Select-Object -First 1
if (-not $apk) { throw 'İmzalı APK oluşmadı.' }
$name = "couchsubs-androidtv_$version.apk"
$targets = @(Join-Path $root 'dist\tv-app') + @(Get-ChildItem (Join-Path $root 'windows\src\CouchSubs.App\bin') -Recurse -Directory -Filter tv-app -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($dir in $targets) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    Get-ChildItem $dir -Filter 'couchsubs-androidtv_*.apk' | Remove-Item -Force
    Copy-Item $apk.FullName (Join-Path $dir $name)
    Write-Host "Kondu: $(Join-Path $dir $name)"
}
Write-Host "Bitti: $name"
