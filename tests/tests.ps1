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
    @('bulle vers la discussion', @('--thinktest', "$out\think.png")),
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
# petits écrans : la discussion ne déborde jamais de la zone utile
Remove-Item "$out\fit.txt" -ErrorAction SilentlyContinue
RunApp @('--fittest', "$out\fit.txt") | Out-Null
$ft = if (Test-Path "$out\fit.txt") { Get-Content "$out\fit.txt" -Encoding UTF8 } else { @() }
Result 'discussion tient sur petit écran' ($ft.Count -eq 4 -and -not (($ft -join "`n") -match 'DEBORDE')) ($ft -join ' / ')
# conversations en fichiers, rappels demandés à Bébène, point du jour (dossiers temporaires, rien de réel touché)
Remove-Item "$out\store.txt" -ErrorAction SilentlyContinue
RunApp @('--storetest', "$out\store.txt") | Out-Null
$st = if (Test-Path "$out\store.txt") { Get-Content "$out\store.txt" -Encoding UTF8 } else { @() }
Result 'historique en fichiers' (($st -join "`n") -match 'convs \[\{"id":"c1","title":"Un bis"\}\]') ($st -join ' / ')
Result 'rappels demandés' (($st -join "`n") -match 'ajoutes 2 \| du Sortir le linge \| encore -') ($st -join ' / ')
Result 'point du jour (données)' (($st -join "`n") -match 'brief \{"date":"[^"]+","today":\[.*"rappels":\[\]\}') ($st -join ' / ')

