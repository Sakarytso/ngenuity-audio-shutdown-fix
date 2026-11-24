@echo off
rem Open the installer in Windows PowerShell.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Install-Patch.ps1"
pause
