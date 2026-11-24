@echo off
rem Restore the HP-signed backup for the default installation.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Switch-Version.ps1" -Mode Restore
pause
