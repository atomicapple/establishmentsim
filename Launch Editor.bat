@echo off
set DOTNET_ROOT=%~dp0dotnet_sdk
start "" "%~dp0Godot_v4.7.1-stable_win64.exe" --path "%~dp0" --editor %*
