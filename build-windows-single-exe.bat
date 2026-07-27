@echo off
setlocal

set "PROJECT_DIR=%~dp0windows-app\AIClockBridge"
set "PROJECT_FILE=%PROJECT_DIR%\AIClockBridge.csproj"
set "OUTPUT_DIR=%PROJECT_DIR%\publish-single"

echo.
echo ==============================================
echo  AIClockBridge Windows single-file EXE build
echo  Configured port: 18765
echo ==============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 8 SDK was not found. Install it and try again:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

if not exist "%PROJECT_FILE%" (
    echo Project file not found: %PROJECT_FILE%
    exit /b 1
)

dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%OUTPUT_DIR%"
if errorlevel 1 (
    echo.
    echo Build failed. Review the messages above.
    exit /b 1
)

echo.
echo Build succeeded:
echo %OUTPUT_DIR%\AIClockBridge.exe
exit /b 0
