# Bébène pour Mac — plan

But : une app `Bebene.app` téléchargeable dans chaque Release GitHub, à côté du zip Windows, avec les fonctions qu'on aime et un rendu propre « à la Mac ».

## Principe : un seul code pour l'interface

- `ui.html` (discussion) et `settings.html` (réglages) sont **les mêmes fichiers** que sur Windows.
- Sur Mac, ils tournent dans WKWebView. Le fichier `mac/pont.js` recrée `window.chrome.webview` (postMessage, addEventListener) par-dessus `webkit.messageHandlers`. Le protocole de messages texte (`send`, `rline`, `convsave`…) ne change pas.
- `pont.js` pose `document.documentElement.dataset.os = "mac"` et `window.BEBENE_OS = "mac"`. Les rares différences (raccourcis ⌘, textes « Finder » au lieu d'« Explorateur », sections masquées) sont gérées dans les HTML par `[data-os=mac]` ou par ce drapeau. Windows n'est pas touché.
- Partie native : Swift + AppKit, compilée par `swiftc` sur le runner `macos-latest` de GitHub Actions (aucun Xcode, aucun Mac nécessaire pour compiler).
- Le chat : dessin porté en JavaScript Canvas (`mac/chat.html`) dans une fenêtre transparente sans bord. Avantage : testable dans n'importe quel navigateur, y compris depuis Windows.

## Correspondances Windows → Mac

| Windows | Mac |
|---|---|
| WinForms + GDI+ (chat) | NSPanel transparent + WKWebView + Canvas |
| WebView2 | WKWebView (déjà dans macOS) |
| Registre HKCU | `UserDefaults` (`ch.bebene.app`) |
| DPAPI (mot de passe Gmail) | Trousseau (Keychain) |
| Clé Run (démarrage auto) | `SMAppService.mainApp` (macOS 13+) |
| Icône de notification | `NSStatusItem` (barre des menus, en haut à droite) |
| Toasts Windows | `UNUserNotificationCenter` |
| RegisterHotKey Alt+Espace | Carbon `RegisterEventHotKey` ⌥Espace (aucune autorisation requise) |
| Acrylique DWM | `NSVisualEffectView` (flou natif) |
| `taskkill /T` | `kill(-pgid)` (groupe de processus) |
| `garde.ps1` (gardien) | `garde.sh` (même logique, bash) |
| `ecran.ps1` (voir l'écran) | `screencapture -x` (autorisation « Enregistrement de l'écran ») |
| skill piloter-applis (COM / UIA) | skill `piloter-applis-mac` (AppleScript / JXA, `osascript`) |
| Explorateur (fichiers sélectionnés) | Finder via AppleScript (`selection`) |
| `explorer /select` | `NSWorkspace.activateFileViewerSelecting` |
| PowerShell dans le terminal | Terminal.app via `osascript` |

## Ce qui est retiré ou remplacé

| Fonction | Sur Mac |
|---|---|
| Correcteur d'orthographe global | **Retiré** en v1 (macOS a déjà son correcteur intégré dans presque toutes les applis). Section masquée. |
| Lecture des mails Outlook (COM) | **Retirée**. Gmail (IMAP) reste. Section Outlook masquée. |
| Calendrier Outlook | Remplacé par les liens ICS (déjà gérés) ; Calendrier Apple plus tard via EventKit. |
| Piloter Excel/Word par COM | Remplacé par AppleScript (Office pour Mac le supporte). Moins complet. |
| Fenêtre active (titre + capture) | Gardé : `NSWorkspace.frontmostApplication` + titre via Accessibilité (facultatif). |
| Remplir les pages web | Gardé tel quel (extension Claude in Chrome, indépendante du système). |
| Caveman, RTK | Gardés (outils de Claude Code, multiplateformes). |

Tout le reste de la discussion est identique, car c'est le même HTML : projets, palette, glisser-déposer, recherche, tâches en parallèle, autorisations, quota, questions, rappels, téléchargements, images collées, post-its de la discussion, thème clair/sombre.

## Ergonomie Mac

- Raccourci : **⌥Espace** (ouvre la discussion, joint les fichiers sélectionnés dans le Finder).
- Raccourcis affichés en ⌘ (⌘K, ⌘Entrée, ⌘V) au lieu de Ctrl.
- Pas d'icône dans le Dock (app « agent », `LSUIElement`) : le chat, l'icône de la barre des menus, et c'est tout.
- Discussion : fenêtre au flou natif, coins arrondis macOS, ombre système, ne vole pas le focus du chat.
- Le chat marche sur le haut du Dock et sur le bas des fenêtres, comme sur Windows (zone `visibleFrame`).
- Police système (San Francisco) automatiquement, via `-apple-system` déjà présent dans les CSS.

## Étapes

1. **Socle** (cette session) : dossier `mac/`, `pont.js`, app Swift (barre des menus, discussion WKWebView, réglages, stockage, lancement de `claude` en stream-json, gardien bash, conversations, projets), build GitHub Actions → `Bebene-mac-<version>.zip` dans la Release.
2. **Chat animé** : port du dessin et des comportements en Canvas (`chat.html`), fenêtre transparente, glisser, déposer des fichiers, états (réfléchit, question, prêt, joie).
3. **Quotidien** : ⌥Espace + fichiers du Finder, notifications, rappels, post-its, agenda ICS, Gmail (Trousseau), démarrage auto, mises à jour.
4. **Adaptation HTML** : textes et raccourcis Mac, sections masquées, `fonctions.json` filtré (`os`).
5. **Test sur ton Mac** : premier lancement (clic droit → Ouvrir), autorisations, corrections.

## Limites à accepter

- Sans compte Apple Developer (99 $/an), l'app n'est pas signée : au premier lancement, clic droit → Ouvrir (ou `xattr -dr com.apple.quarantine Bebene.app`).
- Claude Code doit être installé sur le Mac (`claude` dans le PATH, ex. `~/.local/bin` ou Homebrew).
- macOS 13 Ventura ou plus récent.
- Chaque nouvelle fonction native devra être codée deux fois. Les fonctions d'interface (HTML) restent communes.
