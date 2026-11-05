[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Serial,
    [string]$Bridge,
    [ValidateSet('auto', 'codex', 'claude', 'dual')][string]$Mode = 'auto',
    [string]$Adb = 'adb',
    [string]$Apk = (Join-Path $PSScriptRoot '../dist/AIClockWatch-0.2.0.apk')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Apk -PathType Leaf)) { throw 'Build the APK first with ./build.ps1' }
& $Adb '-s' $Serial 'install' '-r' $Apk
if ($LASTEXITCODE -ne 0) { throw 'APK install failed' }
# Explicit serial for each command; no server restart, clearing data or stored credentials.
$launchArgs = @('-s', $Serial, 'shell', 'am', 'start', '-n', 'cn.sonic.aiclockwatch/.MainActivity', '--es', 'mode', $Mode)
if ($Bridge) {
    # Reject shell syntax before forwarding through adb shell. Interactive UI supports other valid URLs.
    if ($Bridge -notmatch '^[a-zA-Z0-9.:/\[\]_-]+$') { throw 'Bridge must be a host:port or HTTP(S) URL without shell characters' }
    $launchArgs += @('--es', 'bridge', $Bridge)
}
& $Adb @launchArgs
if ($LASTEXITCODE -ne 0) { throw 'APK launch failed' }
