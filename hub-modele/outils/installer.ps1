# Configure ce PC pour utiliser ClaudeHub : mémoire chargée partout, accès au hub, archivage automatique, skills.
#   -App      : installe aussi le compagnon Minuit (Claude Companion)
#   -Archiver : archive aussi toutes les anciennes conversations présentes sur ce PC
param([switch]$App, [switch]$Archiver)
$ErrorActionPreference = 'Stop'
$hub = Split-Path $PSScriptRoot -Parent
$hubFwd = $hub -replace '\\', '/'
$cl = Join-Path $HOME '.claude'
New-Item -ItemType Directory -Force $cl, "$cl\skills" | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false

# 1. Noyau de mémoire importé dans toutes les sessions (~/.claude/CLAUDE.md)
$md = "$cl\CLAUDE.md"; $imp = "@$hubFwd/memoire/00-noyau.md"
$cur = if (Test-Path $md) { [IO.File]::ReadAllText($md) } else { '' }
if (-not $cur.Contains($imp)) {
    [IO.File]::WriteAllText($md, ($cur.TrimEnd() + "`n`n# Mémoire centrale (ClaudeHub)`n$imp`n").TrimStart(), $utf8)
}

# 2. settings.json : accès au hub + hook d'archivage
$sp = "$cl\settings.json"
$s = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 | ConvertFrom-Json } else { New-Object PSObject }
if (-not $s.permissions) { $s | Add-Member permissions (New-Object PSObject) }
$dirs = @(@($s.permissions.additionalDirectories) | Where-Object { $_ })
if ($dirs -notcontains $hub) { $dirs += $hub }
$s.permissions | Add-Member additionalDirectories $dirs -Force
if (-not $s.hooks) { $s | Add-Member hooks (New-Object PSObject) }
$cmd = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$hubFwd/outils/archiver-session.ps1`""
$s.hooks | Add-Member SessionEnd @(@{ hooks = @(@{ type = 'command'; command = $cmd; timeout = 30 }) }) -Force
# RTK : compresse la sortie des commandes Bash (git, npm, tests…) avant que Claude la lise
$s.hooks | Add-Member PreToolUse @(@{ matcher = 'Bash'; hooks = @(@{ type = 'command'; command = 'rtk hook claude' }) }) -Force
if (-not $s.env) { $s | Add-Member env (New-Object PSObject) }
$s.env | Add-Member DO_NOT_TRACK '1' -Force
[IO.File]::WriteAllText($sp, (ConvertTo-Json -InputObject $s -Depth 20), $utf8)

# Outils d'économie de tokens : RTK (+ ripgrep), Node.js (pour les hooks Caveman), plugin Caveman
foreach ($id in 'rtk-ai.rtk', 'BurntSushi.ripgrep.MSVC', 'OpenJS.NodeJS.LTS') {
    winget install --id $id -e --accept-source-agreements --accept-package-agreements --disable-interactivity | Out-Null
}
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
claude plugin marketplace add JuliusBrussee/caveman | Out-Null
claude plugin install caveman@caveman | Out-Null

# 3. Skills du hub reliées dans ~/.claude/skills
Get-ChildItem "$hub\skills" | Where-Object PSIsContainer | ForEach-Object {
    $t = "$cl\skills\$($_.Name)"
    if (-not (Test-Path $t)) { cmd /c mklink /J "$t" "$($_.FullName)" | Out-Null }
}

# 4. Archivage des anciennes conversations
if ($Archiver) {
    Get-ChildItem "$cl\projects" -Recurse -Filter *.jsonl | Where-Object { $_.Directory.Name -ne 'subagents' } |
        ForEach-Object { try { & "$PSScriptRoot\archiver-session.ps1" -Transcript $_.FullName } catch { } }
}

# 5. Le compagnon Minuit
if ($App) { & "$hub\projets\claude-companion\build.ps1" -Install }

"ClaudeHub configuré sur ce PC."
