# Compile Claude Companion (Minuit) depuis ce dossier vers le dossier d'installation local.
#   -Install : crée aussi le raccourci du menu Démarrer, le lancement au démarrage, et lance l'app
#   -NoStart : compile seulement, sans lancer Bébène (intégration continue, mesures)
param([string]$Out = "$HOME\ClaudeCompanion", [switch]$Install, [switch]$NoStart)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = $PSScriptRoot
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
# arrête seulement la copie de Bébène qu'on remplace (pas celle installée quand on compile ailleurs)
Get-Process ClaudeCompanion -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Out, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# WebView2 (téléchargé une fois, gardé en local)
# (cache de compilation hors du dossier installe : 52 Mo qui ne servent pas a faire tourner Bebene)
$lib = "$env:LOCALAPPDATA\BebeneBuild\webview2-1.0.2903.40"
if (Test-Path "$Out\lib") { Remove-Item "$Out\lib" -Recurse -Force -ErrorAction SilentlyContinue }   # ancien emplacement
if (Test-Path "$Out\companion.ico") { Remove-Item "$Out\companion.ico" -Force -ErrorAction SilentlyContinue }   # ancienne icone
if (-not (Test-Path "$lib\lib\net462\Microsoft.Web.WebView2.WinForms.dll")) {
    New-Item -ItemType Directory -Force $lib | Out-Null
    $zip = "$env:TEMP\webview2-1.0.2903.40.zip"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2903.40" -OutFile $zip
    Expand-Archive $zip $lib -Force
    Remove-Item $zip -Force
}
Copy-Item "$lib\lib\net462\Microsoft.Web.WebView2.Core.dll", "$lib\lib\net462\Microsoft.Web.WebView2.WinForms.dll", "$lib\runtimes\win-x64\native\WebView2Loader.dll" $Out -Force
Copy-Item "$src\ui.html", "$src\settings.html", "$src\fonctions.json" $Out -Force

# Icône : assets\icone-bebene.png en plusieurs tailles (nette partout : barre des tâches, menu Démarrer, notifications)
$srcImg = [Drawing.Image]::FromFile("$src\assets\icone-bebene.png")
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $b = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($b)
    # pixel art : tailles multiples de 32 en vrais pixels nets ; petites tailles adoucies pour rester lisibles
    if ($s % 32 -eq 0) { $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half' }
    else { $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality' }
    $g.DrawImage($srcImg, 0, 0, $s, $s); $g.Dispose()
    $ms = New-Object IO.MemoryStream
    if ($s -ge 256) { $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png) }       # 256 px : PNG (standard)
    else {                                                                       # autres tailles : bitmap 32 bits classique (lu correctement partout)
        $bw = New-Object IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $b.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)                        # masque ET (vide : la transparence vient de l'alpha)
        $bw.Write((New-Object byte[] ($maskRow * $s))); $bw.Flush()
    }
    $frames += ,$ms.ToArray(); $b.Dispose()
}
$srcImg.Dispose()
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$off)
    $off += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[IO.File]::WriteAllBytes("$Out\bebene.ico", $ico.ToArray())

# Compilation
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:x64 /unsafe /optimize+ /codepage:65001 /win32icon:"$Out\bebene.ico" /out:"$Out\ClaudeCompanion.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll `
    /lib:"$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll `
    /r:"$Out\Microsoft.Web.WebView2.Core.dll" /r:"$Out\Microsoft.Web.WebView2.WinForms.dll" (Get-ChildItem "$src\*.cs").FullName   # Companion.cs (chat) + Discussion, PostIts, Agenda, Modeles, Installation, Reglages
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation" }

if ($Install) {
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Bébène.lnk")
    $sh.TargetPath = "$Out\ClaudeCompanion.exe"; $sh.IconLocation = "$Out\bebene.ico,0"; $sh.WorkingDirectory = $Out; $sh.Description = 'Ton compagnon Claude'; $sh.Save()
    Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run ClaudeCompanion "`"$Out\ClaudeCompanion.exe`" --background"
}
if (-not $NoStart) { Start-Process "$Out\ClaudeCompanion.exe" -ArgumentList '--background' }
"OK"
