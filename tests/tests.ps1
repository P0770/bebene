# Tests de Bébène : lance l'app installée en mode test et vérifie les pages HTML dans Edge (sans fenêtre).
# Usage : powershell -ExecutionPolicy Bypass -File tests\tests.ps1 [-Claude]
#   -Claude : ajoute le test réel des tâches en parallèle (2 conversations + 2e message dans la même session ; coûte quelques tokens Haiku)
# Résultat : une ligne OK / ÉCHEC par test ; les images sont dans %TEMP%\BebeneTests pour un coup d'œil.
param([switch]$Claude)
$ErrorActionPreference = 'Stop'
$src = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $env:USERPROFILE 'ClaudeCompanion\ClaudeCompanion.exe'
$out = Join-Path $env:TEMP 'BebeneTests'; New-Item -ItemType Directory -Force $out | Out-Null
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$fails = 0
function Result($name, $ok, $detail) {
    if ($ok) { Write-Host ("OK      " + $name) -ForegroundColor Green }
    else { Write-Host ("ÉCHEC   " + $name + "  " + $detail) -ForegroundColor Red; $script:fails++ }
}
function RunApp($argsList, $timeoutSec = 60) {
    $p = Start-Process -FilePath $exe -ArgumentList $argsList -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); return $false }
    return $true
}

# 1) images du chat : poses, réponse prête, question, besoin de toi, progression, pendule
$imgs = @(
    @('poses', @('--preview', "$out\poses.png")),
    @('réponse prête (!)', @('--readytest', "$out\ready.png")),
    @('question (?)', @('--asktest', "$out\ask.png")),
    @('besoin de la souris', @('--handtest', "$out\hand-souris.png", 'souris')),
    @('besoin de voir un écran', @('--handtest', "$out\hand-ecran.png", 'ecran')),
    @('progression 3/5', @('--progtest', "$out\progress.png")),
    @('chat porté (pendule)', @('--swingtest', "$out\swing.png"))
)
foreach ($t in $imgs) {
    $f = $t[1][1]; Remove-Item $f -ErrorAction SilentlyContinue
    $done = RunApp $t[1]
    Result ("image · " + $t[0]) ($done -and (Test-Path $f) -and (Get-Item $f).Length -gt 1000) "fichier absent ou vide"
}

# 2) gardien (Bébène ne peut pas se modifier lui-même) et commande de capture d'écran
Remove-Item "$out\guard.json" -ErrorAction SilentlyContinue
RunApp @('--guardtest', "$out\guard.json") | Out-Null
$gp = if (Test-Path "$out\guard.json") { (Get-Content "$out\guard.json" -Raw).Trim() } else { '' }   # chemin du fichier de réglages du gardien
$g = if ($gp -and (Test-Path $gp)) { Get-Content $gp -Raw } else { '' }
Result 'gardien prêt' ($g -match 'PreToolUse' -and $g -match 'garde') "settings du gardien inattendus"
RunApp @('--screentest', "$out\screen.txt") | Out-Null
$s = if (Test-Path "$out\screen.txt") { Get-Content "$out\screen.txt" -Raw } else { '' }
Result 'commande de capture' ($s -match 'ecran\.ps1' -and $s -match 'BebeneEcran') "commande inattendue"

