import AppKit

// ---------- Le centre de contrôle : settings.html (même fichier que Windows) ----------
final class SettingsWindow: NSObject, NSWindowDelegate {
    let page: WebPage
    let window: NSWindow
    weak var app: AppDelegate?
    var pendingGoto: String?

    init(app: AppDelegate) {
        self.app = app
        page = WebPage(page: "settings.html")
        window = glassWindow(size: NSSize(width: 620, height: 760), title: "Réglages de " + Prefs.name, web: page.web)
        super.init()
        window.delegate = self
        window.center()
        page.onMessage = { [weak self] m in self?.onMessage(m) }
    }
    func show(_ section: String?) {
        if let s = section { pendingGoto = s; page.post("goto\n" + s) }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
    func refresh() { page.post("settings\n" + settingsJson()) }

    func features() -> Any {
        guard let d = FileManager.default.contents(atPath: (Paths.res as NSString).appendingPathComponent("fonctions.json")),
              let arr = (try? JSONSerialization.jsonObject(with: d)) as? [[String: Any]] else { return [] }
        return arr.filter { ($0["os"] as? String ?? "") != "win" }            // fonctions propres à Windows : pas affichées
    }
    func notifPrefs() -> [String: Bool] {
        var r: [String: Bool] = [:]
        for k in ["fini", "question", "main", "pret", "rappel", "erreur"] { r[k] = Prefs.bool("Notif_" + k, true) }
        return r
    }
    func settingsJson() -> String {
        let p = app?.panel
        let d: [String: Any] = [
            "os": "mac", "name": Prefs.name, "model": Prefs.str("Model", ""), "scale": Prefs.int("Scale", 100), "activity": Prefs.int("Activity", 50),
            "showCat": Prefs.bool("ShowCat", true), "topMost": Prefs.bool("TopMost", true), "autoStart": app?.autoStart ?? false,
            "prompt": Prefs.str("Prompt", ""), "hotkey1": "Alt+Space", "dailyBrief": Prefs.bool("DailyBrief", true), "notifs": notifPrefs(),
            "hotkeyOk": Prefs.bool("HotkeyOk", true), "stream": Prefs.bool("Stream", true), "light": Prefs.bool("Light", false),
            "autoModel": Prefs.bool("AutoModel", true), "caveman": Prefs.bool("Caveman", true), "chrome": Prefs.bool("Chrome", true),
            "postits": Prefs.bool("PostIts", true), "prepare": Prefs.bool("Prepare", true), "autoAllow": Prefs.str("AutoAllow", ""),
            "remindExam": Prefs.bool("RemindExam", true), "remindRdv": Prefs.bool("RemindRdv", true), "remindWhen": Prefs.str("RemindWhen", "both"),
            "features": features(), "manual": [Any](), "hidden": [String](), "calendars": [Any](), "locFilter": Prefs.str("LocFilter", ""),
            "gmailUser": "", "gmailSet": false, "outlookOn": false, "mailStatus": "", "mailRunning": false,
            "workDir": p?.workDir ?? Paths.home, "hasSession": !(app?.sessionId ?? "").isEmpty, "version": appVersion]
        return J.enc(d)
    }

    func apply(_ json: String) {
        guard let d = J.dec(json) as? [String: Any] else { return }
        for (k, v) in d {
            switch k {
            case "name":
                var n = "\(v)".trimmingCharacters(in: .whitespaces); if n.count > 30 { n = String(n.prefix(30)) }
                Prefs.set("Name", n); app?.panel.post("name\n" + Prefs.name)
            case "model": let m = "\(v)"; if m.isEmpty || matches(m, "^[a-z0-9.-]+$") { Prefs.set("Model", m) }
            case "prompt": Prefs.set("Prompt", String("\(v)".prefix(4000)))
            case "scale": Prefs.set("Scale", (v as? Int) ?? 100); app?.cat?.applyScale()
            case "showCat": app?.showCat((v as? Bool) ?? true)
            case "activity": Prefs.set("Activity", max(0, min(100, (v as? Int) ?? 50)))
            case "topMost": Prefs.set("TopMost", (v as? Bool) ?? true); app?.cat?.applyLevel()
            case "light": Prefs.set("Light", (v as? Bool) ?? false); app?.panel.postTheme()
            case "caveman": Prefs.set("Caveman", (v as? Bool) ?? true)
            case "stream": Prefs.set("Stream", (v as? Bool) ?? true)
            case "autoModel": Prefs.set("AutoModel", (v as? Bool) ?? true)
            case "dailyBrief": Prefs.set("DailyBrief", (v as? Bool) ?? true)
            case "chrome": Prefs.set("Chrome", (v as? Bool) ?? true)
            case "postits": Prefs.set("PostIts", (v as? Bool) ?? true)
            case "prepare": Prefs.set("Prepare", (v as? Bool) ?? true)
            case "remindExam": Prefs.set("RemindExam", (v as? Bool) ?? true)
            case "remindRdv": Prefs.set("RemindRdv", (v as? Bool) ?? true)
            case "remindWhen": let w = "\(v)"; if ["both", "veille", "heure"].contains(w) { Prefs.set("RemindWhen", w) }
            case "locFilter": Prefs.set("LocFilter", "\(v)".trimmingCharacters(in: .whitespaces))
            case "notif": if let nd = v as? [String: Any] { for (nk, nv) in nd { Prefs.set("Notif_" + nk, (nv as? Bool) ?? true) } }
            case "autoAllow":
                let ok = "\(v)".split(separator: ",").map(String.init).filter { matches($0, "^[A-Za-z][A-Za-z0-9_-]*$") }
                Prefs.set("AutoAllow", ok.joined(separator: ","))
            case "hotkeyPause": app?.pauseHotKey()
            case "hotkeyResume": app?.registerHotKey()
            case "autoStart": app?.autoStart = (v as? Bool) ?? false
            default: break
            }
        }
    }

    func onMessage(_ m: String) {
        let i = m.firstIndex(of: "\n")
        let k = i.map { String(m[..<$0]) } ?? m, v = i.map { String(m[m.index(after: $0)...]) } ?? ""
        switch k {
        case "get":
            page.markReady(); refresh()
            if let g = pendingGoto { page.post("goto\n" + g); pendingGoto = nil }
        case "set": apply(v); refresh()
        case "setupcheck": setupCheck()
        case "setupdo": page.post("setupmsg\n" + Setup.run(v.trimmingCharacters(in: .whitespacesAndNewlines))); setupCheck()
        case "folder": app?.panel.pickFolder(); refresh()
        case "chat": app?.openChat()
        case "terminal": app?.openTerminal(resume: false)
        case "resume": app?.openTerminal(resume: true)
        case "quit": NSApp.terminate(nil)
        case "projlist": page.post("projlist\n" + Projects.json())
        case "projarch":
            let p = v.split(separator: "\n").map(String.init)
            if let n = p.first { Projects.setArchived(n, p.count > 1 && p[1] == "1") }
            page.post("projlist\n" + Projects.json()); app?.panel.postProjects()
        case "tpl": page.post("tpl\n" + J.enc(["dir": (Paths.hub as NSString).appendingPathComponent("modeles"), "kinds": [Any]()]))
        default: break
        }
    }
    func setupCheck() {
        page.post("setupbusy")
        DispatchQueue.global().async { let r = Setup.check(); self.page.post("setup\n" + J.enc(r)) }
    }
    func windowShouldClose(_ s: NSWindow) -> Bool { s.orderOut(nil); app?.registerHotKey(); return false }
}

// ---------- Assistant d'installation : vérifications (même format que Windows : {id, st, msg}) ----------
enum Setup {
    static func s(_ id: String, _ st: String, _ msg: String) -> [String: String] { ["id": id, "state": st, "detail": msg] }
    static func check() -> [[String: String]] {
        var r: [[String: String]] = []
        let v = ProcessInfo.processInfo.operatingSystemVersion
        r.append(s("windows", v.majorVersion >= 13 ? "ok" : "no", "macOS \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"))
        r.append(s("webview", "ok", "WebKit (intégré à macOS)"))
        let cv = Shell.which("claude").flatMap { Shell.run($0, ["--version"], timeout: 25, env: Shell.env) }
        r.append(s("claude", cv != nil ? "ok" : "no", cv.map { String($0.split(separator: "\n").first ?? "") } ?? "Claude Code n'est pas installé"))
        let home = Paths.home as NSString
        let login = FileManager.default.fileExists(atPath: home.appendingPathComponent(".claude/.credentials.json"))
            || (Shell.run("/usr/bin/security", ["find-generic-password", "-s", "Claude Code-credentials"], timeout: 5) != nil)
            || ProcessInfo.processInfo.environment["ANTHROPIC_API_KEY"] != nil
        r.append(s("login", login ? "ok" : "no", login ? "Compte connecté" : "Pas encore connecté à ton compte Claude"))
        let hub = Paths.hub
        let cloud = hub.contains("CloudStorage") || hub.contains("Mobile Documents") || hub.contains("OneDrive")
        r.append(s("onedrive", cloud ? "ok" : "warn", cloud ? hub : "Mémoire locale (ni OneDrive ni iCloud) : elle restera sur ce Mac"))
        let hasHub = FileManager.default.fileExists(atPath: (hub as NSString).appendingPathComponent("memoire/00-noyau.md"))
        r.append(s("hub", hasHub ? "ok" : "no", hasHub ? hub : "Mémoire ClaudeHub introuvable"))
        let md = home.appendingPathComponent(".claude/CLAUDE.md")
        let wired = ((try? String(contentsOfFile: md, encoding: .utf8)) ?? "").contains("memoire/00-noyau.md")
        r.append(s("wire", wired ? "ok" : "no", wired ? "Mémoire branchée dans Claude Code" : "Claude Code ne connaît pas encore ClaudeHub"))
        let rtk = Shell.which("rtk") != nil, node = Shell.which("node") != nil
        let cave = FileManager.default.fileExists(atPath: home.appendingPathComponent(".claude/plugins/cache/caveman"))
        r.append(s("tools", rtk && node && cave ? "ok" : "warn", (rtk ? "RTK ✓" : "RTK ✗") + " · " + (node ? "Node ✓" : "Node ✗") + " · " + (cave ? "Caveman ✓" : "Caveman ✗")))
        let inApps = Bundle.main.bundlePath.hasPrefix("/Applications") || Bundle.main.bundlePath.hasPrefix(home.appendingPathComponent("Applications"))
        r.append(s("install", inApps ? "ok" : "no", inApps ? "Installé dans " + Bundle.main.bundlePath : "Glisse Bebene.app dans le dossier Applications"))
        return r
    }
    // actions de l'assistant : sur Mac, on ouvre ce qu'il faut et on explique (pas d'installation silencieuse)
    static func run(_ what: String) -> String {
        switch what {
        case "claude":
            AppDelegate.shared.runInTerminal("curl -fsSL https://claude.ai/install.sh | bash")
            return "Installation de Claude Code lancée dans le Terminal."
        case "login":
            AppDelegate.shared.runInTerminal("claude")
            return "Claude Code s'ouvre dans le Terminal : connecte-toi, puis reviens ici."
        case "hub":
            let hub = Paths.hub as NSString
            for d in ["memoire", "journal", "projets"] { try? FileManager.default.createDirectory(atPath: hub.appendingPathComponent(d), withIntermediateDirectories: true) }
            let noyau = hub.appendingPathComponent("memoire/00-noyau.md")
            if !FileManager.default.fileExists(atPath: noyau) {
                try? "# Noyau de mémoire (chargé dans chaque session : rester court)\n\n## Préférences\n- Toujours répondre en français.\n".write(toFile: noyau, atomically: true, encoding: .utf8)
            }
            return "Mémoire ClaudeHub créée dans " + (hub as String) + "."
        case "wire":
            let md = (Paths.home as NSString).appendingPathComponent(".claude/CLAUDE.md")
            try? FileManager.default.createDirectory(atPath: (md as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            var cur = (try? String(contentsOfFile: md, encoding: .utf8)) ?? ""
            if !cur.contains("memoire/00-noyau.md") {
                cur += (cur.isEmpty ? "" : "\n") + "# Mémoire centrale (ClaudeHub)\n@" + (Paths.hub as NSString).appendingPathComponent("memoire/00-noyau.md") + "\n"
                try? cur.write(toFile: md, atomically: true, encoding: .utf8)
            }
            return "Claude Code charge maintenant la mémoire ClaudeHub."
        case "install":
            NSWorkspace.shared.open(URL(fileURLWithPath: "/Applications"))
            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: Bundle.main.bundlePath)])
            return "Glisse Bebene.app dans Applications, puis relance-le depuis là."
        default: return "Rien à faire ici sur Mac."
        }
    }
}

extension AppDelegate {
    func runInTerminal(_ cmd: String) {
        let src = "tell application \"Terminal\"\nactivate\ndo script \"" + cmd.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\"\nend tell"
        var err: NSDictionary?
        NSAppleScript(source: src)?.executeAndReturnError(&err)
    }
}
