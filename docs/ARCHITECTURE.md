# Architecture technique de Bébène

Ce document explique comment Bébène fonctionne de l'intérieur : utile pour le faire évoluer avec Claude Code ou pour comprendre un bug.

---

## Vue d'ensemble

```
┌──────────────────────────── ClaudeCompanion.exe (C# WinForms, .NET Framework 4) ────────────────────────────┐
│                                                                                                             │
│  Companion (le chat)          ChatPanel (discussion)               SettingsWindow (réglages)               │
│  fenêtre « layered »   ──▶    Form + WebView2 → ui.html  ◀──▶      Form + WebView2 → settings.html         │
│  animation 60 i/s             messages texte postMessage                                                    │
│        │                             │                                                                      │
│        │                             ├── claude -p --output-format stream-json …   (une réponse = un process)│
│        │                             ├── sonde de quota (claude -p --model haiku …)                         │
│        │                             └── volets : ArrowTab ×2, CalWidget, UpWidget (fenêtres layered)       │
│        │                                                                                                    │
│  SpellAssist (correcteur)     Calendars / Mailbox / Reminders / Manual      Setup (assistant)               │
│  hooks clavier+souris, UIA    iCal, IMAP Gmail, Outlook COM                 vérifie et installe             │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
        Réglages : registre HKCU\Software\ClaudeCompanion          Journal : %LOCALAPPDATA%\ClaudeCompanion\bebene.log
```

Tout le programme tient dans **`Companion.cs`** (≈ 3 000 lignes). Il est compilé par le `csc.exe` du .NET Framework 4 présent dans Windows : **C# 5 uniquement** (pas de `?.`, pas de `$"…"`, pas de `nameof`, pas de fonctions locales).

---

## Les classes principales

| Classe | Rôle |
|---|---|
| `Program` | point d'entrée, mutex d'instance unique, modes de test (`--preview`, `--paneltest`…), journal, *watchdog* qui enregistre la pile si l'interface se fige > 3 s |
| `Settings` | lecture/écriture du registre `HKCU\Software\ClaudeCompanion` |
| `Util` | verre acrylique Windows 11 (`DwmExtendFrameIntoClientArea` + `DWMWA_SYSTEMBACKDROP_TYPE`), coins arrondis, initialisation WebView2, relecture du `PATH` |
| `Companion` | **le chat** : fenêtre transparente, poses, comportement, interactions, icône de notification |
| `ChatPanel` | **la discussion** : WebView2 (`ui.html`), lancement de Claude Code, quota, projets, volets latéraux |
| `SettingsWindow` | **les réglages** : WebView2 (`settings.html`) |
| `Layered` | base des petites fenêtres transparentes pixel par pixel (flèches, calendrier, cartes, rappel) : zones cliquables, déplacement à la souris |
| `ArrowTab`, `CalWidget`, `UpWidget`, `ReminderCard` | les volets et la carte de rappel |
| `Calendars`, `CalEvent` | lecture iCal/webcal, règles de répétition, classement examen / rendez-vous |
| `Mailbox`, `Imap`, `Mime`, `MailMsg` | lecture des mails (Gmail IMAP, Outlook COM) et extraction des dates |
| `Reminders`, `Manual` | rappels, événements ajoutés ou masqués à la main |
| `Speller`, `SpellAssist`, `SpellMark` | correcteur d'orthographe global |
| `Setup` | assistant d'installation : `Check()` (état de chaque étape) et `Do()` (action) |

---

## Le chat (`Companion`)

### Rendu
- Une fenêtre **layered** (`UpdateLayeredWindow`, alpha par pixel) : seul le chat est visible, le reste est transparent et laisse passer les clics.
- Le chat est **dessiné par le code**, sans image : une `Pose` décrit le corps (`bx, by, bw, bh, ba`), la tête (`hx, hy, ht`), les yeux, les pattes avant/arrière et la queue (`ta, tc, tf`). Les poses (`SIT`, `STAND`, `GROOM`, `STRETCH`, `LIE`, `CURL`, `SPRAWL`, `LOAF`, `SCRATCH`, `LICK`, `HANG`…) sont **interpolées** entre elles.
- Rendu à petite résolution puis agrandi au facteur `Px` (pixel art net), avec un contour (`PixelPass`). Les yeux sont un sprite 5×6 posé sur des positions entières pour éviter les bavures.
- La queue est une **chaîne physique** avec inertie.