# 3) pages HTML : chargement sans erreur JS, menus, scénario multitâche (réponses de Claude simulées)
if (-not $edge) { Result 'pages HTML' $false 'Edge introuvable' }
else {
    $stub = '<script>window.__err=[];window.onerror=(m,u,l)=>{__err.push(m+" @"+l)};window.__posts=[];window.__h=null;' +
            'window.chrome={webview:{postMessage(m){__posts.push(m)},addEventListener(t,f){if(t==="message")__h=f}}};</script>'
    function PageTest($name, $page, $script, $expect) {
        $ErrorActionPreference = 'Continue'                                   # Edge écrit des avertissements sur stderr : ce ne sont pas des échecs
        $html = $stub + [IO.File]::ReadAllText((Join-Path $src $page), [Text.Encoding]::UTF8) + '<script>setTimeout(()=>{try{' + $script + '}catch(x){document.title="EX:"+x.message}},500)</script>'
        $f = Join-Path $out (($page -replace '\.html$', '') + '-' + ($name -replace '[^a-z]', '') + '.html')
        [IO.File]::WriteAllText($f, $html, (New-Object Text.UTF8Encoding $false))
        $dom = & $edge --headless --disable-gpu --virtual-time-budget=3000 --dump-dom ("file:///" + ($f -replace '\\', '/')) 2>$null | Out-String
        $title = if ($dom -match '<title>([^<]*)</title>') { [Net.WebUtility]::HtmlDecode($Matches[1]) } else { '(pas de titre)' }
        Result ("page · " + $name) ($title -eq $expect) $title
    }
    PageTest 'discussion se charge' 'ui.html' 'document.title="R:"+JSON.stringify(__err)' 'R:[]'
    PageTest 'réglages se chargent' 'settings.html' 'document.title="R:"+JSON.stringify(__err)' 'R:[]'
    PageTest 'menus restent ouverts' 'ui.html' (
        'NOTES={enabled:true,notes:[{id:"a",title:"T",color:"jaune",shown:true,pinned:false}]};renderNotes();$("#notes").click();$("[data-ndel]").click();' +
        'var a=$("#notepop").classList.contains("open");convs=[{id:"c1",title:"T",updated:Date.now(),project:""}];project="";$("#ddHist").click();$("[data-del]").click();' +
        'var b=$("#ddH").classList.contains("open");document.body.click();document.title="R:"+a+b+!$("#notepop").classList.contains("open")+JSON.stringify(__err)') 'R:truetruetrue[]'
    PageTest 'multitâche' 'ui.html' (
        'const send=m=>__h({data:m});const asst=t=>JSON.stringify({type:"assistant",message:{content:[{type:"text",text:t}]}});' +
        'input.value="Range mes fichiers";submit();const kA=__posts.filter(p=>p.startsWith("send\n")).pop().split("\n")[1];' +
        'send("rline\n"+kA+"\n"+JSON.stringify({type:"assistant",message:{content:[{type:"tool_use",name:"TodoWrite",input:{todos:[{content:"a",status:"completed"},{content:"b",status:"pending"}]}}]}}));' +
        'newChat({project:""});input.value="Bonjour";submit();const kB=rkey;' +
        'send("rline\n"+kA+"\n"+asst("Il me faut ta souris.\n```bebene-main\n{\"quoi\":\"Excel\",\"type\":\"souris\"}\n```"));send("rdone\n"+kA+"\n0\n");' +
        'var bg=VIEWS[kA]&&!VIEWS[kA].busy&&VIEWS[kA].conv.attn==="hand"&&rkey===kB&&busy;' +
        'send("rdone\n"+kB+"\n0\n");openConv(VIEWS[kA].conv);var re=rkey===kA&&!!$("#log .ask.hand")&&!!$("#log .plan");' +
        'document.title="R:"+bg+re+JSON.stringify(__err)') 'R:truetrue[]'
}

# 4) (option) Claude réel : 2 conversations en parallèle, puis un 2e message qui réutilise la même session
if ($Claude) {
    Remove-Item "$out\run.txt" -ErrorAction SilentlyContinue
    RunApp @('--runtest', "$out\run.txt") 180 | Out-Null
    $r = if (Test-Path "$out\run.txt") { Get-Content "$out\run.txt" -Encoding UTF8 } else { @() }
    $pids = $r | Where-Object { $_ -match 'result .* · A · pid (\d+)' } | ForEach-Object { [regex]::Match($_, 'pid (\d+)').Groups[1].Value }
    Result 'Claude · 2 tâches en parallèle' (($r | Where-Object { $_ -match 'rdone \| [AB] \| 0' }).Count -ge 3) ($r -join ' / ')
    Result 'Claude · session gardée ouverte' ($pids.Count -ge 2 -and ($pids | Select-Object -Unique).Count -eq 1) ("pids A : " + ($pids -join ', '))
}

Write-Host ""
if ($fails) { Write-Host "$fails test(s) en échec" -ForegroundColor Red; exit 1 } else { Write-Host "Tout est OK" -ForegroundColor Green }
