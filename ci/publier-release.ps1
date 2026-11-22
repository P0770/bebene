# Déploiement continu (lancé par .github/workflows/release.yml, après compilation et tests) :
# vérifie que le tag correspond à la version du code, crée le zip et publie la Release GitHub
# avec les nouveautés tirées de fonctions.json (tout ce qui est apparu depuis la dernière Release publiée).
# Il faut : gh connecté (GH_TOKEN), le tag poussé, Bébène compilé dans %USERPROFILE%\ClaudeCompanion.
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$src = Split-Path $PSScriptRoot -Parent
Set-Location $src

$cs = Get-Content "$src\Companion.cs" -Raw -Encoding UTF8
$version = [regex]::Match($cs, 'public const string Version = "([^"]+)"').Groups[1].Value
$asm = [regex]::Match($cs, 'AssemblyVersion\("([^"]+)"\)').Groups[1].Value
if ("v$version" -ne $Tag) { throw "Tag $Tag, mais Version = $version dans Companion.cs" }
if (-not $asm.StartsWith("$version.")) { throw "AssemblyVersion $asm ne correspond pas à $version" }

$zip = & "$src\creer-paquet.ps1" -NoOpen | Select-Object -Last 1
if (-not $zip -or -not (Test-Path $zip)) { throw "Zip introuvable" }

# message du tag annoté (écrit par publier-version.ps1) = première ligne des notes
$msg = (git tag -l --format='%(contents)' $Tag | Out-String).Trim()
if (-not $msg) { $msg = "Bébène $version" }

$f = [IO.File]::ReadAllText("$src\fonctions.json", [Text.Encoding]::UTF8) | ConvertFrom-Json
$ErrorActionPreference = 'Continue'                   # aucune Release encore : message sur stderr, pas une erreur
$prevTag = (gh release list --limit 1 --exclude-drafts --json tagName --jq '.[0].tagName' 2>$null)
$ErrorActionPreference = 'Stop'
$prev = [version]'0.0'; try { if ($prevTag) { $prev = [version]($prevTag -replace '^v', '') } } catch { }
$cur = [version]$version
$new = @($f | Where-Object { try { $d = [version]$_.depuis; $d -gt $prev -and $d -le $cur } catch { $false } } |
        Sort-Object { [version]$_.depuis } | ForEach-Object { "- **$($_.titre)** ($($_.depuis)) : $($_.desc)" })
$notes = "$msg`n`n"
if ($new.Count) { $notes += "### Nouveautés`n" + ($new -join "`n") + "`n`n" }
$notes += "### Installer`nTélécharge **$(Split-Path $zip -Leaf)** ci-dessous, dézippe-le, lance ``ClaudeCompanion.exe`` et suis l'assistant d'installation.`nMise à jour : ferme Bébène (clic droit sur l'icône → Quitter), dézippe par-dessus, relance."
$nf = Join-Path $env:TEMP 'bebene-notes.md'
[IO.File]::WriteAllText($nf, $notes, (New-Object Text.UTF8Encoding $false))

gh release create $Tag $zip --title "Bébène $version" --notes-file $nf --verify-tag
if ($LASTEXITCODE -ne 0) { throw "Publication refusée par GitHub" }
Write-Host "Publié : Bébène $version" -ForegroundColor Green
