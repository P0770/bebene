@echo off
rem Double-clic : crée un zip de Bébène tel qu'il est maintenant, dans le sous-dossier « paquets »
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0creer-paquet.ps1"
if errorlevel 1 pause
