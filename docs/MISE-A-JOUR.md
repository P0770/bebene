# Faire évoluer Bébène et publier une mise à jour

Ce guide explique comment modifier Bébène, tester, puis mettre à jour le dépôt GitHub et publier une version téléchargeable.

> **Règle d'or** : Bébène ne peut pas se modifier lui-même depuis ses conversations (verrou de sécurité).
> Toute évolution se fait avec **Claude Code dans le terminal**, ouvert dans ce dossier.

---

## Les deux niveaux de mise à jour

| | Quoi | Quand |
|---|---|---|
| **Envoyer le code** (*commit + push*) | Le code du dépôt est mis à jour sur GitHub. | À chaque avancée, même petite. |
| **Publier une version** (*Release*) | En plus, une page « Bébène 1.31 » est créée dans **Releases**, avec le zip prêt à installer et la liste des nouveautés. | Quand une nouvelle version est prête à être utilisée ou partagée. |

Le script **`Publier sur GitHub.cmd`** fait les deux : il envoie toujours le code, et crée la Release **seulement si le numéro de version est nouveau**.

---

## Pas à pas

### 1. Ouvrir Claude Code dans le projet

```powershell
cd "$env:USERPROFILE\OneDrive\ClaudeHub\projets\claude-companion"
claude
```

Ou depuis Bébène : Réglages → **Ouvrir Claude Code**, puis `cd` vers le dossier.

### 2. Demander la modification

Exemple : *« Ajoute à Bébène une animation quand il éternue »*.
Claude Code modifie `Companion.cs`, `ui.html`, `settings.html`…

### 3. Tester

```powershell
.\build.ps1
```

Compile dans `%USERPROFILE%\ClaudeCompanion` et relance Bébène. Des modes de test produisent des images sans toucher au bureau :

| Commande | Résultat |
|---|---|
| `ClaudeCompanion.exe --preview poses.png` | planche de toutes les poses du chat |
| `ClaudeCompanion.exe --readytest out.png` | Bébène content qui attend qu'on lise sa réponse |
| `ClaudeCompanion.exe --swingtest out.png` | le chat attrapé et secoué, image par image |
| `ClaudeCompanion.exe --sidepreview cal.ics out.png` | calendrier, cartes examens/RDV et flèches |
| `ClaudeCompanion.exe --remindtest cal.ics out.png` | carte de rappel |
| `ClaudeCompanion.exe --paneltest out.png` | la vraie fenêtre de discussion |
| `ClaudeCompanion.exe --caltest out.json` | lecture des calendriers configurés |
| `ClaudeCompanion.exe --mailtest dossier_eml out.txt` | extraction examens/RDV depuis des mails `.eml` |
| `ClaudeCompanion.exe --manualtest cal.ics out.txt` | ajout/retrait manuel d'événements |
| `ClaudeCompanion.exe --spelltest` / `--spelldemo "texte"` | correcteur d'orthographe |
| `ClaudeCompanion.exe --setuptest out.txt` | état des étapes de l'assistant d'installation |
| `ClaudeCompanion.exe --guardtest out.txt` | préparation du verrou de sécurité |

### 4. Changer le numéro de version (seulement pour une nouvelle Release)

Dans `Companion.cs`, **les deux lignes** :

```csharp
[assembly: System.Reflection.AssemblyVersion("1.31.0.0")]
...
public const string Version = "1.31";
```

Conventions :
- `1.30` → `1.31` : nouvelle fonction ou amélioration visible ;
- `1.31` → `1.31.1` : petite correction (`AssemblyVersion("1.31.1.0")`).

### 5. Décrire les nouveautés dans `fonctions.json`

Chaque fonction nouvelle ou améliorée a une entrée :

```json
{ "cat": "Le chat", "titre": "Éternuement", "depuis": "1.31",
  "desc": "De temps en temps, Bébène éternue en secouant la tête.",
  "ex": "Observe-le quelques minutes" }
```

Cette entrée sert à trois endroits :
1. dans les **réglages** de Bébène (« Ce que Bébène sait faire », avec un badge *Nouveau*) ;
2. dans les **notes de la Release** GitHub : toutes les entrées dont `depuis` vaut la version publiée y sont listées ;
3. dans le **README** si tu veux la mettre en avant.

### 6. Publier

Double-clic sur **`Publier sur GitHub.cmd`**. Le script :

1. **compile** (`build.ps1`), pour que le zip corresponde exactement au code ;
2. demande **« Qu'est-ce qui a changé ? »** : une phrase, qui sert de message de commit et d'introduction aux notes ;
3. **enregistre et envoie** le code (`git add -A`, `git commit`, `git push`) ;
4. si la version n'existe pas encore sur GitHub :
   - crée le zip (`creer-paquet.ps1` : app compilée + modèle de mémoire sans tes infos personnelles + LISEZMOI),
   - crée la **Release** `v1.31` avec le zip en pièce jointe et les nouveautés.

Si la version existe déjà, seul le code est envoyé, et le script te rappelle d'augmenter le numéro.

Équivalent en ligne de commande :

```powershell
.\publier-version.ps1 -Message "Bébène éternue maintenant"
```

Ou demande-le simplement à Claude Code : *« publie la nouvelle version de Bébène sur GitHub »*.

---

## Mettre à jour Bébène sur un PC

1. Sur GitHub → **Releases** → télécharge le dernier zip.
2. Quitte Bébène : clic droit sur son icône → **Quitter**.
3. Dézippe par-dessus l'ancienne installation (ou ailleurs, puis lance `ClaudeCompanion.exe` : l'assistant propose d'installer).
4. Relance. Réglages, conversations et calendriers sont conservés (registre et stockage local).

---

## Ce qui n'est jamais envoyé sur GitHub

Exclu par `.gitignore` :
- `conversations/` : tes conversations archivées par ClaudeHub ;
- `paquets/` et `*.zip` : les zips (ils vont dans les Releases, pas dans le code) ;
- `*.exe`, `*.dll`, `*.pdb` : les fichiers compilés.

Les réglages personnels (registre), les mails, les mots de passe et l'historique des discussions ne sont jamais dans le dossier du projet.

---

## Prérequis (une seule fois)

```powershell
winget install GitHub.cli
gh auth login --web --git-protocol https
```

## En cas de problème

| Message | Solution |
|---|---|
| `GitHub CLI introuvable` | `winget install GitHub.cli`, puis rouvrir le terminal |
| `Envoi refusé par GitHub` | `gh auth login` pour se reconnecter |
| `Bébène n'est pas compilé` | lancer `.\build.ps1` une fois |
| Erreur de compilation | lire la ligne `Companion.cs(ligne,colonne)` affichée et la donner à Claude Code |
