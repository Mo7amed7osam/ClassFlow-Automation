@echo off
rem Starts the Claude runner in this folder. Leave the window open while you want Claude to build/run here.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0claude-runner.ps1"
pause