# 3) pages HTML : chargement sans erreur JS, menus, scénario multitâche (réponses de Claude simulées)
if (-not $edge) { Result 'pages HTML' $false 'Edge introuvable' }
else {
    $stub = '<script>window.__err=[];window.onerror=(m,u,l)=>{__err.push(m+" @"+l)};window.__posts=[];window.__h=null;' +
            'window.chrome={webview:{postMessage(m){__posts.push(m)},addEventListener(t,f){if(t==="message")__h=f}}};</script>'
    # Chaque page tourne dans son propre Edge (profil à part) : tous lancés en même temps, résultats lus à la fin (PageResults)
    $script:pages = @()
    function PageTest($name, $page, $script, $expect, $pre = '') {
        $html = $stub + $pre + [IO.File]::ReadAllText((Join-Path $src $page), [Text.Encoding]::UTF8) + '<script>setTimeout(()=>{try{' + $script + '}catch(x){document.title="EX:"+x.message}},500)</script>'
        $id = ($page -replace '\.html$', '') + '-' + ($name -replace '[^a-z]', '')
        $f = Join-Path $out ($id + '.html'); $o = Join-Path $out ($id + '.dom')
        [IO.File]::WriteAllText($f, $html, (New-Object Text.UTF8Encoding $false))
        $udd = Join-Path $env:TEMP ('BebeneEdge-' + $id); if (Test-Path $udd) { Remove-Item $udd -Recurse -Force -ErrorAction SilentlyContinue }
        $p = Start-Process $edge -PassThru -WindowStyle Hidden -RedirectStandardOutput $o -RedirectStandardError ($o + '.err') -ArgumentList @(
            '--headless', '--disable-gpu', '--no-first-run', '--virtual-time-budget=3000', ('"--user-data-dir=' + $udd + '"'), '--dump-dom', ('"file:///' + ($f -replace '\\', '/') + '"'))
        $script:pages += [pscustomobject]@{ Name = $name; Expect = $expect; P = $p; Out = $o; Udd = $udd }
    }
    function PageResults {
        foreach ($t in $script:pages) {
            if (-not $t.P.WaitForExit(90000)) { try { $t.P.Kill() } catch { } }
            $dom = ''                                                         # lu en partage : les processus enfants d'Edge peuvent encore tenir le fichier
            if (Test-Path $t.Out) { $fs = [IO.File]::Open($t.Out, 'Open', 'Read', 'ReadWrite'); try { $dom = (New-Object IO.StreamReader $fs).ReadToEnd() } finally { $fs.Dispose() } }
            $title = if ($dom -match '<title>([^<]*)</title>') { [Net.WebUtility]::HtmlDecode($Matches[1]) } else { '(pas de titre)' }
            Result ("page · " + $t.Name) ($title -eq $t.Expect) $title
            Remove-Item $t.Udd -Recurse -Force -ErrorAction SilentlyContinue
        }
        $script:pages = @()
    }
    PageTest 'discussion se charge' 'ui.html' 'document.title="R:"+JSON.stringify(__err)' 'R:[]'
    PageTest 'réglages se chargent' 'settings.html' 'document.title="R:"+JSON.stringify(__err)' 'R:[]'
    PageTest 'menus restent ouverts' 'ui.html' (
        'NOTES={enabled:true,notes:[{id:"a",title:"T",color:"jaune",shown:true,pinned:false}]};renderNotes();$("#notes").click();$("[data-ndel]").click();' +
        'var a=$("#notepop").classList.contains("open");convs=[{id:"c1",title:"T",updated:Date.now(),project:""}];project="";$("#ddProj").click();$("[data-del]").click();' +
        'var b=$("#ddP").classList.contains("open");document.body.click();document.title="R:"+a+b+!$("#notepop").classList.contains("open")+JSON.stringify(__err)') 'R:truetruetrue[]'
    PageTest 'multitâche' 'ui.html' (
        'const send=m=>__h({data:m});const asst=t=>JSON.stringify({type:"assistant",message:{content:[{type:"text",text:t}]}});' +
        'input.value="Range mes fichiers";submit();const kA=__posts.filter(p=>p.startsWith("send\n")).pop().split("\n")[1];' +
        'send("rline\n"+kA+"\n"+JSON.stringify({type:"assistant",message:{content:[{type:"tool_use",name:"TodoWrite",input:{todos:[{content:"a",status:"completed"},{content:"b",status:"pending"}]}}]}}));' +
        'newChat({project:""});input.value="Bonjour";submit();const kB=rkey;' +
        'send("rline\n"+kA+"\n"+asst("Il me faut ta souris.\n```bebene-main\n{\"quoi\":\"Excel\",\"type\":\"souris\"}\n```"));send("rdone\n"+kA+"\n0\n");' +
        'var bg=VIEWS[kA]&&!VIEWS[kA].busy&&VIEWS[kA].conv.attn==="hand"&&rkey===kB&&busy;' +
        'send("rdone\n"+kB+"\n0\n");openConv(VIEWS[kA].conv);var re=rkey===kA&&!!$("#log .ask.hand")&&!!$("#log .plan");' +
        'document.title="R:"+bg+re+JSON.stringify(__err)') 'R:truetrue[]'
    # v1.50 : recherches temporaires, pause de quota, sous-groupes, clic droit .txt, titre manuel, ⚙ dans le rond
    $sim = 'const send=m=>__h({data:m});const tu=(n,i)=>JSON.stringify({type:"assistant",message:{content:[{type:"tool_use",name:n,input:i}]}});' +
           'const asst=t=>JSON.stringify({type:"assistant",message:{content:[{type:"text",text:t}]}});'
    PageTest 'recherches retirées à la fin' 'ui.html' ($sim +
        'input.value="Trouve mon CV";submit();const k=rkey;send("rline\n"+k+"\n"+tu("Grep",{pattern:"CV",path:"C:/Users/x/Documents"}));' +
        'send("rline\n"+k+"\n"+tu("Bash",{command:"ls \"C:/Users/x/Desktop\""}));var one=document.querySelectorAll("#log .srch").length===1&&/Desktop/.test($("#log .srch").textContent);' +
        'send("rline\n"+k+"\n"+tu("Edit",{file_path:"C:/a/cv.md",old_string:"a",new_string:"b"}));send("rline\n"+k+"\n"+asst("Voici :\n```js\nls()\n```"));send("rdone\n"+k+"\n0\n");' +
        'var gone=!$("#log .srch")&&!/Grep|Recherche/.test($("#log").textContent)&&!!$("#log .diff summary span")&&!!$("#log .md pre");' +
        'document.title="R:"+one+gone+JSON.stringify(__err)') 'R:truetrue[]'
    PageTest 'réponse seule une fois arrivée' 'ui.html' ($sim +
        'input.value="Range";submit();const k=rkey;send("rline\n"+k+"\n"+tu("Bash",{command:"npm run build"}));send("rline\n"+k+"\n"+tu("TodoWrite",{todos:[{content:"A",status:"completed"}]}));' +
        'send("rline\n"+k+"\n"+tu("Write",{file_path:"C:/a/x.md",content:"x"}));send("rline\n"+k+"\n"+tu("Edit",{file_path:"C:/a/x.md",old_string:"x",new_string:"y"}));' +
        'var during=!!$("#log .tool");send("rline\n"+k+"\n"+asst("Fini."));send("rdone\n"+k+"\n0\n");' +
        'const m=$("#log .msg.bot");var a=during&&!m.querySelector(":scope > .tool, :scope > .srch, :scope > .plan, :scope > .diff")&&!!m.querySelector(":scope > details.acts .plan")&&m.querySelectorAll("details.acts .diff").length===2;' +
        'var b=/1 fichier modifié/.test(m.querySelector("details.acts summary").textContent);' +
        'log.innerHTML="<div class=\"msg bot\"><div class=\"tool\"><i></i><span>Lire · x</span></div><div class=\"srch\">s</div><div class=\"md\">ok</div></div>";tidyAll();var c=!$("#log .tool")&&!$("#log .srch");' +
        'document.title="R:"+a+b+c+JSON.stringify(__err)') 'R:truetruetrue[]'
    PageTest 'zone de saisie à la taille du texte' 'ui.html' (
        'input.value="";autosize();var h1=input.offsetHeight,a=input.scrollHeight<=input.clientHeight+1&&getComputedStyle(input).overflowY==="hidden";' +
        'input.value="a\nb\nc";autosize();var h3=input.offsetHeight,b=h3>h1&&getComputedStyle(input).overflowY==="hidden";' +
        'input.value="x\n".repeat(80);autosize();var c=getComputedStyle(input).overflowY==="auto"&&input.offsetHeight<=Math.max(120,innerHeight*0.4)+1;' +
        'input.value="";autosize();var d=input.offsetHeight===h1;document.title="R:"+a+b+c+d+JSON.stringify(__err)') 'R:truetruetruetrue[]'
    PageTest 'pause avant la fin du quota' 'ui.html' ($sim +
        'input.value="Refais le rapport";submit();const k=rkey;send("rline\n"+k+"\n"+tu("TodoWrite",{todos:[{content:"Lire",status:"completed"},{content:"Écrire",status:"in_progress"}]}));' +
        'send("rline\n"+k+"\n"+tu("Write",{file_path:"C:/a/r.md",content:"x"}));' +
        'send("rline\n"+k+"\n"+JSON.stringify({type:"rate_limit_event",rate_limit_info:{unifiedWindows:{five_hour:{utilization:0.985,resetsAt:Date.now()/1000+3600}}}}));' +
        'var st=__posts.includes("stop\n"+k);send("rdone\n"+k+"\n1\n");var card=$("#log .pause:not(.done)");' +
        'var sum=!!card&&/Lire/.test(card.textContent)&&/r\.md/.test(card.textContent)&&!$("#log .err");' +
        'input.value="vas-y";submit();var blocked=!busy&&/encore bas/.test(card.textContent);' +
        'send("quota\n"+JSON.stringify({unifiedWindows:{five_hour:{utilization:0.2}}}));submit();const p=__posts.filter(x=>x.startsWith("send\n")).pop().split("\n");' +
        'var res=busy&&p[3]===""&&/Reprise/.test(p.slice(7).join("\n"))&&/Écrire/.test(p.slice(7).join("\n"))&&card.classList.contains("done");' +
        'document.title="R:"+st+sum+blocked+res+JSON.stringify(__err)') 'R:truetruetruetrue[]'
    PageTest 'sous-groupes' 'ui.html' (
        'convs=[{id:"c1",title:"Un",updated:Date.now(),project:"",hist:"<div class=\"msg user\">x</div>"},{id:"c2",title:"Deux",updated:Date.now(),project:""}];project="";conv=null;' +
        'const ent=i=>i.dispatchEvent(new KeyboardEvent("keydown",{key:"Enter",bubbles:true}));$("#ddProj").click();$("#mGroupAdd").click();' +
        'var i=$("#mConvs input.gren");i.value="Cours";ent(i);var a=JSON.stringify(GROUPS[""])==="[\"Cours\"]";' +
        'askMove($("[data-c=c1]"),"c1");var s=$("#mConvs select");s.value="g:Cours";s.onchange();var b=convs[0].group==="Cours"&&!!$(".mgrp[data-g=Cours]");' +
        '$(".mgrp[data-g=Cours] [data-grn]").click();i=$("#mConvs input.gren");i.value="Maths";ent(i);var c=convs.find(x=>x.id==="c1").group==="Maths";' +
        '$(".mgrp[data-g=Maths] [data-gdel]").click();$(".mgrp[data-g=Maths] [data-gdel]").click();var d=!$(".mgrp[data-g=Maths]")&&convs.length===2&&!convs[0].group&&!!convs.find(x=>x.id==="c1").hist;' +
        'var open=$("#ddP").classList.contains("open");document.title="R:"+a+b+c+d+open+JSON.stringify(__err)') 'R:truetruetruetruetrue[]'
    PageTest 'clic droit : télécharger en .txt' 'ui.html' (
        'conv={id:"c9",title:"Mon rapport",updated:Date.now(),project:""};var m=el("msg bot","<div class=\"md\">Bonjour</div>");$("#log .md").dataset.raw="Bonjour **toi**";' +
        '$("#log .md").dispatchEvent(new MouseEvent("contextmenu",{bubbles:true,cancelable:true,clientX:50,clientY:50}));var o=$("#ctxm").classList.contains("open");' +
        '$("#ctxm [data-a=txt]").click();var p=__posts.find(x=>x.startsWith("savetxt\n"))||"";' +
        'document.title="R:"+o+(/^savetxt\nMon rapport - \d{4}-\d\d-\d\d \d\dh\d\d\nBonjour \*\*toi\*\*$/.test(p))+!$("#ctxm").classList.contains("open")+JSON.stringify(__err)') 'R:truetruetrue[]'
    PageTest 'titre écrit à la main gardé' 'ui.html' (
        'convs=[{id:"c1",title:"Mon titre",titleLock:true,updated:Date.now(),project:""}];conv=convs[0];__h({data:"title\nc1\nAutre"});' +
        'el("msg user","a");el("msg bot","<div class=\"md\">b</div>");askTitle("0");' +
        'document.title="R:"+(convs[0].title==="Mon titre")+!__posts.some(x=>x.startsWith("title\n"))+JSON.stringify(__err)') 'R:truetrue[]'
    PageTest 'réglages dans le rond' 'ui.html' (
        '$("#cfg").click();var a=__posts.includes("settings")&&!!$("#orbw #cfg .gear");__h({data:"upd\n"+JSON.stringify({available:true,latest:"9.9",current:"1.50"})});' +
        '$("#updot").click();var b=$("#updpop").classList.contains("open");$("#ddProj").click();var c=!$("#updpop").classList.contains("open")&&$("#ddP").classList.contains("open");' +
        'document.dispatchEvent(new KeyboardEvent("keydown",{key:"Escape"}));var d=!$("#ddP").classList.contains("open")&&!__posts.includes("hide");' +
        'document.title="R:"+a+b+c+d+JSON.stringify(__err)') 'R:truetruetruetrue[]'
    # v1.51 : historique en fichiers, recherche, rappels, sélection, point du jour, image collée
    PageTest 'historique en fichiers' 'ui.html' (
        'var a=convs.length===2&&!__posts.some(p=>p.startsWith("conv"))&&localStorage.getItem("convs")===null;' +
        'convs.find(c=>c.id==="c1").title="Nouveau";persist();var s=__posts.filter(p=>p.startsWith("conv"));var b=s.length===1&&s[0].startsWith("convsave\nc1\n{");' +
        'convs=convs.filter(c=>c.id!=="c2");persist();var c=__posts.includes("convdel\nc2");persist();var d=__posts.filter(p=>p.startsWith("conv")).length===2;' +
        'document.title="R:"+a+b+c+d+JSON.stringify(__err)') 'R:truetruetruetrue[]' (
        '<script>window.__CONVS=[{"id":"c1","title":"Un","updated":1,"project":"","hist":"<div class=\"msg user\">x</div>"},{"id":"c2","title":"Deux","updated":2,"project":""}]</script>')
    PageTest 'recherche dans l historique' 'ui.html' (
        'convs=[{id:"c1",title:"Maths",updated:Date.now(),project:"p1",hist:"<div class=\"md\">Le courant électrique</div>"},{id:"c2",title:"Autre",updated:Date.now(),project:"",hist:"rien"}];project="";' +
        '$("#ddProj").click();var s=$("#mSearch");s.value="ELECTRIQUE";s.dispatchEvent(new Event("input"));var r=document.querySelectorAll("#mConvs .mitem");' +
        'var a=r.length===1&&r[0].dataset.c==="c1"&&!!r[0].querySelector("mark");s.value="zzz";s.dispatchEvent(new Event("input"));var b=/Aucune/.test($("#mConvs").textContent);' +
        'document.title="R:"+a+b+JSON.stringify(__err)') 'R:truetrue[]'
    PageTest 'palette : projets archivés cachés, clavier' 'ui.html' (
        'projects=["alpha","beta","gamma"];ARCH=new Set(["beta"]);convs=[{id:"c1",title:"A1",updated:Date.now(),project:"alpha"}];project="";' +
        'document.dispatchEvent(new KeyboardEvent("keydown",{key:"k",ctrlKey:true,bubbles:true}));var a=$("#ddP").classList.contains("open");' +
        'const tl=()=>[...document.querySelectorAll("#mProjects .ptile")].map(x=>x.dataset.p).join();var b=tl()==="alpha,gamma"&&!$("#pArchived").hidden;' +
        'var s=$("#mSearch");s.value="bet";s.dispatchEvent(new Event("input"));var c=tl()==="beta"&&!!$("#mProjects .ptile.arch");' +
        's.value="";s.dispatchEvent(new Event("input"));$("#mProjects [data-p=alpha]").click();' +
        'var d=project==="alpha"&&$("#ddP").classList.contains("open")&&!!$("#mConvs [data-c=c1]")&&!!$("#mConvs [data-start]");' +
        '$("#pal").dispatchEvent(new KeyboardEvent("keydown",{key:"ArrowDown",bubbles:true}));var e=kbEl===$("#mNoProj");var hit=[...document.querySelectorAll("#pal .ptile, #pal #mConvs [data-c]")].filter(x=>{const r=x.getBoundingClientRect();if(r.bottom>innerHeight||r.top<0)return true;const el=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);return el&&el.closest("#pal");}).length===document.querySelectorAll("#pal .ptile, #pal #mConvs [data-c]").length;e=e&&hit;' +
        'document.title="R:"+a+b+c+d+e+JSON.stringify(__err)') 'R:truetruetruetruetrue[]'
    PageTest 'glisser une conversation' 'ui.html' (
        'projects=["alpha","beta"];GROUPS={alpha:["G1"]};convs=[{id:"c1",title:"Un",updated:Date.now(),project:"alpha"},{id:"c2",title:"Deux",updated:Date.now()-9,project:"alpha",group:"G1"}];project="alpha";$("#ddProj").click();' +
        'const P=(t,el,x,y,b)=>el.dispatchEvent(new PointerEvent(t,{clientX:x,clientY:y,button:0,buttons:b,bubbles:true}));const ctr=el=>{const r=el.getBoundingClientRect();return[r.left+r.width/2,r.top+r.height/2];};' +
        'const dragTo=(id,q)=>{const s=$("#mConvs [data-c="+id+"] .tx"),[x,y]=ctr(s);P("pointerdown",s,x,y,1);P("pointermove",document,x+20,y+20,1);const tg=typeof q==="string"?$(q):q();tg.scrollIntoView({block:"center"});const[a,b]=ctr(tg);P("pointermove",document,a,b,1);P("pointerup",document,a,b,0);};' +
        'dragTo("c1",()=>[...document.querySelectorAll("#mConvs .mgrp[data-g]")].find(m=>m.dataset.g==="G1"));var a=convs[0].group==="G1"&&convs[0].project==="alpha";' +
        'dragTo("c1","#mConvs [data-dz=loose]");var b=convs[0].group==="";' +
        'dragTo("c2","#mConvs [data-dz=new]");var c=convs[1].group==="Nouveau groupe"&&GROUPS.alpha.includes("Nouveau groupe")&&!!$("#mConvs input.gren");' +
        'commitEdit($("#mConvs input.gren"),true);dragTo("c1","#mProjects [data-p=beta]");var d=convs[0].project==="beta"&&convs[0].group===""&&!$("#dghost")&&!document.body.classList.contains("dragging");' +
        'document.title="R:"+a+b+c+d+JSON.stringify(__err)') 'R:truetruetruetrue[]'
    PageTest 'rappel dans la réponse' 'ui.html' ($sim +
        'input.value="Rappelle-moi";submit();const k=rkey;send("rline\n"+k+"\n"+asst("C est noté.\n```bebene-rappel\n{\"quand\":\"2030-01-02T08:30\",\"quoi\":\"Sortir le linge\"}\n```"));send("rdone\n"+k+"\n0\n");' +
        'var c=$("#log .rcard");var a=!!c&&/Sortir le linge/.test(c.textContent)&&!/bebene-rappel/.test($("#log").textContent);c.querySelector(".rx").click();' +
        'var b=__posts.includes("remindcancel\n2030-01-02T08:30|Sortir le linge")&&!!$("#log .rcard .rdone");document.title="R:"+a+b+JSON.stringify(__err)') 'R:truetrue[]'
    PageTest 'demander sur la sélection' 'ui.html' (
        'const send=m=>__h({data:m});send("selection\nBonjour le monde");var a=$("#selbar").classList.contains("on")&&/Bonjour/.test($("#selbar").textContent);' +
        '$("#selbar [data-a=trad]").click();const p=__posts.filter(x=>x.startsWith("send\n")).pop()||"";var b=/Traduis/.test(p)&&/Bonjour le monde/.test(p)&&!$("#selbar").classList.contains("on");' +
        'send("selection\n");var c=!$("#selbar").classList.contains("on");document.title="R:"+a+b+c+JSON.stringify(__err)') 'R:truetruetrue[]'
    PageTest 'point du jour' 'ui.html' (
        'NOTES={enabled:true,notes:[{id:"a",title:"Acheter du pain",pinned:true,shown:true}]};convs=[{id:"c5",title:"Rapport",attn:"pause",updated:1,project:""}];' +
        '__h({data:"brief\n"+JSON.stringify({date:"samedi 22 novembre",today:[{t:"Cours de maths",h:"08:15",l:"Salle 3",k:"",m:false}],soon:[{t:"Examen physique",h:"lun. 24 nov.",k:"exam",m:true}],rappels:[{t:"Appeler Paul",h:"14:00"}]})});' +
        'var b=$("#brief");var a=b.classList.contains("on")&&["Cours de maths","Examen physique","Appeler Paul","Acheter du pain","Rapport"].every(x=>b.textContent.includes(x));' +
        'b.querySelector(".x").click();document.title="R:"+a+!b.classList.contains("on")+JSON.stringify(__err)') 'R:truetrue[]'
    PageTest 'coller une image' 'ui.html' (
        'const dt=new DataTransfer();dt.items.add(new File([new Uint8Array([137,80,78,71])],"a.png",{type:"image/png"}));' +
        'input.dispatchEvent(new ClipboardEvent("paste",{clipboardData:dt,bubbles:true,cancelable:true}));' +
        'let n=0;const w=setInterval(()=>{var a=__posts.some(p=>p.startsWith("pasteimg\ndata:image/png;base64,"));if(!a&&++n<40)return;clearInterval(w);' +
        '__h({data:"pasted\nC:\\t\\img.png"});var b=/img\.png/.test(input.value);document.title="R:"+a+b+JSON.stringify(__err)},50)') 'R:truetrue[]'
    PageResults
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
