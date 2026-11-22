# Crée un zip de Bébène tel qu'il est maintenant, prêt à partager.
# Lancé par « Créer le paquet Bébène.cmd » (double-clic). Résultat : paquets\Bebene-<version>-<date>.zip
#   Contenu : l'application compilée (dossier d'installation), un modèle de mémoire ClaudeHub SANS tes infos
#   personnelles (outils + skills à jour, noyau générique) et un mode d'emploi.
#   Options : -App = dossier de l'app compilée (défaut : installation locale) ; -NoOpen = ne pas ouvrir l'Explorateur.
#   Le modèle de mémoire vient de hub-modele\ (dans le dépôt, pour que GitHub puisse aussi créer le paquet) ;
#   sur le PC du propriétaire, il est d'abord resynchronisé depuis ClaudeHub\outils, skills et README.md.
param([switch]$NoOpen, [string]$App = (Join-Path $env:USERPROFILE 'ClaudeCompanion'))
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$app = $App
$hub = Split-Path (Split-Path $src -Parent) -Parent          # ...\ClaudeHub
$exe = Join-Path $app 'ClaudeCompanion.exe'
if (-not (Test-Path $exe)) { throw "Bébène n'est pas compilé dans $app : lance d'abord build.ps1" }

$version = (Select-String -Path "$src\Companion.cs" -Pattern 'public const string Version = "([^"]+)"').Matches[0].Groups[1].Value
$stage = Join-Path $env:TEMP ('bebene-paquet-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $stage | Out-Null
$u8 = New-Object Text.UTF8Encoding $false

# 1. l'application (uniquement ce qu'il faut pour la lancer)
Get-ChildItem $app -File | Where-Object { $_.Extension -in '.exe', '.dll', '.html', '.json', '.ico' -and $_.Name -ne 'companion.ico' } |
    Copy-Item -Destination $stage

# 2. modèle de mémoire ClaudeHub, sans rien de personnel
$tpl = Join-Path $src 'hub-modele'
if ((Test-Path "$hub\outils") -and (Test-Path "$hub\skills")) {          # PC du propriétaire : copie à jour dans le dépôt
    if (Test-Path $tpl) { Remove-Item $tpl -Recurse -Force }
    New-Item -ItemType Directory -Force $tpl | Out-Null
    foreach ($sub in 'outils', 'skills') { Copy-Item "$hub\$sub" "$tpl\$sub" -Recurse }
    if (Test-Path "$hub\README.md") { Copy-Item "$hub\README.md" "$tpl\README.md" }
}
if (-not (Test-Path "$tpl\skills")) { throw "Modèle de mémoire introuvable ($tpl)" }
$m = Join-Path $stage 'hub-modele'
foreach ($sub in 'outils', 'skills') { Copy-Item "$tpl\$sub" "$m\$sub" -Recurse }
New-Item -ItemType Directory -Force "$m\memoire", "$m\journal", "$m\projets" | Out-Null
[IO.File]::WriteAllText("$m\memoire\00-noyau.md", @"
# Noyau de mémoire (chargé dans chaque session : rester court)

## Préférences
- Répondre en français.
- Économiser les tokens : réponses courtes et directes, lire seulement les parties utiles.

## Hub central : ClaudeHub
- ``memoire/index.md`` : liste des souvenirs détaillés (ne lire que si pertinent).
- ``journal/AAAA/AAAA-MM-JJ.md`` : résumé du jour + conversations de projet.
- ``projets/<nom>/`` : ``NOTES.md`` + ``conversations/`` archivées automatiquement.

## Règles
- Seules les conversations liées à un projet sont archivées (ligne « 📁 Projet : <nom> » ou session lancée dans projets/<nom>).
- « Cherche dans ta mémoire » : skill ``memoire``. Agir dans un programme : skill ``piloter-applis``.
- Nouveau souvenir durable → fichier dans ``memoire/`` + ligne dans ``memoire/index.md``.
"@, $u8)
[IO.File]::WriteAllText("$m\memoire\index.md", "# Index des souvenirs`n`n- [Noyau](00-noyau.md) : préférences et règles, chargé automatiquement`n", $u8)
if (Test-Path "$tpl\README.md") { Copy-Item "$tpl\README.md" "$m\README.md" }

# 3. mode d'emploi
[IO.File]::WriteAllText("$stage\LISEZMOI.txt", @"
BÉBÈNE $version — installation sur un nouveau PC

1. Dézippe ce dossier où tu veux (Bureau, Téléchargements…).
2. Double-clique sur ClaudeCompanion.exe.
3. L'assistant d'installation s'ouvre : suis les étapes dans l'ordre, chaque bouton fait le travail pour toi
   (installer Claude Code, te connecter à ton compte Claude, préparer la mémoire, installer Bébène…).

Il faut : Windows 10 ou 11, une connexion internet et un compte Claude (Pro ou Max).
Si Windows affiche « Windows a protégé votre ordinateur », clique sur « Informations complémentaires » puis « Exécuter quand même ».
"@.Replace("`n", "`r`n"), $u8)

# 4. le zip
$outDir = Join-Path $src 'paquets'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir ("Bebene-$version-" + (Get-Date -Format 'yyyy-MM-dd_HHmm') + '.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }                # 2e paquet dans la même minute : remplace le précédent
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force
Write-Host "Paquet créé : $zip" -ForegroundColor Green
$zip                                                          # chemin renvoyé (utilisé par ci\publier-release.ps1)
if (-not $NoOpen) { Start-Process explorer.exe "/select,`"$zip`"" }
