@echo off
rem Builds Nyakke into dist\Nyakke. Needs the .NET 10 SDK.
setlocal
cd /d "%~dp0"
dotnet publish src\Nyakke -c Release -r win-x64 --self-contained -o dist\Nyakke || exit /b 1
echo.
echo Done: %~dp0dist\Nyakke\Nyakke.exe
