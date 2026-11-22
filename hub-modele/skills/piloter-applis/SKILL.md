---
name: piloter-applis
description: Agir dans les programmes Windows de l'utilisateur — Excel, Word, PowerPoint, Outlook, Inventor ou n'importe quelle autre appli — pour modifier un fichier ouvert, remplir un champ, cliquer un bouton, mettre en forme, créer un graphique, etc. Utiliser dès que l'utilisateur demande d'agir « dans » un programme ou sur « le fichier ouvert devant moi ».
---

# Piloter les programmes de l'utilisateur (Windows, PowerShell)

Lancer les scripts avec `powershell -NoProfile -Command "…"` (ou un `.ps1` dans `$env:TEMP` s'il est long).

## Règles
- **Un fichier ouvert est verrouillé** : passer par le programme qui l'a ouvert, jamais par une copie.
- Méthode, du plus fiable au plus universel : **1. API COM du programme → 2. sa ligne de commande → 3. UI Automation → 4. le fichier directement** (texte, CSV, code).
- Ne jamais envoyer, supprimer ou écraser quelque chose d'important sans accord (mail : créer un brouillon et l'afficher, pas `Send()`).
- Finir en disant précisément ce qui a changé (quelle feuille, quel graphique, quel paragraphe…).
- `GetActiveObject` qui échoue avec `0x800401E3` : le programme n'est pas ouvert (ou tourne en administrateur) → l'ouvrir via `New-Object -ComObject`.

## Multitâche (l'utilisateur travaille en même temps)
L'utilisateur continue d'utiliser son PC pendant que la tâche tourne. **Ne jamais lui prendre la souris, le clavier ni le premier plan sans son accord.**
- Sans gêne (à préférer) : COM, ligne de commande, fichiers, UI Automation **par patterns** (`InvokePattern.Invoke()`, `ValuePattern.SetValue()`, `SelectionItemPattern`…) : ils agissent sans bouger le curseur ni changer la fenêtre active.
- Interdit sans accord : `SendKeys`, `AppActivate`, `SetFocus()`, `SetForegroundWindow`, `SendInput` / clics simulés, déplacement du curseur, outil `computer` de Chrome (clics, frappe) sur un onglet que l'utilisateur regarde. Pour le web : travailler dans **son propre onglet** (`tabs_create_mcp`).
- Ouvrir un programme pour un traitement : le garder invisible si possible (`$app.Visible = $false`) et le refermer s'il n'était pas ouvert avant.
- **Besoin réel de la souris / du clavier, ou d'une fenêtre impossible à capturer** (réduite, cachée, autre bureau, écran verrouillé) : ne pas forcer. Dans Bébène, terminer la réponse par un bloc ```` ```bebene-main ```` contenant `{"quoi":"ce dont tu as besoin et pourquoi","type":"souris"}` (ou `"type":"ecran"`) et s'arrêter : le chat prévient l'utilisateur, qui donne la main ; la tâche reprend ensuite. Dans le terminal : le demander simplement.

