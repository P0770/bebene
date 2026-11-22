# ClaudeHub

Mémoire et projets partagés entre Claude et moi, synchronisés par OneDrive.

| Dossier | Contenu | Chargé quand ? |
|---|---|---|
| `memoire/00-noyau.md` | L'essentiel : qui je suis, mes préférences, les règles du hub | Automatiquement, à chaque session |
| `memoire/` (le reste) | Souvenirs détaillés, listés dans `memoire/index.md` | Seulement si utile |
| `journal/AAAA/AAAA-MM-JJ.md` | Ce qu'on a fait chaque jour (résumé + conversations de projet) | Sur demande |
| `projets/<nom>/` | Un dossier par projet : `NOTES.md` + `conversations/` archivées automatiquement | Quand on travaille dessus |

Seules les conversations de **projet** sont archivées. Pour en lier une : « je travaille sur le projet X » (à Claude ou à Bébène).
| `skills/` | Les compétences de Claude (ex. `/memoire`) | Quand on les appelle |
| `outils/` | Scripts : archivage automatique et installation | — |

## Exemples de demandes
- « Cherche dans ta mémoire ce qu'on a fait le 19 novembre »
- « Retrouve la conversation où on a parlé de … »
- « Retiens que … »
- « Résume la journée »

## Installer sur un autre PC
1. Connecter le même OneDrive personnel et attendre la synchronisation.
2. Installer Claude Code : `winget install Anthropic.ClaudeCode`
3. Lancer : `powershell -ExecutionPolicy Bypass -File "$env:OneDrive\ClaudeHub\outils\installer.ps1" -App`
   (`-App` installe aussi le compagnon Bébène.)

Conseil : n'utilise pas deux PC en même temps, pour éviter les conflits de synchronisation.
