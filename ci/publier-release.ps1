# Lancé par GitHub (.github/workflows/bebene.yml) après compilation et tests réussis sur main.
# Si la version de Companion.cs n'a jamais été publiée : crée le zip et publie la Release vX.Y,
# avec le dernier message de commit et les nouveautés de fonctions.json. Sinon : ne fait rien.
$ErrorActionPreference = 'Stop'
$src = Split-Path $PSScriptRoot -Parent
Set-Location $src

$cs = Get-Content "$src\Companion.cs" -Raw -Encoding UTF8
$version = [regex]::Match($cs, 'public const string Version = "([^"]+)"').Groups[1].Value
$asm = [regex]::Match($cs, 'AssemblyVersion\("([^"]+)"\)').Groups[1].Value
if (-not $asm.StartsWith("$version.")) { throw "AssemblyVersion $asm ne correspond pas à Version $version (Companion.cs)" }
$tag = "v$version"

$ErrorActionPreference = 'Continue'                   # « release not found » arrive sur stderr : ce n'est pas une erreur
gh release view $tag 2>&1 | Out-Null
$exists = $LASTEXITCODE -eq 0
$prevTag = (gh release list --limit 1 --exclude-drafts --json tagName --jq '.[0].tagName' 2>$null)
$ErrorActionPreference = 'Stop'
if ($exists) { Write-Host "Version $version déjà publiée : rien à faire."; exit 0 }

$zip = & "$src\creer-paquet.ps1" -NoOpen | Select-Object -Last 1
if (-not $zip -or -not (Test-Path $zip)) { throw "Zip introuvable" }

# notes : message du commit + tout ce qui est apparu dans fonctions.json depuis la dernière Release
$msg = (git log -1 --format=%s | Out-String).Trim()
$f = [IO.File]::ReadAllText("$src\fonctions.json", [Text.Encoding]::UTF8) | ConvertFrom-Json
$prev = [version]'0.0'; try { if ($prevTag) { $prev = [version]($prevTag -replace '^v', '') } } catch { }
$cur = [version]$version
$new = @($f | Where-Object { try { $d = [version]$_.depuis; $d -gt $prev -and $d -le $cur } catch { $false } } |
        Sort-Object { [version]$_.depuis } | ForEach-Object { "- **$($_.titre)** ($($_.depuis)) : $($_.desc)" })
$notes = "$msg`n`n"
if ($new.Count) { $notes += "### Nouveautés`n" + ($new -join "`n") + "`n`n" }
$notes += "### Installer`nTélécharge **$(Split-Path $zip -Leaf)** ci-dessous, dézippe-le, lance ``ClaudeCompanion.exe`` et suis l'assistant d'installation.`nMise à jour : ferme Bébène (clic droit sur l'icône → Quitter), dézippe par-dessus, relance."
$nf = Join-Path $env:TEMP 'bebene-notes.md'
[IO.File]::WriteAllText($nf, $notes, (New-Object Text.UTF8Encoding $false))

gh release create $tag $zip --target $env:GITHUB_SHA --title "Bébène $version" --notes-file $nf
if ($LASTEXITCODE -ne 0) { throw "Publication refusée par GitHub" }
Write-Host "Publié : Bébène $version" -ForegroundColor Green
