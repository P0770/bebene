# Double-clic sur « Publier sur GitHub.cmd » : envoie le code sur GitHub. C'est tout.
# GitHub fait le reste (.github/workflows/bebene.yml) : il compile et teste ; si le numéro de version
# (Companion.cs) est nouveau, il publie aussi la version téléchargeable (Release). Tests en échec = rien publié.
# Option : -Message "texte du commit"
param([string]$Message)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$owner = 'P0770'

$gh = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($gh) { $gh.Source } else { Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw "GitHub CLI introuvable : installe-le avec  winget install GitHub.cli  puis  gh auth login" }

# Seul le propriétaire envoie sur le dépôt officiel (GitHub refuserait de toute façon : ceci donne un message clair).
$ErrorActionPreference = 'Continue'
$me = & $gh api user --jq .login 2>$null
$origin = git remote get-url origin 2>$null
$scopes = & $gh auth status 2>&1 | Out-String
$ErrorActionPreference = 'Stop'
if ($me -ne $owner -or $origin -notmatch "github\.com[/:]$owner/bebene") {
    Write-Host "Réservé au propriétaire du dépôt officiel ($owner/bebene). Pour proposer tes modifications : fork puis pull request (CONTRIBUTING.md)." -ForegroundColor Yellow
    exit 1
}
# GitHub exige le droit « workflow » pour envoyer les fichiers .github/workflows : demandé une seule fois
if ($scopes -notmatch "'workflow'") {
    Write-Host "Une autorisation GitHub manque (une seule fois) : appuie sur Entrée, colle le code dans le navigateur, valide." -ForegroundColor Yellow
    & $gh auth refresh -h github.com -s workflow
}

if (-not $Message) { $Message = Read-Host "Qu'est-ce qui a changé ? (Entrée = « Mise à jour »)" }
if (-not $Message) { $Message = 'Mise à jour' }
git add -A
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -q -m $Message }
git push -q
if ($LASTEXITCODE -ne 0) { throw "Envoi refusé par GitHub (connexion ? lance  gh auth login)" }

Write-Host "`nEnvoyé. GitHub teste (et publie si la version est nouvelle) : https://github.com/$owner/bebene/actions" -ForegroundColor Green
