# Archive une session Claude Code dans le hub (Markdown lisible) et l'ajoute au journal du jour.
# Appelé par le hook SessionEnd (JSON sur l'entrée standard), ou à la main : -Transcript <fichier.jsonl>
param([string]$Transcript)
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.Encoding]::UTF8
$hub = Split-Path $PSScriptRoot -Parent
$sid = ''; $cwd = ''
if ($Transcript) { $tp = $Transcript } else {
    $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $tp = $in.transcript_path; $sid = $in.session_id; $cwd = $in.cwd
}
if (-not $tp -or -not (Test-Path $tp)) { exit 0 }
if (-not $sid) { $sid = [IO.Path]::GetFileNameWithoutExtension($tp) }

$body = New-Object Text.StringBuilder
$title = $null; $start = $null; $n = 0; $lastRole = ''
foreach ($line in [IO.File]::ReadLines($tp)) {
    if ($line -notmatch '"type":"(user|assistant)"' -or $line -match '"type":"tool_result"') { continue }
    try { $o = $line | ConvertFrom-Json } catch { continue }
    if (($o.type -ne 'user' -and $o.type -ne 'assistant') -or $o.isSidechain -or $o.isMeta) { continue }
    if (-not $cwd -and $o.cwd) { $cwd = $o.cwd }
    if (-not $start -and $o.timestamp) { $start = ([datetime]$o.timestamp).ToLocalTime() }
    $c = $o.message.content; $text = ''
    if ($c -is [string]) { $text = $c } else {
        foreach ($b in $c) {
            if ($b.type -eq 'text') { $text += $b.text + "`n" }
            elseif ($b.type -eq 'tool_use') { $text += "*[outil : " + $b.name + "]*`n" }
        }
    }
    $text = [regex]::Replace($text, '(?s)<system-reminder>.*?</system-reminder>', '')
    $text = [regex]::Replace($text, '(?s)<local-command-(stdout|stderr)>.*?</local-command-\1>', '')
    $text = [regex]::Replace($text, '<command-name>(.*?)</command-name>', '$1')
    $text = [regex]::Replace($text, '(?s)<command-(message|args)>.*?</command-\1>', '')
    $text = $text.Replace([string][char]0xFEFF, '').Trim()
    if (-not $text) { continue }
    $role = $o.type
    if ($role -eq 'user') { $n++; if (-not $title) { $title = ($text -split "`n")[0].Trim() } }
    if ($role -ne $lastRole) { [void]$body.Append("`n## " + $(if ($role -eq 'user') { 'Moi' } else { 'Claude' }) + "`n`n") }
    [void]$body.Append($text + "`n`n"); $lastRole = $role
}
if ($n -eq 0) { exit 0 }

# On n'archive que les conversations liées à un projet :
# ligne « 📁 Projet : <nom> » écrite par Claude, ou session lancée dans projets/<nom>
$proj = $null
$ms = [regex]::Matches($body.ToString(), '(?m)^\s*📁\s*Projet\s*:\s*([^\s`*]+)')
if ($ms.Count -gt 0) { $proj = $ms[$ms.Count - 1].Groups[1].Value }
elseif ($cwd -and $cwd.StartsWith("$hub\projets\", [StringComparison]::OrdinalIgnoreCase)) { $proj = $cwd.Substring("$hub\projets\".Length).Split('\')[0] }
if (-not $proj) { exit 0 }
$proj = ($proj.ToLower() -replace '[^\w.-]+', '-').Trim('-', '.')
if (-not $proj) { exit 0 }

if (-not $start) { $start = (Get-Item $tp).CreationTime }
$title = $title -replace '[\[\]`#*]', ''
if ($title.Length -gt 70) { $title = $title.Substring(0, 70) + '…' }
$y = $start.ToString('yyyy'); $mo = $start.ToString('MM'); $day = $start.ToString('yyyy-MM-dd')
$short = $sid.Substring(0, [Math]::Min(8, $sid.Length))
$utf8 = New-Object Text.UTF8Encoding $false

$pdir = Join-Path $hub "projets\$proj"
if (-not (Test-Path "$pdir\NOTES.md")) {
    New-Item -ItemType Directory -Force $pdir | Out-Null
    [IO.File]::WriteAllText("$pdir\NOTES.md", "# $proj`n`n## État`n- Créé le $day.`n", $utf8)
}
$dir = Join-Path $pdir 'conversations'
New-Item -ItemType Directory -Force $dir | Out-Null
$name = '{0}_{1}_{2}.md' -f $day, $start.ToString('HHmm'), $short
$head = "# $title`n`n- Projet : $proj`n- Date : $($start.ToString('yyyy-MM-dd HH:mm'))`n- Dossier : ``$cwd```n- Session : $sid`n- Messages : $n`n"
[IO.File]::WriteAllText((Join-Path $dir $name), $head + $body.ToString(), $utf8)

$jdir = Join-Path $hub "journal\$y"
New-Item -ItemType Directory -Force $jdir | Out-Null
$jf = Join-Path $jdir "$day.md"
$j = if (Test-Path $jf) { [IO.File]::ReadAllText($jf) } else { "# Journal du $day`n" }
if ($j -notmatch [regex]::Escape("_$short.md")) {
    if ($j -notmatch '(?m)^## Conversations') { $j = $j.TrimEnd() + "`n`n## Conversations`n" }
    $j = $j.TrimEnd() + "`n- $($start.ToString('HH:mm')) · **$proj** · [$title](../../projets/$proj/conversations/$name)`n"
    [IO.File]::WriteAllText($jf, $j, $utf8)
}