## 1. API COM (programmes ouverts)
```powershell
# Excel
$xl = [Runtime.InteropServices.Marshal]::GetActiveObject('Excel.Application')
$wb = $xl.ActiveWorkbook                      # ou $xl.Workbooks.Item('Nom.xlsx')
$ws = $wb.Worksheets.Item('Feuille1'); $ws.Range('A1').Value2 = 'texte'
$ch = $ws.ChartObjects(1).Chart; $ch.Axes(2).ScaleType = -4133   # axe Y en log (xlScaleLogarithmic) ; Axes(1) = X
$wb.Save()

# Word
$w = [Runtime.InteropServices.Marshal]::GetActiveObject('Word.Application'); $doc = $w.ActiveDocument
$doc.Content.Find.Execute('ancien', $false, $false, $false, $false, $false, $true, 1, $false, 'nouveau', 2)  # remplacer partout
$doc.Paragraphs.Item(1).Range.Style = 'Titre 1'; $doc.Save()

# PowerPoint
$pp = [Runtime.InteropServices.Marshal]::GetActiveObject('PowerPoint.Application'); $p = $pp.ActivePresentation
$p.Slides.Item(1).Shapes.Item(1).TextFrame.TextRange.Text = 'Nouveau titre'; $p.Save()

# Outlook classique : brouillon affiché, jamais envoyé sans accord
$o = New-Object -ComObject Outlook.Application; $m = $o.CreateItem(0)
$m.To = 'x@y.ch'; $m.Subject = 'Objet'; $m.Body = 'Texte'; $m.Display()

# Autodesk Inventor : paramètres de la pièce ouverte
$inv = [Runtime.InteropServices.Marshal]::GetActiveObject('Inventor.Application'); $d = $inv.ActiveDocument
$d.ComponentDefinition.Parameters.Item('d0').Expression = '20 mm'; $d.Update(); $d.Save()
```
Fichier fermé : `$xl = New-Object -ComObject Excel.Application; $xl.Visible = $false; $wb = $xl.Workbooks.Open($chemin)` … `$wb.Save(); $wb.Close(); $xl.Quit()`.
Constante inconnue : chercher sa valeur numérique (ex. `xlScaleLogarithmic = -4133`) ; explorer un objet avec `$obj | Get-Member`.

## 2. Ligne de commande
Si le programme en a une (`code`, `arduino-cli`, `inkscape`, `ffmpeg`…), la préférer à l'interface.

## 3. UI Automation (n'importe quelle appli avec une interface)
```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]; $T = [Windows.Automation.TreeScope]
$win = $A::RootElement.FindAll($T::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like '*Titre partiel*' } | Select-Object -First 1
# repérer les contrôles :
$win.FindAll($T::Descendants, [Windows.Automation.Condition]::TrueCondition) | Select-Object -First 80 | ForEach-Object { $_.Current.ControlType.ProgrammaticName + ' | ' + $_.Current.Name + ' | ' + $_.Current.AutomationId }
$c = New-Object Windows.Automation.PropertyCondition($A::NameProperty, 'Enregistrer')
$win.FindFirst($T::Descendants, $c).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()        # cliquer
$champ.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('texte')                                # remplir un champ
```
Dernier recours, raccourcis clavier : `$s = New-Object -ComObject WScript.Shell; $s.AppActivate('Titre'); $s.SendKeys('^s')`. **Seulement avec son accord** (voir Multitâche) : il ne doit pas toucher au clavier pendant ce temps.

## 4. Sites web (remplir un champ, un formulaire, « about me »…)
Ne pas refuser : remplir à la place de l'utilisateur, mais ne rien **envoyer / valider / publier** sans son accord.
1. **Claude in Chrome** (outils `mcp__claude-in-chrome__*`) : pour une page que l'utilisateur a déjà remplie à moitié, `tabs_context_mcp` → trouver son onglet (ne pas le recharger : on perdrait ce qui est saisi) → `find` / `read_page` pour repérer le champ → `form_input` pour le remplir (`form_input` ne prend pas la souris ; `computer` oui : voir Multitâche). Pour une recherche ou une nouvelle page : ouvrir **son propre onglet** (`tabs_create_mcp`).
2. **Sans l'extension, ou Edge / Firefox** : UI Automation (section 3). Les champs de page web sont des contrôles `Edit` (ou `Document` pour les zones riches) dans la fenêtre du navigateur : chercher par `Name` (libellé du champ), puis `ValuePattern.SetValue`. Si `SetValue` échoue (zone riche) : `SetFocus()` sur l'élément, puis coller le texte (`Set-Clipboard` + `SendKeys('^v')`).
3. Avant d'écrire un texte personnel (présentation, bio…) : chercher les infos dans la mémoire ClaudeHub ; s'il manque une info capitale, la demander.
