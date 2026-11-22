import AppKit
import Carbon
import ServiceManagement
import UserNotifications

// appVersion : généré par build-mac.sh depuis Companion.cs (même numéro que Windows)

// ---------- Application : barre des menus, raccourci ⌥Espace, notifications, rappels ----------
final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    static var shared: AppDelegate!
    var status: NSStatusItem!
    var panel: Discussion!
    var settings: SettingsWindow?
    var cat: CatWindow?
    var sessionId = ""
    var lastApp = ""                              // dernière appli au premier plan (hors Bébène) : « l'application active »
    var hotKey: EventHotKeyRef?
    var handlerInstalled = false

    func applicationDidFinishLaunching(_ n: Notification) {
        AppDelegate.shared = self
        _ = Shell.path                             // PATH du terminal lu tout de suite (premier message plus rapide)
        buildMenu()
        panel = Discussion()
        panel.app = self
        panel.onCat = { [weak self] st, d, t in self?.cat?.setState(st, done: d, total: t); self?.setStatusIcon(st) }
        if Prefs.bool("ShowCat", true) { showCat(true) }
        registerHotKey()
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let a = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication, a.processIdentifier != getpid() else { return }
            self?.lastApp = a.localizedName ?? ""
        }
        let nc = UNUserNotificationCenter.current(); nc.delegate = self
        nc.requestAuthorization(options: [.alert, .sound]) { _, _ in }
        Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in self?.checkReminders() }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in self?.cat?.clampToScreen() }
        DistributedNotificationCenter.default().addObserver(forName: Notification.Name("AppleInterfaceThemeChangedNotification"), object: nil, queue: .main) { [weak self] _ in self?.panel.postTheme() }
        if !Prefs.bool("Welcomed", false) { Prefs.set("Welcomed", true); showSettings("setup") }
    }
    func applicationWillTerminate(_ n: Notification) { panel.killAll(); if panel.flash { panel.forget(sessionId) } }
    func applicationShouldHandleReopen(_ s: NSApplication, hasVisibleWindows f: Bool) -> Bool { panel.show(near: cat?.window.frame); return true }

    // ---- barre des menus ----
    func buildMenu() {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        setStatusIcon("idle")
        let m = NSMenu()
        m.addItem(withTitle: "Discussion", action: #selector(openChat), keyEquivalent: "").target = self
        m.addItem(withTitle: "Afficher le chat", action: #selector(toggleCat), keyEquivalent: "").target = self
        m.addItem(.separator())
        m.addItem(withTitle: "Ouvrir Claude Code (Terminal)", action: #selector(openTerminalNew), keyEquivalent: "").target = self
        m.addItem(withTitle: "Reprendre la conversation dans Claude Code", action: #selector(openTerminalResume), keyEquivalent: "").target = self
        m.addItem(.separator())
        m.addItem(withTitle: "Réglages…", action: #selector(openSettings), keyEquivalent: ",").target = self
        m.addItem(withTitle: "Quitter " + Prefs.name, action: #selector(quit), keyEquivalent: "q").target = self
        m.delegate = self
        status.menu = m
    }
    func setStatusIcon(_ state: String) {
        let sym: String
        switch state {
        case "thinking": sym = "ellipsis.bubble"
        case "question", "hand": sym = "exclamationmark.bubble"
        case "ready": sym = "checkmark.bubble"
        default: sym = "pawprint"
        }
        let img = NSImage(systemSymbolName: sym, accessibilityDescription: Prefs.name) ?? NSImage(systemSymbolName: "bubble.left", accessibilityDescription: nil)
        img?.isTemplate = true
        status.button?.image = img
    }
    @objc func openChat() { panel.show(near: cat?.window.isVisible == true ? cat?.window.frame : nil) }
    @objc func toggleCat() { showCat(!(cat?.window.isVisible ?? false)) }
    @objc func openSettings() { showSettings(nil) }
    @objc func openTerminalNew() { openTerminal(resume: false) }
    @objc func openTerminalResume() { openTerminal(resume: true) }
    @objc func quit() { NSApp.terminate(nil) }

    func showCat(_ on: Bool) {
        Prefs.set("ShowCat", on)
        if on { if cat == nil { cat = CatWindow(app: self) }; cat?.show() } else { cat?.hide() }
    }
    func showSettings(_ section: String?) {
        if settings == nil { settings = SettingsWindow(app: self) }
        settings?.show(section)
    }

    // ---- ⌥Espace : ouvre la discussion ; depuis le Finder, joint les fichiers sélectionnés ----
    func registerHotKey() {
        if let h = hotKey { UnregisterEventHotKey(h); hotKey = nil }
        if !handlerInstalled {                                              // une seule fois : sinon un appui ouvrirait puis refermerait
            handlerInstalled = true
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                DispatchQueue.main.async { AppDelegate.shared.hotKeyPressed() }; return noErr
            }, 1, &spec, nil, nil)
        }
        let id = EventHotKeyID(signature: OSType(0x4245_4245), id: 1)       // 'BEBE'
        let st = RegisterEventHotKey(UInt32(kVK_Space), UInt32(optionKey), id, GetApplicationEventTarget(), 0, &hotKey)
        Prefs.set("HotkeyOk", st == noErr)
    }
    func pauseHotKey() { if let h = hotKey { UnregisterEventHotKey(h); hotKey = nil } }
    func hotKeyPressed() {
        if panel.window.isVisible && panel.window.isKeyWindow { panel.hide(); return }
        let front = NSWorkspace.shared.frontmostApplication
        var files: [String] = []
        if front?.bundleIdentifier == "com.apple.finder" { files = finderSelection() }
        panel.show(near: cat?.window.isVisible == true ? cat?.window.frame : nil)
        if !files.isEmpty { panel.post("selection\n" + J.enc(["text": "", "files": files.prefix(50).map { ["p": $0, "d": isDir($0)] as [String: Any] }])) }
    }
    func isDir(_ p: String) -> Bool { var d: ObjCBool = false; return FileManager.default.fileExists(atPath: p, isDirectory: &d) && d.boolValue }
    // fichiers sélectionnés dans le Finder (macOS demande une fois l'autorisation « Automatisation › Finder »)
    func finderSelection() -> [String] {
        let src = "tell application \"Finder\"\nset out to \"\"\nrepeat with i in (get selection)\nset out to out & POSIX path of (i as alias) & linefeed\nend repeat\nreturn out\nend tell"
        var err: NSDictionary?
        guard let r = NSAppleScript(source: src)?.executeAndReturnError(&err).stringValue else { return [] }
        return r.split(separator: "\n").map { String($0).hasSuffix("/") && $0.count > 1 ? String($0.dropLast()) : String($0) }.filter { !$0.isEmpty }
    }

    // ---- notifications ----
    func notify(kind: String, title: String, key: String) {
        if !Prefs.bool("Notif_" + kind, true) { return }
        let c = UNMutableNotificationContent()
        c.title = Prefs.name
        c.body = title
        c.sound = .default
        c.userInfo = ["key": key]
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }
    func userNotificationCenter(_ c: UNUserNotificationCenter, willPresent n: UNNotification, withCompletionHandler h: @escaping (UNNotificationPresentationOptions) -> Void) { h([.banner, .sound]) }
    func userNotificationCenter(_ c: UNUserNotificationCenter, didReceive r: UNNotificationResponse, withCompletionHandler h: @escaping () -> Void) {
        openChat()
        if let k = r.notification.request.content.userInfo["key"] as? String, !k.isEmpty { panel.post("openconv\n" + k) }
        h()
    }
    func checkReminders() {
        while let r = Reminders.due() {
            notify(kind: "rappel", title: "⏰ " + r.Quoi, key: "")
            cat?.say("⏰ " + r.Quoi)
        }
    }

    // ---- Claude Code dans le Terminal ----
    func openTerminal(resume: Bool) {
        let dir = (resume ? Projects.sessionDir(sessionId) : nil) ?? panel.dirFor(panel.flash, panel.project)
        var cmd = "cd " + shq(dir) + " && claude"
        if resume && isSid(sessionId) { cmd += " --resume " + sessionId }
        let src = "tell application \"Terminal\"\nactivate\ndo script \"" + cmd.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\"\nend tell"
        var err: NSDictionary?
        NSAppleScript(source: src)?.executeAndReturnError(&err)
        if err != nil { Log.write("Terminal", "\(err!)") }
    }
    func shq(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    // ---- démarrage avec la session (macOS 13+) ----
    var autoStart: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set { do { if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } } catch { Log.write("Démarrage auto", "\(error)") } }
    }
}

extension AppDelegate: NSMenuDelegate {
    func menuWillOpen(_ m: NSMenu) { m.item(at: 1)?.title = cat?.window.isVisible == true ? "Cacher le chat" : "Afficher le chat" }
}

