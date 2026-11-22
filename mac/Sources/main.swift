import AppKit

// ---------- point d'entrée : app « agent » (pas d'icône dans le Dock, LSUIElement dans Info.plist) ----------
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
signal(SIGPIPE, SIG_IGN)                       // Claude fermé pendant un envoi : erreur gérée, pas de plantage
// menu Édition : sans lui, ⌘C / ⌘V / ⌘A ne marchent pas dans les pages
let mainMenu = NSMenu(), editItem = NSMenuItem()
mainMenu.addItem(NSMenuItem()); mainMenu.addItem(editItem)
let edit = NSMenu(title: "Édition")
edit.addItem(withTitle: "Annuler", action: Selector(("undo:")), keyEquivalent: "z")
edit.addItem(withTitle: "Rétablir", action: Selector(("redo:")), keyEquivalent: "Z")
edit.addItem(.separator())
edit.addItem(withTitle: "Couper", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
edit.addItem(withTitle: "Copier", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
edit.addItem(withTitle: "Coller", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
edit.addItem(withTitle: "Tout sélectionner", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
edit.addItem(withTitle: "Fermer la fenêtre", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
editItem.submenu = edit
app.mainMenu = mainMenu
app.run()
