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

Le programme est réparti en 8 fichiers C# compilés ensemble (`build.ps1` prend tous les `*.cs`) : **`Companion.cs`** (démarrage, réglages, chat animé, `Version`), `Discussion.cs` (fenêtre de discussion et tâches Claude), `PostIts.cs`, `Agenda.cs` (calendriers, mails, rappels), `Correcteur.cs`, `Modeles.cs`, `Installation.cs` (assistant, mises à jour), `Reglages.cs`. Il est compilé par le `csc.exe` du .NET Framework 4 présent dans Windows : **C# 5 uniquement** (pas de `?.`, pas de `$"…"`, pas de `nameof`, pas de fonctions locales).

**Tâches en parallèle** : chaque conversation a sa propre session `claude -p --input-format stream-json` gardée ouverte (classe `Runner` dans `Discussion.cs`) ; jusqu'à 3 travaillent en même temps. Les messages entre la page et C# portent la clé de la conversation (`send\n<clé>…`, `rline\n<clé>\n<json>`, `rdone\n<clé>\n<code>`) ; la page garde une « vue » par tâche en fond (`VIEWS`, `withRun`). Une session est relancée avec `--resume` si ses réglages changent, et se ferme après 10 min sans message.

**Tests** : `tests\tests.ps1` (ajouter `-Claude` pour le test réel des tâches).

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
| `Updater` | mises à jour : dernière Release GitHub, comparaison de version, téléchargement et remplacement du programme |

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
| `projects`, `newproject\n<nom>`, `folder` | projets ClaudeHub (réponse `projects\n{all, arch}`), dossier de travail |
| `sid\n<id>`, `forget\n<id>` | session courante, effacement d'une conversation flash |
| `pin\n0/1`, `hide`, `drag`, `settings`, `terminal` | fenêtre et navigation |
| `quotaprobe` | redemander le quota (fenêtre de 5 h écoulée) |
| `title\n<id>\n<extrait>` | faire résumer la conversation en titre (Haiku) |
| `compact\n<session>` | compacter la conversation (`/compact`, Haiku) |
| `updcheck`, `updinstall`, `updopen`, `updseen` | mises à jour : vérifier, installer, ouvrir la Release, masquer la pastille verte |
| `copy\n<texte>` | copier dans le presse-papiers Windows (la page est en `file://`, où l'API du navigateur n'est pas fiable) |
| `files` | glisser-déposer (chemins des fichiers) |
| `savetxt\n<nom>\n<texte>`, `reveal\n<chemin>` | enregistrer une réponse en `.txt` dans Téléchargements (jamais d'écrasement : « (2) »…), l'afficher dans l'Explorateur |

| C# → page | Effet |
|---|---|
| `line\n<json>` | une ligne du flux `stream-json` de Claude Code |
| `done\n<code>\n<erreurs>` | fin de la réponse |
| `quota\n<json>` | infos de limite (`unifiedWindows.five_hour` / `seven_day` : `utilization`, `resetsAt`) |
| `title\n<id>\n<titre>` | titre résumé d'une conversation |
| `compacted\n<ok>\n<avant>\n<après>` | fin du compactage (tokens avant / après) |
| `saved\n<chemin>`, `saveerr\n<message>` | réponse enregistrée en `.txt` (ou échec) |
| `upd\n<json>`, `updstatus\n<état>\n<message>` | état des mises à jour (pastille, fenêtre) |
| `cal\n<json>`, `projects\n<json>`, `name`, `dir`, `pinned`, `paths`, `focus`, `new` | données et commandes d'interface |

### Lancement de Claude Code
Chaque message lance :

```
claude -p --output-format stream-json --verbose
       --include-partial-messages                              (écriture en direct, réglage Stream)
       --permission-mode acceptEdits | bypassPermissions      (mode autonome)
       --resume <session>                                      (suite de la conversation)
       --allowedTools <outils autorisés>
       --append-system-prompt <persona de Bébène>
       --settings <garde-settings.json>                        (verrou de sécurité)
```

Le texte est envoyé sur l'entrée standard, précédé de `[Fenêtre active : …]`. Chaque ligne JSON du flux est transmise à la page, qui affiche le texte (Markdown → `marked` + `DOMPurify`), les outils utilisés, les différences de fichiers et les demandes d'autorisation (`permission_denials`).

**Recherches en cours** : un outil de recherche (`Read`, `Glob`, `Grep`, `WebSearch`, `WebFetch`, lecture Chrome, MCP `search/list/get/read…`, commande Bash/PowerShell qui ne fait que lister ou lire : `ls`, `dir`, `grep`, `Get-ChildItem`…, capture `ecran.ps1`) n'ajoute pas d'étiquette : une seule ligne temporaire `.srch` montre où Bébène cherche (📄 fichier, 📁 dossier, 🔎 contenu, 🌐 web, ✉️/📅/🪟 appli). Elle disparaît dès que Bébène écrit ou agit, et à la fin du tour. Une modification de fichier = une seule ligne dépliable (`details.diff`). Le texte de la réponse (et son code) n'est jamais touché.

**Écriture en direct** : les événements `stream_event` / `text_delta` remplissent un aperçu (`.md.live`) révélé mot par mot à vitesse adaptative (`requestAnimationFrame`, rattrape le retard en ~7 images, Markdown re-rendu au plus toutes les 40 ms ; les blocs `bebene-*` ne s'affichent jamais, même à moitié). Le message `assistant` complet remplace ensuite l'aperçu.

**Pause de quota** : à chaque `rate_limit_event` (et à chaque sonde), si la session 5 h ou la semaine passe sous **3 %** restant, la tâche est arrêtée (`stop`) et une carte `.pause` résume la demande, les étapes `TodoWrite` faites / restantes, les fichiers modifiés (`Write`/`Edit`) et le dernier message (`data-p`, gardé dans l'historique). Rien ne repart seul : « ▶ Continuer » ou un message tapé lance une **nouvelle session** (`sid` vide) qui reçoit ce résumé. Si le quota est encore bas, la carte le dit et propose « Reprendre quand même ».

**Clic droit sur une réponse** : menu `#ctxm` (copier la sélection, copier la réponse, télécharger en `.txt`).

### Raccourcis clavier
Un seul raccourci : `RegisterHotKey` id 1 depuis le réglage `Hotkey1` (défaut `Alt+Space` ; texte `Ctrl+Alt+Shift+Win+Touche`, nom de touche .NET ; vide = désactivé). `AskSelection` : depuis Bébène, il referme la discussion ; dans un terminal (`ConsoleWindowClass`, `CASCADIA_HOSTING_WINDOW_CLASS`…), il ouvre seulement ; ailleurs, il attend le relâchement des modificateurs, simule Ctrl+C, lit le texte ou la liste de fichiers (`FileDropList`, avec `d` = dossier), restaure le presse-papiers et envoie `selection\n{text, files:[{p, d}]}`. Pendant la saisie d'une combinaison dans les réglages, le raccourci est libéré (`hotkeyPause`) puis réenregistré ; `HotkeyOk` signale une combinaison déjà prise.

**Brouillon** (page) : `DRAFT = {items:[{t:'text', v} | {t:'path', p, d}], group}` dans `localStorage.draft`, texte tapé dans `draftText`. Chaque sélection s'ajoute (sans doublon) ; si la conversation affichée a déjà des messages, une nouvelle est ouverte. `submit` ajoute `draftContext()` au message ; `save()` crée la conversation dans `DRAFT.group`.

**Notifications Windows** : `Companion.Notify(type, …)` avec `type` ∈ `NotifKinds` (`done`, `ask`, `perm`, `hand`, `pause`, `rappel`) ; chaque type se coupe dans les réglages (`Notif_<type>`).

### Conversations
Stockées dans le `localStorage` de la WebView (au plus 40, les anciennes raccourcies). Un **projet** fait travailler Claude dans `ClaudeHub/projets/<nom>/`, ce qui déclenche l'archivage automatique de ClaudeHub. **Sous-groupes** : `conv.group` + liste `localStorage.groups` par projet (`""` = sans projet), repliés dans `gfold`. Créer (＋ Groupe), renommer (✎), supprimer (× en 2 clics : les conversations restent, sans groupe), déplacer (⇢ ou clic droit → liste Sous-groupe / Projet). Le mode **Flash** n'enregistre rien et supprime la transcription `~/.claude/projects/**/<session>.jsonl`.

**Palette** (`#ddP` / `#pal`, Ctrl+K) : un seul panneau pour les projets et l'historique. Recherche `#mSearch` (noms de projets + contenu des conversations), tuiles `.ptile` (couleur `hueOf(nom)`), conversations `#mConvs`. `palScope` = projet affiché sans y être passé (survol d'une tuile pendant un glisser) ; `menuScope()` le suit. **Glisser-déposer** en pointer events (pas HTML5 DnD) : `dropTarget()` (tuile, `.mgrp[data-g]`, `[data-dz=loose]`, `[data-dz=new]`, autre conversation), `placeIn()` + toast Annuler. **Projets archivés** : `projets/.archives` (un nom par ligne, suit OneDrive), `ChatPanel.SetArchived` ; cachés des tuiles, désarchivés par `newproject` / `ensureproject` ; gérés dans Réglages › Mes projets (`projlist`, `projarch\n<nom>\n1|0`).

### Quota
- Pendant une réponse : lu dans l'événement `rate_limit_event` du flux.
- Sinon : **sonde** silencieuse (`claude -p --model haiku --setting-sources "" --tools "" --no-session-persistence …`) à l'ouverture (au plus toutes les 5 min) et toutes les 15 min quand la fenêtre est visible.
- La page recalcule le compte à rebours toutes les 30 s ; si `resetsAt` est passé, elle affiche 100 % et envoie `quotaprobe`.

### Fenêtre
- Verre acrylique Windows 11 + fond HTML à 90 % d'opacité, coins arrondis natifs (8 px), contour DWM orange discret.
- Clic ailleurs = fermeture (sauf épinglée). Deux détections : un sondage des boutons de la souris (`GetAsyncKeyState`, toutes les 25 ms tant que la fenêtre est visible) qui ferme sur un clic hors de la partie visible (`DWMWA_EXTENDED_FRAME_BOUNDS`) d'une fenêtre « ailleurs » (`IsElsewhere` : autre processus, ou les réglages sauf ⚙ cliqué il y a moins de 3 s ; le chat, les post-it, les volets et les fenêtres propres à la discussion via `OwnedByMe` ne comptent pas) ; et la perte d'activation (Alt+Tab), avec nouvel essai si le premier plan est encore vide.
- **Menus de la page** : une seule fenêtre flottante ouverte à la fois (`closePops`) ; clic dehors ou Échap la ferme (Échap ne cache la discussion que si rien n'est ouvert). Un menu n'est jamais redessiné pendant un clic (`ptrDown`) ni pendant une saisie dedans (`editingIn`), et seulement si son contenu change (`setHTML`) : sinon l'élément cliqué est remplacé et le clic est perdu.
- **Rond en haut à gauche** : avatar ; au survol, ⚙ (clic = réglages). La pastille de mise à jour est un bouton séparé sur son bord.
- **Déplacement** : `mousedown` sur l'en-tête → message `drag` → `ReleaseCapture` + `WM_NCLBUTTONDOWN(HTCAPTION)`. Un drapeau `Moved` empêche le recalage sur le chat ; il est remis à zéro à la fermeture sauf si la fenêtre est épinglée. Même principe pour les volets (`Layered.Moved`).

### Titres et compactage
- **Titres** : après les échanges 1, 2, 3 puis tous les 3, un extrait de la conversation (≤ 4 000 caractères) et les titres des autres conversations sont envoyés à `claude -p --model haiku` sans outils ni session, qui renvoie un titre de 2 à 5 mots (≤ 40 caractères, sujet concret, différent des autres). Un titre modifié à la main (✎ dans l'historique, double-clic sur le titre sous le nom) est verrouillé (`titleLock`).
- **Compactage** : la page suit la taille du contexte (champ `usage` des messages de l'assistant : `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`). Au premier échange elle mémorise la taille de départ (instructions, outils, mémoire) ; quand le contexte a grossi de **40 000 tokens**, elle demande `/compact` sur la session (`claude -p --model haiku --resume <session>`). La session garde le même identifiant et la suite de la conversation repart du résumé. L'envoi est bloqué le temps du compactage.

---

## Les mises à jour (`Updater`)

1. **Vérification** 15 s après le démarrage, toutes les 6 h et à l'ouverture de la discussion (au plus une fois par heure) : `GET https://api.github.com/repos/P0770/bebene/releases/latest`. Si le dépôt est privé (404), repli sur `gh api …` (GitHub CLI connecté). Si rien ne répond (hors ligne, pas de `gh`), aucune pastille : pas d'erreur visible.
2. **Comparaison** numérique des versions (`1.35` > `1.34.2`), entre `tag_name` de la Release et `Program.Version`. Une mise à jour n'est proposée que si la Release est **plus récente** ; une copie modifiée avec un numéro plus grand n'est jamais « rétrogradée ».
3. **Installation** (seulement si l'utilisateur clique) : téléchargement du zip de la Release dans `%TEMP%\bebene-maj-<version>`, décompression, puis un script PowerShell caché attend la fermeture de Bébène, copie **uniquement** `*.exe, *.dll, *.html, *.json, *.ico` dans le dossier de l'app (10 essais si un fichier est encore verrouillé) et relance Bébène.
4. **Ce qui est conservé** : réglages (registre), conversations et stockage de la page (profil WebView2 dans `%LOCALAPPDATA%\ClaudeCompanion`), calendriers, mails, rappels, journal, mémoire ClaudeHub. Le modèle `hub-modele` du zip n'est pas copié.
5. **Pastille verte** : au démarrage, `Updater.Init()` compare la version avec `LastVersion` (registre) ; si elle a augmenté, `UpdatedShown` est posé jusqu'à ce que l'utilisateur clique sur « OK ».

Test sans rien installer : `ClaudeCompanion.exe --updtest out.txt` (dernière Release, téléchargement et décompression).

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
