import AppKit
import WebKit
import UserNotifications

// ---------- La discussion : ui.html (même fichier que Windows) + sessions Claude ----------
final class Discussion: NSObject, NSWindowDelegate {
    let page: WebPage
    let window: NSWindow
    var runners: [String: Runner] = [:]
    let maxBusy = 3
    var pinned = Prefs.bool("Pinned", false)
    var flash = false, project = ""
    var workDir = Prefs.str("WorkDir", Paths.home)
    var sessionId = ""
    var onCat: ((String, Int, Int) -> Void)?                  // état du chat (idle, thinking, question, hand, ready, happy), fait, total
    weak var app: AppDelegate?

    override init() {
        page = WebPage(page: "ui.html", initScript: "window.__CONVS=" + ConvStore.allJson() + ";")
        let f = NSRect(x: 0, y: 0, width: Prefs.int("PanelW", 440), height: Prefs.int("PanelH", 640))
        window = glassWindow(size: f.size, title: Prefs.name, web: page.web)
        super.init()
        window.delegate = self
        // la page a ses propres boutons (épingler, fermer) : pas de pastilles rouge/orange/verte par-dessus
        [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton].forEach { window.standardWindowButton($0)?.isHidden = true }
        window.level = pinned ? .floating : .normal
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        page.onMessage = { [weak self] m in self?.onMessage(m) }
        page.web.onFiles = { [weak self] files in self?.page.post("paths\n" + files.joined(separator: "\n")) }
        Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in self?.idleTick() }
    }

    var visible: Bool { window.isVisible }
    func post(_ s: String) { page.post(s) }

    func show(near: NSRect? = nil) {
        if !window.isVisible { place(near: near) }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        post("focus")
    }
    func hide() { window.orderOut(nil) }
    func toggle(near: NSRect?) { if window.isVisible && window.isKeyWindow { hide() } else { show(near: near) } }
    // près du chat s'il est là, sinon en bas à droite de l'écran (au-dessus du Dock)
    func place(near: NSRect?) {
        guard let scr = NSScreen.main?.visibleFrame else { return }
        var f = window.frame
        if let n = near {
            f.origin.x = min(max(n.midX - f.width / 2, scr.minX + 8), scr.maxX - f.width - 8)
            f.origin.y = min(n.maxY + 8, scr.maxY - f.height - 8)
            if f.origin.y < scr.minY { f.origin.y = scr.minY + 8 }
        } else { f.origin = NSPoint(x: scr.maxX - f.width - 16, y: scr.minY + 16) }
        window.setFrame(f, display: false)
    }
    func windowDidEndLiveResize(_ n: Notification) { Prefs.set("PanelW", Int(window.frame.width)); Prefs.set("PanelH", Int(window.frame.height)) }
    // clic ailleurs : la discussion se range, sauf si elle est épinglée ou si une tâche attend une réponse
    func windowDidResignKey(_ n: Notification) {}

    // glisser la fenêtre depuis l'en-tête de la page (message « drag ») : suivi de la souris jusqu'au relâchement
    func dragWindow() {
        let start = NSEvent.mouseLocation, origin = window.frame.origin
        window.trackEvents(matching: [.leftMouseDragged, .leftMouseUp], timeout: .infinity, mode: .eventTracking) { ev, stop in
            guard let ev = ev else { return }
            if ev.type == .leftMouseUp { stop.pointee = true; return }
            let p = NSEvent.mouseLocation
            self.window.setFrameOrigin(NSPoint(x: origin.x + p.x - start.x, y: origin.y + p.y - start.y))
        }
    }

    func postTheme() {
        let light = Prefs.object("Light") == nil ? !isDarkMode() : Prefs.bool("Light", false)   // par défaut : suit macOS
        post("theme\n" + (light ? "light" : "dark"))
    }
    func isDarkMode() -> Bool { NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua }
    func postProjects() { post("projects\n" + Projects.json()) }
    func dirFor(_ flash: Bool, _ project: String) -> String {
        if !flash && !project.isEmpty { let d = (Paths.projects as NSString).appendingPathComponent(project); if FileManager.default.fileExists(atPath: d) { return d } }
        return workDir
    }

    // ---- messages de la page ----
    func onMessage(_ m: String) {
        let parts = m.split(separator: "\n", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
        let a1 = parts.count > 1 ? parts[1] : ""
        let rest = m.firstIndex(of: "\n").map { String(m[m.index(after: $0)...]) } ?? ""
        switch parts[0] {
        case "ready":
            page.markReady()
            post("pinned\n" + (pinned ? "1" : "0")); postTheme()
            post("dir\n" + workDir); post("name\n" + Prefs.name)
            postProjects()
            if window.isVisible { post("focus") }
        case "pastefiles":                                                  // ⌘V de fichiers copiés dans le Finder
            let files = BridgeWebView.fileURLs(NSPasteboard.general)
            if !files.isEmpty { post("paths\n" + files.joined(separator: "\n")) }
        case "convsave":
            let sp = m.split(separator: "\n", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
            if sp.count == 3 { ConvStore.save(sp[1], sp[2]) }
        case "convdel": ConvStore.delete(a1)
        case "pasteimg": pasteImage(rest)
        case "remindcancel": Reminders.cancel(rest)
        case "pin": pinned = a1 == "1"; Prefs.set("Pinned", pinned); window.level = pinned ? .floating : .normal
        case "sid": sessionId = a1; app?.sessionId = a1
        case "terminal": app?.openTerminal(resume: true)
        case "settings": app?.showSettings(a1.isEmpty ? nil : a1)
        case "hide": hide()
        case "title":
            let sp = m.split(separator: "\n", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
            if sp.count == 3 { makeTitle(sp[1], sp[2]) }
        case "savetxt":
            let sp = m.split(separator: "\n", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
            if sp.count == 3 { do { post("saved\n" + (try saveTxt(sp[1], sp[2]))) } catch { post("saveerr\n\(error.localizedDescription)") } }
        case "reveal": if FileManager.default.fileExists(atPath: rest) { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: rest)]) }
        case "copy": if !rest.isEmpty { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(rest, forType: .string) }
        case "drag": dragWindow()
        case "resize": break                                               // macOS : les bords de la fenêtre se redimensionnent d'eux-mêmes
        case "compact": post("compacted\n" + a1 + "\n0\n0\n0")              // pas encore sur Mac
        case "updopen": NSWorkspace.shared.open(URL(string: "https://github.com/P0770/bebene/releases")!)
        case "folder": pickFolder()
        case "projects": postProjects()
        case "mode":
            flash = a1 == "1"; project = safeName(parts.count > 2 ? parts[2] : "")
            post("dir\n" + dirFor(flash, project))
        case "newproject":
            let n = safeName(a1)
            if !n.isEmpty, Projects.ensure(n) { postProjects(); post("project\n" + n) }
        case "ensureproject": if Projects.ensure(a1) { postProjects() }
        case "forget": forget(a1)
        case "stop": if let r = runners[a1], r.busy { r.stopping = true; Shell.killTree(r.proc) }
        case "drop": dropRun(a1)
        case "alwaysallow": addAutoAllow(a1)                              // « Toujours » sur une carte de fin de tour
        case "permreply":                                                  // permreply\n<clé>\n<id>\n<yes|all|always|no>\n<message ou outils>
            if parts.count >= 4 { permReply(key: a1, id: parts[2], how: parts[3], msg: parts.count > 4 ? parts[4] : "") }
        case "calm": for r in runners.values where (a1.isEmpty || r.key == a1) && !r.busy { r.asking = false; r.hand = false }; updateCat(false, 0)
        case "send":
            let sp = m.split(separator: "\n", maxSplits: 7, omittingEmptySubsequences: false).map(String.init)
            if sp.count == 8 { send(key: sp[1], text: sp[7], autonomous: sp[2] == "1", sid: sp[3], allowed: sp[4], flash: sp[5] == "1", project: sp[6]) }
        case "progress":
            if parts.count > 3, let r = runners[a1], let d = Int(parts[2]), let t = Int(parts[3]) { r.done = d; r.total = t; updateCat(false, 0) }
        case "notify":
            let sp = m.split(separator: "\n", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
            if sp.count == 5 && (!window.isVisible || sp[2] != "1") { app?.notify(kind: sp[1], title: sp[4], key: sp[3]) }
        default: break
        }
    }

    func pickFolder() {
        let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false; p.directoryURL = URL(fileURLWithPath: workDir)
        p.message = "Dossier de travail de " + Prefs.name
        if p.runModal() == .OK, let u = p.url { workDir = u.path; Prefs.set("WorkDir", workDir); post("dir\n" + workDir) }
    }
    func pasteImage(_ data: String) {
        var b64 = data
        if let c = b64.firstIndex(of: ","), b64.distance(from: b64.startIndex, to: c) < 100 { b64 = String(b64[b64.index(after: c)...]) }
        guard let d = Data(base64Encoded: b64), let img = NSImage(data: d), let tiff = img.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { post("saveerr\nimage illisible"); return }
        let f = DateFormatter(); f.dateFormat = "yyyyMMdd-HHmmss-SSS"
        let path = (Paths.screenDir as NSString).appendingPathComponent("image-collee-" + f.string(from: Date()) + ".png")
        do { try png.write(to: URL(fileURLWithPath: path)); post("pasted\n" + path) } catch { post("saveerr\nimage illisible") }
    }
    func saveTxt(_ name: String, _ text: String) throws -> String {
        var n = name.replacingOccurrences(of: "[\\x00-\\x1f<>:\"/\\\\|?*]+", with: " ", options: .regularExpression)
        n = n.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression).trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        if n.count > 90 { n = String(n.prefix(90)) }
        if n.isEmpty { n = "Réponse de " + Prefs.name }
        var f = (Paths.downloads as NSString).appendingPathComponent(n + ".txt"), i = 2
        while FileManager.default.fileExists(atPath: f) { f = (Paths.downloads as NSString).appendingPathComponent("\(n) (\(i)).txt"); i += 1 }
        try text.write(toFile: f, atomically: true, encoding: .utf8)
        return f
    }
    func forget(_ sid: String) {
        guard isSid(sid) else { return }
        let root = (Paths.home as NSString).appendingPathComponent(".claude/projects")
        for d in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? [] {
            try? FileManager.default.removeItem(atPath: ((root as NSString).appendingPathComponent(d) as NSString).appendingPathComponent(sid + ".jsonl"))
        }
    }

    // ---- titre de conversation résumé par Haiku, en arrière-plan ----
    func makeTitle(_ id: String, _ excerpt: String) {
        guard let claude = Shell.which("claude") else { return }
        DispatchQueue.global().async {
            let p = Process(); p.executableURL = URL(fileURLWithPath: claude)
            p.arguments = ["-p", "--model", "haiku", "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--tools", "",
                "--system-prompt", "Tu donnes un titre a une conversation entre un utilisateur et son assistant. Reponds UNIQUEMENT par le titre, en francais : 2 a 5 mots, 40 caracteres maximum, qui nomment le sujet concret (objet, fichier, nom propre, cours, projet) plutot que l action. Commence par le mot le plus distinctif. Interdits : mots vagues (Aide, Question, Demande, Discussion, Conversation, Probleme, Assistance), guillemets, point final, emoji. Si d autres titres sont donnes, le tien doit s en distinguer clairement.",
                "--output-format", "text"]
            var env = Shell.env; env["CAVEMAN_DEFAULT_MODE"] = "off"; p.environment = env
            p.currentDirectoryURL = URL(fileURLWithPath: NSTemporaryDirectory())
            let i = Pipe(), o = Pipe(); p.standardInput = i; p.standardOutput = o; p.standardError = Pipe()
            do { try p.run() } catch { return }
            i.fileHandleForWriting.write(excerpt.data(using: .utf8)!); try? i.fileHandleForWriting.close()
            let t = String(decoding: o.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            p.waitUntilExit()
            if t.count > 1 && p.terminationStatus == 0 { self.post("title\n" + id + "\n" + t) }
        }
    }

    // ---- tâches en parallèle ----
    var anyBusy: Bool { runners.values.contains { $0.busy } }
    func idleTick() {
        for r in Array(runners.values) {
            if !r.busy && r.proc != nil && Date().timeIntervalSince(r.used) > 600 { retire(r) }
            if (r.asking || r.hand) && !r.busy && Date().timeIntervalSince(r.flagAt) > 1800 { r.asking = false; r.hand = false; updateCat(false, 0) }
        }
    }
    func retire(_ r: Runner) {
        guard let p = r.proc else { return }
        r.proc = nil; r.stopping = false
        try? r.stdin?.close(); r.stdin = nil
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) { Shell.killTree(p) }
    }
    func dropRun(_ key: String) {
        guard let r = runners[key] else { return }
        r.asking = false; r.hand = false
        if r.busy { r.forgotten = true; r.stopping = true; Shell.killTree(r.proc) } else { retire(r); runners[key] = nil }
        updateCat(false, 0)
    }
    func killAll() { runners.values.forEach { Shell.killTree($0.proc) } }
    func postDone(_ key: String, _ code: String, _ err: String) { post("rdone\n" + key + "\n" + code + "\n" + err) }

    // le chat montre l'état le plus important : besoin de toi > question > travail > fini
    func updateCat(_ finished: Bool, _ code: Int32) {
        var hand: Runner?, ask = false, busy = false, done = 0, total = 0
        for r in runners.values {
            if r.hand && hand == nil { hand = r }; if r.asking { ask = true }
            if r.busy { busy = true; if r.total > 0 { done = r.done; total = r.total } }
        }
        let s = hand != nil ? "hand" : ask ? "question" : busy ? "thinking" : finished ? (!window.isVisible ? "ready" : code == 0 ? "happy" : "idle") : "idle"
        onCat?(s, busy ? done : 0, busy ? total : 0)
    }

    static let heavy = "(fai[st]|cr[ée]e|modifi|corrig|[ée]cri[st]|r[ée]dig|g[ée]n[èe]r|produi|ouvr|rang|d[ée]plac|supprim|rempli|install|code|script|fichier|dossier|document|excel|word|powerpoint|keynote|pages|numbers|pdf|projet|analys|r[ée]sum|tradui|calcul|regard|[ée]cran|cliqu|cherch|trouv|mail|planifi|organis|compar|d[ée]taill|plan\\b|rapport|pr[ée]sentation|tableau|\\\\|/|\\.[a-z]{2,4}\\b|```)"
    func simple(_ t: String) -> Bool {
        let s = t.trimmingCharacters(in: .whitespacesAndNewlines)
        return !s.isEmpty && s.count <= 160 && !s.contains("\n") && s.range(of: Discussion.heavy, options: [.regularExpression, .caseInsensitive]) == nil
    }
    func pickModel(_ text: String, _ sid: String, _ r: Runner?) -> String {
        let m = Prefs.str("Model", "")
        if matches(m, "^[a-z0-9.-]+$") { return m }
        if !Prefs.bool("AutoModel", true) { return "" }
        return simple(text) && (!isSid(sid) || (r?.lite ?? false)) ? "haiku" : ""
    }

    func send(key: String, text: String, autonomous: Bool, sid: String, allowed: String, flash: Bool, project: String) {
        let project = safeName(project)
        if let r = runners[key], r.busy { postDone(key, "-1", "Claude est encore en train de répondre dans cette conversation : attends la fin ou clique sur Arrêter."); return }
        if runners.values.filter({ $0.busy }).count >= maxBusy { postDone(key, "-1", "\(maxBusy) tâches tournent déjà en parallèle : attends qu'une se termine, ou arrête-en une."); return }
        guard let claude = Shell.which("claude") else {
            postDone(key, "-1", "Claude Code est introuvable sur ce Mac. Installe-le (curl -fsSL https://claude.ai/install.sh | bash), puis relance " + Prefs.name + "."); return
        }
        var r = runners[key]
        let model = pickModel(text, sid, r)
        var msg = text
        if let a = app?.lastApp, !a.isEmpty { msg = "[Application active : " + a + " · pour la voir : " + screenCommand + "]\n" + msg }
        let df = DateFormatter(); df.locale = Locale(identifier: "fr_CH"); df.dateFormat = "EEEE d MMMM yyyy, HH:mm"
        msg = "[Maintenant : " + df.string(from: Date()) + "]\n" + msg
        let args = buildArgs(flash: flash, project: project, model: model)
        let sigStr = args.joined(separator: "\u{1}")
        if r == nil { r = Runner(key: key); runners[key] = r }
        let run = r!
        run.auto = autonomous
        for x in allowed.split(separator: ",").map(String.init) where matches(x, "^[A-Za-z][A-Za-z0-9_-]*$") { run.granted.insert(x) }
        run.pending.removeAll()
        if let p = run.proc, !p.isRunning || run.sig != sigStr { retire(run) }
        if run.proc == nil {
            var full = args
            if isSid(sid) { full += ["--resume", sid] }
            if !startRunner(run, exe: claude, args: full, dir: Projects.sessionDir(sid) ?? dirFor(flash, project)) { return }
            run.sig = sigStr; run.model = model
        }
        run.lite = model == "haiku"
        run.busy = true; run.asking = false; run.hand = false; run.stopping = false; run.done = 0; run.total = 0; run.used = Date(); run.err = ""
        let line = J.enc(["type": "user", "message": ["role": "user", "content": msg]]) + "\n"
        try? run.stdin?.write(contentsOf: line.data(using: .utf8)!)        // Claude déjà arrêté : son erreur arrive par « rdone »
        updateCat(false, 0)
    }

    func startRunner(_ r: Runner, exe: String, args: [String], dir: String) -> Bool {
        let p = Process(); p.executableURL = URL(fileURLWithPath: exe); p.arguments = args
        p.currentDirectoryURL = URL(fileURLWithPath: dir)
        var env = Shell.env; env["CAVEMAN_DEFAULT_MODE"] = Prefs.bool("Caveman", true) ? "caveman" : "off"; env["BEBENE_SESSION"] = "1"; p.environment = env
        let i = Pipe(), o = Pipe(), e = Pipe()
        p.standardInput = i; p.standardOutput = o; p.standardError = e
        let key = r.key
        let outR = LineReader(o.fileHandleForReading) { [weak self] d in
            DispatchQueue.main.async {
                guard let self = self, r.proc == nil || r.proc === p else { return }   // session remplacée : ses dernières lignes sont ignorées
                if d.contains("\"control_request\""), let o = J.dec(d) as? [String: Any], "\(o["type"] ?? "")" == "control_request" { self.onControl(r, o); return }
                let isResult = d.contains("\"type\":\"result\""), mark = d.contains("bebene-")
                var type = ""
                if isResult || mark, let o = J.dec(d) as? [String: Any] { type = "\(o["type"] ?? "")" }
                if type == "assistant" {
                    if d.contains("bebene-question") { r.asking = true; r.flagAt = Date() }
                    if d.contains("bebene-main") { r.hand = true; r.flagAt = Date(); r.handKind = matches(d, "bebene-main[^`]*(ecran|écran)") ? "ecran" : "souris" }
                    if d.contains("bebene-rappel") { Reminders.addFrom(Discussion.assistantText(d)) }
                }
                self.post("rline\n" + key + "\n" + d)
                if type == "result", r.proc === p, r.busy { r.busy = false; r.used = Date(); self.postDone(key, "0", ""); self.updateCat(true, 0) }
            }
        }
        let errR = LineReader(e.fileHandleForReading) { d in DispatchQueue.main.async { r.err += d + "\n" } }
        r.readers = [outR, errR]
        p.terminationHandler = { [weak self] pr in
            DispatchQueue.main.async {
                guard let self = self else { return }
                // processus remplacé (retire) : sa fin ne concerne pas le tour en cours (sinon « Erreur (code 2) »)
                guard r.proc === pr else { return }
                r.proc = nil
                if r.busy {
                    let code = pr.terminationStatus
                    r.busy = false
                    self.postDone(key, r.stopping ? "1" : (code == 0 ? "2" : String(code)), r.stopping ? "" : r.err)
                    r.stopping = false
                    if r.forgotten { self.runners[key] = nil; self.updateCat(false, 0) } else { self.updateCat(true, code) }
                }
            }
        }
        do { try p.run() } catch { postDone(key, "-1", "Impossible de lancer Claude Code : \(error.localizedDescription)"); return false }
        r.proc = p; r.stdin = i.fileHandleForWriting
        return true
    }

    static func assistantText(_ json: String) -> String {
        guard let o = J.dec(json) as? [String: Any], let m = o["message"] as? [String: Any], let c = m["content"] as? [[String: Any]] else { return "" }
        return c.compactMap { $0["text"] as? String }.joined(separator: "\n")
    }

    // voir l'écran : capture sans bruit (autorisation « Enregistrement de l'écran » demandée par macOS la première fois)
    var screenCommand: String { "screencapture -x " + (Paths.screenDir as NSString).appendingPathComponent("ecran.png") }

    // ---- demandes d'accord PENDANT le tour (--permission-prompt-tool stdio), comme sur Windows ----
    func grantName(_ t: String) -> String { t.hasPrefix("mcp__claude-in-chrome__") ? "mcp__claude-in-chrome" : t }
    func autoAllowed(_ n: String) -> Bool { Prefs.str("AutoAllow", "").split(separator: ",").map(String.init).contains(n) }
    func addAutoAllow(_ csv: String) {
        var l = Prefs.str("AutoAllow", "").split(separator: ",").map(String.init)
        for x in csv.split(separator: ",").map(String.init) where matches(x, "^[A-Za-z][A-Za-z0-9_-]*$") && !l.contains(x) { l.append(x) }
        Prefs.set("AutoAllow", l.joined(separator: ","))
    }
    func answer(_ r: Runner, _ id: String, allow: Bool, input: Any?, why: String) {
        let resp: [String: Any] = allow ? ["behavior": "allow", "updatedInput": input ?? [String: Any]()] : ["behavior": "deny", "message": why]
        let line = J.enc(["type": "control_response", "response": ["subtype": "success", "request_id": id, "response": resp] as [String: Any]]) + "\n"
        try? r.stdin?.write(contentsOf: line.data(using: .utf8)!)
    }
    func onControl(_ r: Runner, _ o: [String: Any]) {
        let id = "\(o["request_id"] ?? "")"
        guard let req = o["request"] as? [String: Any], "\(req["subtype"] ?? "")" == "can_use_tool" else {
            let line = J.enc(["type": "control_response", "response": ["subtype": "error", "request_id": id, "error": "non géré par Bébène"] as [String: Any]]) + "\n"
            try? r.stdin?.write(contentsOf: line.data(using: .utf8)!); return
        }
        let tool = "\(req["tool_name"] ?? "")", input = req["input"] ?? [String: Any](), n = grantName(tool)
        if r.auto || r.granted.contains(n) || autoAllowed(n) { answer(r, id, allow: true, input: input, why: ""); return }
        r.pending[id] = (tool, input)
        post("perm\n" + r.key + "\n" + id + "\n" + J.enc(["tool_name": tool, "tool_input": input, "description": "\(req["description"] ?? "")"]))
        r.asking = true; r.flagAt = Date(); updateCat(false, 0)
    }
    func permReply(key: String, id: String, how: String, msg: String) {
        guard let r = runners[key], r.proc != nil, let it = r.pending.removeValue(forKey: id) else { return }
        if how == "all" { r.auto = true }
        else if how == "yes" { r.granted.insert(grantName(it.tool)) }
        else if how == "always" { r.granted.insert(grantName(it.tool)); addAutoAllow(msg + "," + grantName(it.tool)) }
        if how == "no" {
            answer(r, id, allow: false, input: nil, why: msg.isEmpty ? "L'utilisateur a refusé cette action. Ne la relance pas ; continue sans elle ou demande-lui comment faire." : "L'utilisateur a refusé cette action et t'écrit : " + msg)
        } else { answer(r, id, allow: true, input: it.input, why: "") }
        for (k, v) in r.pending where r.auto || r.granted.contains(grantName(v.tool)) || autoAllowed(grantName(v.tool)) {   // demandes en parallèle couvertes
            r.pending.removeValue(forKey: k); answer(r, k, allow: true, input: v.input, why: ""); post("permdone\n" + key + "\n" + k)
        }
        if r.pending.isEmpty { r.asking = false }
        updateCat(false, 0)
    }

    func buildArgs(flash: Bool, project: String, model: String) -> [String] {
        var a = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"]
        if Prefs.bool("Stream", true) { a.append("--include-partial-messages") }
        // accords (conversation, « tout autoriser », réglage AutoAllow) : appliqués dans onControl, hors des arguments
        a += ["--permission-mode", "acceptEdits", "--permission-prompt-tool", "stdio"]
        a += ["--allowedTools", "Bash(screencapture -x *)", "--add-dir", Paths.screenDir]   // regarder l'écran : lecture seule, sans carte d'accord
        let chrome = Prefs.bool("Chrome", true)
        if chrome { a.append("--chrome") }
        if matches(model, "^[a-z0-9.-]+$") { a += ["--model", model] }
        a += ["--append-system-prompt", Persona.text(flash: flash, project: project, chrome: chrome).replacingOccurrences(of: "\n", with: " ")]
        a += ["--settings", Guard.settingsFile]
        return a
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool { hide(); return false }
}

extension Prefs { static func object(_ k: String) -> Any? { d.object(forKey: k) } }
