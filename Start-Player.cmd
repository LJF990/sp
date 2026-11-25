@echo off
setlocal
cd /d "%~dp0"
set "PLAYER_CONFIG=%~dp0artifacts\local-player.json"
if not "%~1"=="" set "PLAYER_CONFIG=%~f1"
"%~dp0Server\bin\Release\net8.0-windows\Server.exe" local-player "%~dp0Player\bin\Release\net8.0-windows\Player.exe" "%PLAYER_CONFIG%"
if errorlevel 1 pause
