@echo off
rem Double-click to install or update SteamGridDB for Xbox (Community).
set SGDB_INSTALLER_PAUSE=1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
