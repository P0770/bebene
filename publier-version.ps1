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
& $gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "La version $version existe déjà sur GitHub : seul le code a été mis à jour." -ForegroundColor DarkYellow
    Write-Host "Pour une nouvelle version téléchargeable, augmente le numéro dans Companion.cs (Version + AssemblyVersion)." -ForegroundColor DarkYellow
    exit 0
}
& "$src\creer-paquet.ps1" -NoOpen
$zip = Get-ChildItem "$src\paquets\Bebene-$version-*.zip" | Sort-Object LastWriteTime | Select-Object -Last 1

$f = [IO.File]::ReadAllText("$src\fonctions.json", [Text.Encoding]::UTF8) | ConvertFrom-Json
$new = @($f | Where-Object { $_.depuis -eq $version } | ForEach-Object { "- **$($_.titre)** : $($_.desc)" })
$notes = "$Message`n`n"
if ($new.Count) { $notes += "### Nouveautés`n" + ($new -join "`n") + "`n`n" }
$notes += "### Installer`nTélécharge **$($zip.Name)** ci-dessous, dézippe-le, lance ``ClaudeCompanion.exe`` et suis l'assistant d'installation.`nMise à jour : ferme Bébène (clic droit sur l'icône → Quitter), dézippe par-dessus, relance."
$nf = Join-Path $env:TEMP "bebene-notes.md"
[IO.File]::WriteAllText($nf, $notes, (New-Object Text.UTF8Encoding $false))
& $gh release create $tag $zip.FullName --title "Bébène $version" --notes-file $nf
Remove-Item $nf
Write-Host "`nPublié : Bébène $version" -ForegroundColor Green
