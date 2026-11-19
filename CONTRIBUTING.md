# Utiliser, modifier et proposer des changements

Merci de t'intéresser à Bébène ! Voici comment l'utiliser ou le modifier **sans jamais toucher aux versions officielles**.

## Qui peut publier quoi

| Action | Qui |
|---|---|
| Télécharger et utiliser Bébène (Releases) | tout le monde |
| Copier le code et le modifier chez soi (fork / clone) | tout le monde |
| Modifier le dépôt officiel `P0770/bebene` et publier une version | **uniquement le propriétaire** |

GitHub n'autorise l'écriture sur le dépôt officiel qu'à son propriétaire : un clone ou un fork ne peut ni pousser du code, ni créer de Release, ni modifier celles qui existent. Le script `Publier sur GitHub.cmd` vérifie en plus le compte connecté et s'arrête avec un message clair si ce n'est pas le propriétaire.

## Modifier Bébène pour soi

```powershell
git clone https://github.com/P0770/bebene.git    # ou ton fork
cd bebene
.\build.ps1                                       # compile dans %USERPROFILE%\ClaudeCompanion et relance
```

Bon à savoir :
- **Tes réglages, conversations et calendriers ne dépendent pas du code** : ils sont dans le registre (`HKCU\Software\ClaudeCompanion`) et dans `%LOCALAPPDATA%\ClaudeCompanion`. Recompiler ne les efface pas.
- **Mises à jour officielles** : Bébène compare sa version (`Version` dans `Companion.cs`) avec la dernière Release de `P0770/bebene`. Si tu as modifié ta copie, une mise à jour officielle la **remplacerait** : ne clique pas sur « Mettre à jour », ou garde un numéro de version plus grand que l'officiel dans ta copie.
- Bébène ne peut pas se modifier lui-même depuis ses conversations (verrou de sécurité) : fais tes changements avec Claude Code dans le terminal ou ton éditeur.

## Proposer un changement au projet officiel

1. **Fork** le dépôt sur GitHub, puis clone ton fork.
2. Crée une branche : `git checkout -b ma-modif`.
3. Fais ta modification, teste avec `.\build.ps1` et les modes de test (`docs/MISE-A-JOUR.md`).
4. Pour une nouvelle fonction, ajoute une entrée dans `fonctions.json`.
5. Pousse ta branche sur **ton fork** et ouvre une **pull request**.

Le propriétaire relit, décide de l'intégrer ou non, puis publie lui-même une nouvelle version.

## Règles du code

- C# 5 uniquement (compilé par le `csc.exe` du .NET Framework 4) : pas de `?.`, de `$"…"`, de `nameof` ni de fonctions locales.
- Pas de dépendance à installer : seulement Windows, .NET Framework 4 et WebView2.
- Rien de personnel dans le code (adresses, chemins propres à un PC, mots de passe).
