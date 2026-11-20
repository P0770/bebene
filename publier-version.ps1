# Publie la version actuelle de Bébène sur GitHub, en une fois :
#   1. compile l'app (build.ps1) pour que le zip corresponde au code
#   2. enregistre les changements (commit) et les envoie (push)
#   3. si la version (Companion.cs) n'a jamais été publiée : crée une « Release » GitHub vX.Y avec le zip à télécharger
#      et, en notes, les nouveautés tirées de fonctions.json (entrées dont « depuis » = cette version)
# Lancé par « Publier sur GitHub.cmd » (double-clic). Option : -Message "texte du commit"
param([string]$Message)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
Set-Location $src

$gh = (Get-Command gh -ErrorAction SilentlyContinue)
$gh = if ($gh) { $gh.Source } else { Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw "GitHub CLI introuvable : installe-le avec  winget install GitHub.cli  puis  gh auth login" }

# Seul le propriétaire du dépôt officiel publie. Quelqu'un qui a copié (fork / clone) le projet ne peut pas
# toucher aux versions officielles : GitHub refuse de toute façon, ce contrôle donne juste un message clair.
$owner = 'P0770'
$ErrorActionPreference = 'Continue'
$me = (& $gh api user --jq .login 2>$null)
$origin = (git remote get-url origin 2>$null)
$ErrorActionPreference = 'Stop'
if ($me -ne $owner -or $origin -notmatch "github\.com[/:]$owner/bebene") {
    Write-Host "Publication réservée au propriétaire du dépôt officiel ($owner/bebene)." -ForegroundColor Yellow
    Write-Host "Pour proposer tes modifications : fork sur GitHub puis « pull request » (voir CONTRIBUTING.md)." -ForegroundColor Yellow
    exit 1
}

$version = (Select-String -Path "$src\Companion.cs" -Pattern 'public const string Version = "([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Bébène $version" -ForegroundColor Cyan

# 1. compiler
Write-Host "`n[1/3] Compilation…" -ForegroundColor Yellow
& "$src\build.ps1"

# 2. commit + push
Write-Host "`n[2/3] Envoi du code sur GitHub…" -ForegroundColor Yellow
if (-not $Message) {
    $Message = Read-Host "Qu'est-ce qui a changé ? (Entrée = « Bébène $version »)"
    if (-not $Message) { $Message = "Bébène $version" }
}
git add -A
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -q -m $Message; Write-Host "Changements enregistrés." } else { Write-Host "Aucun changement de code." }
git push -q
if ($LASTEXITCODE -ne 0) { throw "Envoi refusé par GitHub (connexion ? lance  gh auth login)" }

# 3. release
Write-Host "`n[3/3] Version téléchargeable…" -ForegroundColor Yellow
$tag = "v$version"
$ErrorActionPreference = 'Continue'                   # « release not found » arrive sur stderr : ce n'est pas une erreur ici
& $gh release view $tag 2>&1 | Out-Null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($exists) {
    Write-Host "La version $version existe déjà sur GitHub : seul le code a été mis à jour." -ForegroundColor DarkYellow
    Write-Host "Pour une nouvelle version téléchargeable, augmente le numéro dans Companion.cs (Version + AssemblyVersion)." -ForegroundColor DarkYellow
    exit 0
}
& "$src\creer-paquet.ps1" -NoOpen
$zip = Get-ChildItem "$src\paquets\Bebene-$version-*.zip" | Sort-Object LastWriteTime | Select-Object -Last 1

$f = [IO.File]::ReadAllText("$src\fonctions.json", [Text.Encoding]::UTF8) | ConvertFrom-Json
# nouveautés : tout ce qui est apparu depuis la dernière Release publiée (des versions intermédiaires peuvent ne pas avoir été publiées)
$ErrorActionPreference = 'Continue'
$prevTag = (& $gh release list --limit 1 --json tagName --jq '.[0].tagName' 2>$null)
$ErrorActionPreference = 'Stop'
$prev = [version]'0.0'; try { if ($prevTag) { $prev = [version]($prevTag -replace '^v', '') } } catch { }
$cur = [version]$version
$new = @($f | Where-Object { try { $d = [version]$_.depuis; $d -gt $prev -and $d -le $cur } catch { $false } } |
        Sort-Object { [version]$_.depuis } | ForEach-Object { "- **$($_.titre)** ($($_.depuis)) : $($_.desc)" })
$notes = "$Message`n`n"
if ($new.Count) { $notes += "### Nouveautés`n" + ($new -join "`n") + "`n`n" }
$notes += "### Installer`nTélécharge **$($zip.Name)** ci-dessous, dézippe-le, lance ``ClaudeCompanion.exe`` et suis l'assistant d'installation.`nMise à jour : ferme Bébène (clic droit sur l'icône → Quitter), dézippe par-dessus, relance."
$nf = Join-Path $env:TEMP "bebene-notes.md"
[IO.File]::WriteAllText($nf, $notes, (New-Object Text.UTF8Encoding $false))
& $gh release create $tag $zip.FullName --title "Bébène $version" --notes-file $nf
Remove-Item $nf
Write-Host "`nPublié : Bébène $version" -ForegroundColor Green
