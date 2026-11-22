import Foundation

// ---------- Chemins, réglages, journal ----------
enum Paths {
    static let home = FileManager.default.homeDirectoryForCurrentUser.path
    // données de Bébène : ~/Library/Application Support/Bebene (équivalent de %LOCALAPPDATA%\ClaudeCompanion)
    static let data: String = {
        let d = (home as NSString).appendingPathComponent("Library/Application Support/Bebene")
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }()
    static func inData(_ name: String) -> String { (data as NSString).appendingPathComponent(name) }
    // ressources de l'app (ui.html, settings.html, pont.js, fonctions.json…)
    static var res: String { Bundle.main.resourcePath ?? FileManager.default.currentDirectoryPath }

    // ClaudeHub : réglage « Hub », sinon OneDrive, iCloud Drive, puis ~/ClaudeHub
    static var hub: String {
        let custom = Prefs.str("Hub", "")
        if !custom.isEmpty { return custom }
        let fm = FileManager.default
        var candidates: [String] = []
        if let cs = try? fm.contentsOfDirectory(atPath: (home as NSString).appendingPathComponent("Library/CloudStorage")) {
            for c in cs.sorted() where c.hasPrefix("OneDrive") && !c.contains("HES") { candidates.append("Library/CloudStorage/\(c)/ClaudeHub") }
        }
        candidates += ["OneDrive/ClaudeHub", "Library/Mobile Documents/com~apple~CloudDocs/ClaudeHub", "ClaudeHub"]
        for c in candidates {
            let p = (home as NSString).appendingPathComponent(c)
            if fm.fileExists(atPath: (p as NSString).appendingPathComponent("memoire")) { return p }
        }
        return (home as NSString).appendingPathComponent("ClaudeHub")
    }
    static var projects: String { (hub as NSString).appendingPathComponent("projets") }
    static var downloads: String { (home as NSString).appendingPathComponent("Downloads") }
    static let screenDir: String = {
        let d = (NSTemporaryDirectory() as NSString).appendingPathComponent("BebeneEcran")
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }()
}

enum Log {
    static func write(_ what: String, _ detail: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) [\(what)] \(detail)\n"
        let f = Paths.inData("bebene.log")
        if let h = FileHandle(forWritingAtPath: f) { h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); h.closeFile() }
        else { try? line.write(toFile: f, atomically: true, encoding: .utf8) }
    }
}

// Réglages : UserDefaults (équivalent du registre HKCU\Software\ClaudeCompanion), mêmes noms de clés que sur Windows
enum Prefs {
    static let d = UserDefaults.standard
    static func str(_ k: String, _ def: String) -> String { d.string(forKey: k) ?? def }
    static func bool(_ k: String, _ def: Bool) -> Bool { d.object(forKey: k) == nil ? def : d.bool(forKey: k) }
    static func int(_ k: String, _ def: Int) -> Int { d.object(forKey: k) == nil ? def : d.integer(forKey: k) }
    static func set(_ k: String, _ v: Any) { d.set(v, forKey: k) }
    static var name: String { let n = str("Name", "").trimmingCharacters(in: .whitespaces); return n.isEmpty ? "Bébène" : n }
}

// ---------- JSON ----------
enum J {
    static func str(_ s: String) -> String {
        guard let d = try? JSONSerialization.data(withJSONObject: s, options: [.fragmentsAllowed]) else { return "\"\"" }
        return String(data: d, encoding: .utf8) ?? "\"\""
    }
    static func enc(_ o: Any) -> String {
        guard JSONSerialization.isValidJSONObject(o), let d = try? JSONSerialization.data(withJSONObject: o) else { return "{}" }
        return String(data: d, encoding: .utf8) ?? "{}"
    }
    static func enc(_ o: [String: Any]) -> String { enc(o as Any) }       // donne son type aux littéraux { clé: valeurs mêlées }
    static func dec(_ s: String) -> Any? { s.data(using: .utf8).flatMap { try? JSONSerialization.jsonObject(with: $0, options: [.fragmentsAllowed]) } }
}

func matches(_ s: String, _ pattern: String) -> Bool { s.range(of: pattern, options: .regularExpression) != nil }
func isSid(_ s: String) -> Bool { matches(s, "^[0-9a-fA-F-]{36}$") }

// nom de projet sûr : minuscules sans accents, a-z 0-9 . _ -
func safeName(_ s: String) -> String {
    let folded = s.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: Locale(identifier: "fr")).lowercased()
    let r = folded.replacingOccurrences(of: "[^a-z0-9._-]+", with: "-", options: .regularExpression)
    return r.trimmingCharacters(in: CharacterSet(charactersIn: "-."))
}

// ---------- Conversations : un fichier JSON par conversation (même format que Windows) ----------
enum ConvStore {
    static let dir: String = {
        let d = Paths.inData("conversations")
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }()
    static func ok(_ id: String) -> Bool { matches(id, "^[A-Za-z0-9_-]{1,40}$") }
    static func save(_ id: String, _ json: String) {
        guard ok(id), json.hasPrefix("{") else { return }
        do { try json.write(toFile: (dir as NSString).appendingPathComponent(id + ".json"), atomically: true, encoding: .utf8) }
        catch { Log.write("Conversations", "\(error)") }
    }
    static func delete(_ id: String) { if ok(id) { try? FileManager.default.removeItem(atPath: (dir as NSString).appendingPathComponent(id + ".json")) } }
    static func allJson() -> String {
        var parts: [String] = []
        for f in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] where f.hasSuffix(".json") {
            guard let s = try? String(contentsOfFile: (dir as NSString).appendingPathComponent(f), encoding: .utf8) else { continue }
            let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
            if t.hasPrefix("{") && t.hasSuffix("}") { parts.append(t) }
        }
        return "[" + parts.joined(separator: ",") + "]"
    }
}

