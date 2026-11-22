---
name: memoire
description: Chercher ou écrire dans la mémoire centrale ClaudeHub (~/OneDrive/ClaudeHub) — retrouver ce qu'on a fait un jour donné, une ancienne conversation ou un projet ; retenir quelque chose ; résumer la journée. Utiliser quand l'utilisateur dit « cherche dans ta mémoire », « on a fait quoi le… », « tu te souviens… », « retrouve la conversation… », « retiens… », « résume la journée ».
---

Hub : `~/OneDrive/ClaudeHub`. Toujours viser le minimum de lecture.

## Chercher
1. **Un jour précis** → lire `journal/AAAA/AAAA-MM-JJ.md` (résumé + liens vers les conversations). N'ouvrir une conversation que si le résumé ne suffit pas.
2. **Un sujet** → Grep (insensible à la casse, mode `files_with_matches`) dans `journal/`, puis `memoire/`, puis `projets/*/NOTES.md`, puis `projets/*/conversations/`. Ouvrir seulement les 1 à 3 meilleurs fichiers, avec `offset/limit` autour des lignes trouvées si un fichier est long.
3. **Un projet** → `projets/<nom>/NOTES.md`.
4. Répondre en citant la date et le fichier source. Si rien n'est trouvé, le dire simplement.

## Retenir
- Souvenir durable (préférence, fait sur l'utilisateur, décision) → créer ou mettre à jour un fichier court dans `memoire/` et ajouter sa ligne dans `memoire/index.md`. Vérifier d'abord qu'un fichier existant ne couvre pas déjà le sujet.
- Seulement si c'est utile dans presque toutes les sessions → l'ajouter aussi, en une ligne, à `memoire/00-noyau.md`.
- Avancement d'un projet → mettre à jour `projets/<nom>/NOTES.md`.

## Résumer la journée
Ajouter ou remplacer une section `## Résumé` en haut de `journal/AAAA/AAAA-MM-JJ.md` (après le titre) : 3 à 8 puces sur ce qui a été fait, décidé et ce qui reste à faire. Ne pas toucher à la section `## Conversations` (remplie automatiquement).
