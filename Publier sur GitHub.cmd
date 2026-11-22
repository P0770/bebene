@echo off
rem Double-clic : envoie le code sur GitHub ; GitHub teste et publie la version si son numéro est nouveau
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publier-version.ps1"
pause
