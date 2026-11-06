param([switch]$OnlinePets)
$ErrorActionPreference = 'Stop'
$exe = (& "$PSScriptRoot/build.ps1").FullName
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$work = Join-Path ([IO.Path]::GetTempPath()) ('receiver-test-' + [Guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $work
Copy-Item -LiteralPath $exe -Destination $work
$argsList = @('/nologo','/codepage:65001',"/reference:$exe","/out:$work/Test.exe",'/reference:System.Xaml.dll',
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')
foreach ($name in 'PresentationCore','PresentationFramework','WindowsBase') {
    $argsList += "/reference:$framework/WPF/$name.dll"
}
$testSource = (Get-Item -LiteralPath "$PSScriptRoot/tests/Test.cs").FullName
& "$framework/csc.exe" @argsList $testSource
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
$mode = if ($OnlinePets) { 'online' } else { 'offline' }
& "$work/Test.exe" "$work/settings.json" "$PSScriptRoot/../../dist/receiver-qa" $mode
if ($LASTEXITCODE -ne 0) { throw 'Receiver tests failed' }
