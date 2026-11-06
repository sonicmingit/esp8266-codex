$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$output = Join-Path $PSScriptRoot '../../dist'
$null = New-Item -ItemType Directory -Path $output -Force
$exe = Join-Path $output 'AIClockReceiver.exe'
if (Test-Path -LiteralPath $exe) {
    try { [IO.File]::Open($exe, 'Open', 'Write', 'Read').Dispose() }
    catch [IO.IOException] {
        # The previous version can stay open while its replacement is tested.
        $output = Join-Path $output 'receiver-update'
        $null = New-Item -ItemType Directory -Path $output -Force
        $exe = Join-Path $output 'AIClockReceiver.exe'
    }
}
$argsList = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
    "/out:$exe", "/win32icon:$PSScriptRoot/../AIClockBridge/Assets/happy-mac.ico",
    '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xaml.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')
foreach ($assembly in 'WindowsBase','PresentationCore','PresentationFramework') {
    $argsList += "/reference:$framework/WPF/$assembly.dll"
}
foreach ($file in 'Receiver.xaml','Settings.xaml','PetPicker.xaml','DialogTheme.xaml') {
    $argsList += "/resource:$PSScriptRoot/$file,$file"
}
$sprites = Join-Path $PSScriptRoot '../../watch-app/res/drawable-nodpi'
foreach ($name in 'claude','codex') {
    foreach ($frame in 0..5) {
        $file = "${name}_$frame.png"
        $argsList += "/resource:$sprites/$file,$file"
    }
}
$argsList += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
& "$framework/csc.exe" @argsList
if ($LASTEXITCODE -ne 0) { throw 'Receiver compilation failed' }
Get-Item -LiteralPath $exe