### Comportement
Une boucle à ~60 images/s choisit des **actions** (`act`) : `sit`, `walk`, `run`, `chase`, `groom`, `lick`, `scratch`, `rub`, `stretch`, `lie`. Un **état** (`state`) s'y superpose :

| État | Quand | Effet |
|---|---|---|
| `idle` | normal | comportement libre |
| `thinking` | Claude réfléchit | reste assis, queue rapide, petites bulles « … » |
| `happy` | réponse reçue, discussion ouverte | saut + cœur, yeux plissés pendant 1,6 s |
| `ready` | réponse reçue **pendant que la discussion était fermée** | yeux plissés, bulle « ! », saut + cœur toutes les 3,5 s, **jusqu'à l'ouverture de la discussion** |

Interactions : survol = caresse (ronron), clic = discussion, glisser = attrapé par la peau du cou (pendule autour du point 32,19, étirement), relâcher = chute.

---

## La discussion (`ChatPanel` + `ui.html`)

### Communication C# ⇄ page
Des **messages texte** via `chrome.webview.postMessage` : un mot-clé, un saut de ligne, puis les données.

| Page → C# | Effet |
|---|---|
| `ready` | la page est chargée : C# renvoie épinglage, dossier, nom, calendriers, quota |
| `send\n<auto>\n<sid>\n<outils>\n<texte>` | lancer Claude |
| `stop` | arrêter Claude |
| `mode\n<flash>\n<projet>` | mode flash / projet courant |
| `projects`, `newproject\n<nom>`, `folder` | projets ClaudeHub, dossier de travail |
| `sid\n<id>`, `forget\n<id>` | session courante, effacement d'une conversation flash |
| `pin\n0/1`, `hide`, `drag`, `settings`, `terminal` | fenêtre et navigation |
| `quotaprobe` | redemander le quota (fenêtre de 5 h écoulée) |
| `copy\n<texte>` | copier dans le presse-papiers Windows (la page est en `file://`, où l'API du navigateur n'est pas fiable) |
| `files` | glisser-déposer (chemins des fichiers) |

| C# → page | Effet |
|---|---|
| `line\n<json>` | une ligne du flux `stream-json` de Claude Code |
| `done\n<code>\n<erreurs>` | fin de la réponse |
| `quota\n<json>` | infos de limite (`unifiedWindows.five_hour` / `seven_day` : `utilization`, `resetsAt`) |
| `cal\n<json>`, `projects\n<json>`, `name`, `dir`, `pinned`, `paths`, `focus`, `new` | données et commandes d'interface |

### Lancement de Claude Code
Chaque message lance :

```
claude -p --output-format stream-json --verbose
       --permission-mode acceptEdits | bypassPermissions      (mode autonome)
       --resume <session>                                      (suite de la conversation)
       --allowedTools <outils autorisés>
       --append-system-prompt <persona de Bébène>
       --settings <garde-settings.json>                        (verrou de sécurité)
```

Le texte est envoyé sur l'entrée standard, précédé de `[Fenêtre active : …]`. Chaque ligne JSON du flux est transmise à la page, qui affiche le texte (Markdown → `marked` + `DOMPurify`), les outils utilisés, les différences de fichiers et les demandes d'autorisation (`permission_denials`).

### Conversations
Stockées dans le `localStorage` de la WebView (au plus 40, les anciennes raccourcies). Un **projet** fait travailler Claude dans `ClaudeHub/projets/<nom>/`, ce qui déclenche l'archivage automatique de ClaudeHub. Le mode **Flash** n'enregistre rien et supprime la transcription `~/.claude/projects/**/<session>.jsonl`.

### Quota
- Pendant une réponse : lu dans l'événement `rate_limit_event` du flux.
- Sinon : **sonde** silencieuse (`claude -p --model haiku --setting-sources "" --tools "" --no-session-persistence …`) à l'ouverture (au plus toutes les 5 min) et toutes les 15 min quand la fenêtre est visible.
- La page recalcule le compte à rebours toutes les 30 s ; si `resetsAt` est passé, elle affiche 100 % et envoie `quotaprobe`.

