[CmdletBinding()]
param([string]$Proxy)
$ErrorActionPreference = 'Stop'
$toolRoot = Join-Path $PSScriptRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$archives = @(
    @{ File = 'build-tools.zip'; Url = 'https://dl.google.com/android/repository/build-tools_r35_windows.zip'; Sha1 = 'af059bb67cf7786f45ee0db85e2d24985df1b4b6'; Folder = 'sdk-build'; Check = 'sdk-build/android-15/aapt2.exe' },
    @{ File = 'platform.zip'; Url = 'https://dl.google.com/android/repository/platform-35_r02.zip'; Sha1 = '0bb560a90a7a2cbd0dd8348224d518b638fe7949'; Folder = 'sdk-platform'; Check = 'sdk-platform/android-35/android.jar' }
)
foreach ($item in $archives) {
    if (Test-Path -LiteralPath (Join-Path $toolRoot $item.Check)) { continue }
    $archive = Join-Path $toolRoot $item.File
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ne $item.Sha1) {
        $downloadArgs = @('-sS', '-L', '--fail', '--retry', '2', '--max-time', '180')
        if ($Proxy) { $downloadArgs += @('--proxy', $Proxy) }
        & curl.exe @downloadArgs $item.Url '-o' $archive
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($item.Url)" }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ne $item.Sha1) { throw 'Android SDK checksum mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot $item.Folder) -Force
}
$json = Join-Path $toolRoot 'json.jar'
if (!(Test-Path -LiteralPath $json)) {
    $downloadArgs = @('-sS', '-L', '--fail', '--retry', '2', '--max-time', '60')
    if ($Proxy) { $downloadArgs += @('--proxy', $Proxy) }
    & curl.exe @downloadArgs 'https://repo.maven.apache.org/maven2/org/json/json/20240303/json-20240303.jar' '-o' $json
    if ($LASTEXITCODE -ne 0) { throw 'Test JSON library download failed' }
}
if ((Get-FileHash -LiteralPath $json -Algorithm SHA256).Hash -ne '3cf6cd6892e32e2b4c1c39e0f52f5248a2f5b37646fdfbb79a66b46b618414ed') { throw 'Test JSON library checksum mismatch' }
Write-Host 'Android SDK 35 and Build Tools 35.0.0 ready. Build with ./build.ps1'
