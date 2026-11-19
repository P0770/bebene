# Bébène

Compagnon de bureau Windows : un chat noir en pixel art qui vit sur l'écran et ouvre une discussion propulsée par **Claude Code**.

- Discussion (projets ClaudeHub, conversations flash, quota restant, glisser-déposer)
- Calendrier, examens et rendez-vous (webcal/.ics, Gmail, Outlook classique), rappels
- Correcteur d'orthographe global (Windows, local)
- Assistant d'installation, verrou anti auto-modification

Liste complète des fonctions : `fonctions.json`.

## Compiler
C# 5 / WinForms + WebView2, compilé avec `csc.exe` du .NET Framework 4 :

```powershell
.\build.ps1            # compile dans %USERPROFILE%\ClaudeCompanion et relance
.\build.ps1 -Install   # + raccourci du menu Démarrer et démarrage avec Windows
```

Paquet à partager : double-clic sur `Créer le paquet Bébène.cmd`.
