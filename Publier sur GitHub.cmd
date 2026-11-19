@echo off
rem Double-clic : compile Bébène, envoie le code sur GitHub et publie la version (zip téléchargeable)
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publier-version.ps1"
pause
