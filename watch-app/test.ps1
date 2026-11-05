[CmdletBinding()]
param([string]$Jdk = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
if (!$Jdk) { $Jdk = Split-Path (Split-Path (Get-Command javac.exe).Source -Parent) -Parent }
$jsonJar = Join-Path $PSScriptRoot '.tools/json.jar'
if (!(Test-Path -LiteralPath $jsonJar)) { throw 'Run ./setup-tools.ps1 (test JSON library missing)' }
if ((Get-FileHash -LiteralPath $jsonJar -Algorithm SHA256).Hash -ne '3cf6cd6892e32e2b4c1c39e0f52f5248a2f5b37646fdfbb79a66b46b618414ed') { throw 'Test JSON library checksum mismatch' }
$output = Join-Path $PSScriptRoot '.build/tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$src = Join-Path $PSScriptRoot 'src/cn/sonic/aiclockwatch'
& "$Jdk/bin/javac.exe" '-J-Duser.language=en' '-encoding' 'UTF-8' '-cp' $jsonJar '-d' $output "$src/QuotaSnapshot.java" "$src/BridgeClient.java" "$src/BridgeProfiles.java" "$src/PetCatalog.java" "$src/WatchFace.java" "$PSScriptRoot/tests/ProtocolTests.java" "$PSScriptRoot/tests/RenderPreview.java"
if ($LASTEXITCODE -ne 0) { throw 'Watch test compilation failed' }
& "$Jdk/bin/java.exe" '-Dfile.encoding=UTF-8' '-cp' "$output;$jsonJar" 'cn.sonic.aiclockwatch.ProtocolTests'
if ($LASTEXITCODE -ne 0) { throw 'Watch protocol tests failed' }
& "$Jdk/bin/java.exe" '-Dfile.encoding=UTF-8' '-cp' "$output;$jsonJar" 'cn.sonic.aiclockwatch.RenderPreview' $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Watch layout preview failed' }