### Fenêtre
- Verre acrylique Windows 11 + fond HTML à 90 % d'opacité, coins arrondis natifs (8 px), contour DWM orange discret.
- Clic ailleurs = fermeture (sauf épinglée ou si la fenêtre active appartient à Bébène).
- **Déplacement** : `mousedown` sur l'en-tête → message `drag` → `ReleaseCapture` + `WM_NCLBUTTONDOWN(HTCAPTION)`. Un drapeau `Moved` empêche le recalage sur le chat ; il est remis à zéro à la fermeture sauf si la fenêtre est épinglée. Même principe pour les volets (`Layered.Moved`).

---

## Le verrou de sécurité

`ChatPanel.GuardSettings()` écrit `garde.ps1` et `garde-settings.json`, passés à chaque appel de Claude par `--settings`. C'est un hook **PreToolUse** sur `Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell` : si la commande ou le chemin visé contient `claude-companion` ou `claudecompanion`, il sort avec le code 2 → Claude Code **refuse l'action**, même en `bypassPermissions`. La persona indique aussi à Bébène qu'il ne peut pas modifier son programme.

---

## Calendriers, mails, rappels

- **iCal** : analyse maison (`VEVENT`, `DTSTART/DTEND` avec ou sans fuseau, journées entières, `RRULE` hebdo/quotidien/mensuel avec `COUNT`/`UNTIL`/`BYDAY`). Classement *examen* / *rendez-vous* par mots-clés, filtre de lieu optionnel. Actualisation toutes les 30 min.
- **Gmail** : IMAP SSL (`imap.gmail.com`) avec un mot de passe d'application chiffré DPAPI ; recherche sur 6 mois ; décodage MIME (quoted-printable, base64, encodages de titres).
- **Outlook** : COM de l'Outlook *classique* (le nouvel Outlook n'a pas d'interface COM).
- Extraction locale par expressions régulières (date, heure, lieu). Analyse toutes les 3 h, résultats mis en cache.
- **Rappels** : `Reminders.Due()` vérifie la veille à 18 h et 1 h avant ; les rappels déjà montrés sont notés dans `rappels.txt`.

---

## Le correcteur d'orthographe

1. Hooks bas niveau clavier et souris : on sait quand tu tapes et quand tu fais une pause.
2. **UI Automation** (`TextPattern`) lit le texte du champ actif et donne la position de chaque mot à l'écran.
3. **ISpellChecker** de Windows (fr-CH) trouve les fautes.
4. Une petite fenêtre `SpellMark` (croix rouge) est posée sur chaque faute ; un clic affiche les suggestions.
5. La correction est tapée **touche par touche** (`SendInput`, 6 à 12 ms entre les touches) : le nouveau Bloc-notes perd les envois groupés.

Ignoré : champs de mot de passe, terminaux, éditeurs de code, gestionnaires de mots de passe.

---

## Compilation (`build.ps1`)

1. Télécharge une fois le paquet NuGet **WebView2 1.0.2903.40** et copie ses DLL.
2. Génère `bebene.ico` à partir de `assets/icone-bebene.png` (16 à 256 px ; agrandissement « plus proche voisin » pour les multiples de 32 ; images < 256 en BMP 32 bits, 256 en PNG).
3. Compile avec `csc.exe` (références : WinForms, Drawing, Web.Extensions, Core, CSharp, Security, IO.Compression, UIAutomation, WebView2).
4. Copie `ui.html`, `settings.html`, `fonctions.json` et relance l'app.
5. `-Install` : raccourci « Bébène » dans le menu Démarrer + démarrage avec Windows (`HKCU\…\Run`).

---

## Réglages (registre `HKCU\Software\ClaudeCompanion`)

| Clé | Contenu |
|---|---|
| `Name`, `Scale`, `ShowCat`, `TopMost` | compagnon |
| `Model`, `Prompt`, `Caveman` | Claude |
| `WorkDir`, `Pinned`, `AutoAllow` | discussion |
| `DashCal`, `DashUp` | volets ouverts |
| `Calendars` (JSON), `LocFilter` | calendriers |
| `GmailUser`, `GmailPass` (DPAPI), `OutlookMail` | mails |
| `RemindExam`, `RemindRdv`, `RemindWhen` | rappels |
| `ManualEvents`, `HiddenEvents` | événements ajoutés / masqués |
| `SpellCheck` | correcteur |
