[CmdletBinding()]
param(
    [string]$Jdk = $env:JAVA_HOME,
    [string]$BuildTools = (Join-Path $PSScriptRoot '.tools/sdk-build/android-15'),
    [string]$PlatformJar = (Join-Path $PSScriptRoot '.tools/sdk-platform/android-35/android.jar'),
    [string]$KeyStore = (Join-Path $PSScriptRoot '.signing/debug.keystore')
)
$ErrorActionPreference = 'Stop'
if (!$Jdk) { $Jdk = Split-Path (Split-Path (Get-Command javac.exe -ErrorAction Stop).Source -Parent) -Parent }
foreach ($path in @("$Jdk/bin/javac.exe", "$Jdk/bin/java.exe", "$Jdk/bin/jar.exe", "$BuildTools/aapt2.exe", "$BuildTools/zipalign.exe", "$BuildTools/lib/d8.jar", "$BuildTools/lib/apksigner.jar", $PlatformJar)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing $path. Set tool parameters or run ./setup-tools.ps1" }
}
function Run([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited $LASTEXITCODE" }
}
$build = Join-Path $PSScriptRoot ('.build/' + [Guid]::NewGuid().ToString('N'))
$gen = Join-Path $build 'gen'; $classes = Join-Path $build 'classes'; $dex = Join-Path $build 'dex'
New-Item -ItemType Directory -Force -Path $build,$gen,$classes,$dex | Out-Null
$dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../dist'))
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Run "$BuildTools/aapt2.exe" @('compile', '--dir', "$PSScriptRoot/res", '-o', "$build/resources.zip")
Run "$BuildTools/aapt2.exe" @('link', '-I', $PlatformJar, '--manifest', "$PSScriptRoot/AndroidManifest.xml", '--java', $gen, '-o', "$build/unsigned.apk", "$build/resources.zip")
$sources = @(Get-ChildItem -LiteralPath "$PSScriptRoot/src",$gen -Recurse -Filter '*.java' | ForEach-Object { '"' + $_.FullName.Replace('\','/') + '"' })
$sources | Set-Content -LiteralPath "$build/sources.txt" -Encoding utf8
Run "$Jdk/bin/javac.exe" @('-J-Duser.language=en', '-encoding', 'UTF-8', '--release', '8', '-classpath', $PlatformJar, '-d', $classes, "@$build/sources.txt")
Run "$Jdk/bin/jar.exe" @('cf', "$build/classes.jar", '-C', $classes, '.')
Run "$Jdk/bin/java.exe" @('-cp', "$BuildTools/lib/d8.jar", 'com.android.tools.r8.D8', '--lib', $PlatformJar, '--min-api', '26', '--output', $dex, "$build/classes.jar")
Run "$Jdk/bin/jar.exe" @('uf', "$build/unsigned.apk", '-C', $dex, 'classes.dex')
Run "$BuildTools/zipalign.exe" @('-f', '4', "$build/unsigned.apk", "$build/aligned.apk")
if (!(Test-Path -LiteralPath $KeyStore)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $KeyStore -Parent) | Out-Null
    Run "$Jdk/bin/keytool.exe" @('-genkeypair', '-keystore', $KeyStore, '-storepass', 'android', '-keypass', 'android', '-alias', 'androiddebugkey', '-dname', 'CN=AIClock Watch Development', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000')
}
$apk = Join-Path $dist 'AIClockWatch-0.2.0.apk'
Run "$Jdk/bin/java.exe" @('-jar', "$BuildTools/lib/apksigner.jar", 'sign', '--ks', $KeyStore, '--ks-pass', 'pass:android', '--key-pass', 'pass:android', '--out', $apk, "$build/aligned.apk")
Run "$Jdk/bin/java.exe" @('-jar', "$BuildTools/lib/apksigner.jar", 'verify', '--verbose', $apk)
Run "$BuildTools/zipalign.exe" @('-c', '4', $apk)
Run "$BuildTools/aapt2.exe" @('dump', 'badging', $apk)
Write-Host "APK: $apk"
Get-FileHash -LiteralPath $apk -Algorithm SHA256
