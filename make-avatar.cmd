@echo off
setlocal

set "ROOT=%~dp0"
set "EXE=%ROOT%bin\Debug\net10.0-windows\AvatarBuilder.exe"

if not exist "%EXE%" (
    pushd "%ROOT%" >nul
    dotnet build .\AvatarBuilder.csproj --no-restore /p:UseSharedCompilation=false
    if errorlevel 1 (
        popd >nul
        exit /b %errorlevel%
    )
    popd >nul
)

start "Avatar Builder - Easy Avatar Mode" "%EXE%" --easy-avatar --output-folder "D:\Avatar Builder Output" %*