// ---------- Projets ClaudeHub (dossiers de projets/, archives dans projets/.archives) ----------
enum Projects {
    static var archFile: String { (Paths.projects as NSString).appendingPathComponent(".archives") }
    static func list() -> [String] {
        let fm = FileManager.default; var r: [String] = []
        for n in (try? fm.contentsOfDirectory(atPath: Paths.projects)) ?? [] {
            var isDir: ObjCBool = false
            if !n.hasPrefix("."), n != "claude-companion", fm.fileExists(atPath: (Paths.projects as NSString).appendingPathComponent(n), isDirectory: &isDir), isDir.boolValue { r.append(n) }
        }
        return r.sorted()
    }
    static func archived() -> Set<String> {
        guard let s = try? String(contentsOfFile: archFile, encoding: .utf8) else { return [] }
        return Set(s.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty })
    }
    static func setArchived(_ name: String, _ on: Bool) {
        let n = safeName(name); if n.isEmpty { return }
        var a = archived()
        if on { if a.insert(n).inserted == false { return } } else { if a.remove(n) == nil { return } }
        let l = a.sorted()
        try? (l.joined(separator: "\n") + (l.isEmpty ? "" : "\n")).write(toFile: archFile, atomically: true, encoding: .utf8)
    }
    static func json() -> String { let all = list(), a = archived(); return J.enc(["all": all, "arch": all.filter { a.contains($0) }]) }
    static func ensure(_ name: String) -> Bool {
        let n = safeName(name); if n.isEmpty || n == "claude-companion" { return false }
        do { try FileManager.default.createDirectory(atPath: (Paths.projects as NSString).appendingPathComponent(n), withIntermediateDirectories: true) }
        catch { Log.write("Projet", "\(error)"); return false }
        setArchived(n, false); return true
    }

    // Claude Code range une session sous le dossier où elle a commencé : on la reprend toujours depuis ce dossier (premier "cwd")
    static var sessionDirs: [String: String] = [:]
    static func sessionDir(_ sid: String) -> String? {
        guard isSid(sid) else { return nil }
        if let k = sessionDirs[sid] { return k }
        let root = (Paths.home as NSString).appendingPathComponent(".claude/projects")
        for d in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? [] {
            let f = ((root as NSString).appendingPathComponent(d) as NSString).appendingPathComponent(sid + ".jsonl")
            guard let h = FileHandle(forReadingAtPath: f) else { continue }
            let head = String(decoding: h.readData(ofLength: 200_000), as: UTF8.self); h.closeFile()
            if let r = head.range(of: "\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", options: .regularExpression) {
                let raw = String(head[r]).dropFirst(7).dropLast(1)
                let cwd = (J.dec("\"" + raw + "\"") as? String) ?? String(raw)
                if FileManager.default.fileExists(atPath: cwd) { sessionDirs[sid] = cwd; return cwd }
            }
            return nil
        }
        return nil
    }
}

// ---------- Rappels demandés à Bébène (```bebene-rappel {"quand":"AAAA-MM-JJTHH:MM","quoi":"…"}```) ----------
enum Reminders {
    struct Item: Codable { var Quand: String; var Quoi: String; var Fait: Bool; var id: String { Quand + "|" + Quoi } }
    static let file = Paths.inData("rappels-perso.json")
    static var items: [Item] = {
        guard let d = FileManager.default.contents(atPath: file) else { return [] }
        return (try? JSONDecoder().decode([Item].self, from: d)) ?? []
    }()
    static let fmt: DateFormatter = { let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd'T'HH:mm"; return f }()
    static func when(_ i: Item) -> Date? { fmt.date(from: String(i.Quand.prefix(16)).replacingOccurrences(of: " ", with: "T")) }
    static func save() {
        let old = Date().addingTimeInterval(-7 * 86400)
        items.removeAll { $0.Fait && (when($0) ?? .distantPast) < old }
        if let d = try? JSONEncoder().encode(items) { FileManager.default.createFile(atPath: file, contents: d) }
    }
    static func addFrom(_ text: String) {
        guard let re = try? NSRegularExpression(pattern: "```bebene-rappel\\s*(\\{[\\s\\S]*?\\})\\s*```") else { return }
        let ns = text as NSString
        for m in re.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            guard let o = J.dec(ns.substring(with: m.range(at: 1))) as? [String: Any] else { continue }
            var it = Item(Quand: "\(o["quand"] ?? "")".trimmingCharacters(in: .whitespaces), Quoi: "\(o["quoi"] ?? "")".trimmingCharacters(in: .whitespaces), Fait: false)
            if it.Quoi.isEmpty || when(it) == nil || items.contains(where: { $0.id == it.id }) { continue }
            if it.Quoi.count > 200 { it.Quoi = String(it.Quoi.prefix(200)) }
            items.append(it); save()
        }
    }
    static func cancel(_ id: String) { let n = items.count; items.removeAll { $0.id == id }; if items.count != n { save() } }
    static func due() -> Item? {
        let now = Date()
        guard let i = items.indices.filter({ !items[$0].Fait && (when(items[$0]) ?? .distantFuture) <= now })
                .min(by: { (when(items[$0]) ?? now) < (when(items[$1]) ?? now) }) else { return nil }
        items[i].Fait = true; save(); return items[i]
    }
}
