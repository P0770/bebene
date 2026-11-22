import Foundation

// ---------- Environnement : une app lancée depuis le Finder n'a pas le PATH du terminal ----------
// On lit une fois celui du shell de connexion (zsh -l), pour trouver `claude`, node, git, rtk…
enum Shell {
    static var path: String = {
        let base = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        let extra = [".local/bin", ".npm-global/bin", ".claude/local", ".bun/bin"].map { (Paths.home as NSString).appendingPathComponent($0) }.joined(separator: ":")
        let fromShell = run("/bin/zsh", ["-lc", "printf %s \"$PATH\""], timeout: 8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return [fromShell, extra, base].filter { !$0.isEmpty }.joined(separator: ":")
    }()
    static var env: [String: String] {
        var e = ProcessInfo.processInfo.environment
        e["PATH"] = path
        if e["LANG"] == nil { e["LANG"] = "fr_CH.UTF-8" }
        return e
    }
    static func which(_ tool: String) -> String? {
        for d in path.split(separator: ":") {
            let p = (String(d) as NSString).appendingPathComponent(tool)
            if FileManager.default.isExecutableFile(atPath: p) { return p }
        }
        return nil
    }
    // commande courte et synchrone (vérifications) ; nil si échec ou délai dépassé
    @discardableResult
    static func run(_ exe: String, _ args: [String], timeout: Double = 20, dir: String? = nil, env: [String: String]? = nil) -> String? {
        let p = Process(); p.executableURL = URL(fileURLWithPath: exe); p.arguments = args
        if let d = dir { p.currentDirectoryURL = URL(fileURLWithPath: d) }
        if let e = env { p.environment = e }
        let out = Pipe(); p.standardOutput = out; p.standardError = Pipe(); p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let end = Date().addingTimeInterval(timeout)
        while p.isRunning && Date() < end { usleep(50_000) }
        if p.isRunning { p.terminate(); return nil }
        let s = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        return p.terminationStatus == 0 ? s : nil
    }
    static func killTree(_ p: Process?) {
        guard let p = p, p.isRunning else { return }
        let pid = p.processIdentifier
        _ = run("/usr/bin/pkill", ["-TERM", "-P", String(pid)], timeout: 3)    // commandes lancées par Claude
        p.terminate()
        DispatchQueue.global().asyncAfter(deadline: .now() + 3) { if p.isRunning { kill(pid, SIGKILL) } }
    }
}

// ---------- Gardien : Bébène ne peut pas modifier son propre programme (code, installation, réglages, données) ----------
enum Guard {
    static let script = """
    #!/bin/bash
    # Gardien de Bébène : bloque toute modification de son propre programme depuis ses conversations
    s=$(cat | tr '[:upper:]' '[:lower:]')
    for p in 'claudehub/projets/claude-companion' 'claude-companion' 'application support/bebene' 'bebene.app' 'ch.bebene.app'; do
      case "$s" in *"$p"*)
        echo "Bloqué : Bébène ne peut pas modifier son propre programme, ses réglages ou ses données. Pour faire évoluer Bébène, utilise Claude Code dans le terminal." >&2
        exit 2;;
      esac
    done
    exit 0
    """
    static var settingsFile: String = {
        let sh = Paths.inData("garde.sh"), js = Paths.inData("garde-settings.json")
        try? script.write(toFile: sh, atomically: true, encoding: .utf8)
        chmod(sh, 0o755)
        let hook: [String: Any] = ["type": "command", "timeout": 15, "command": "/bin/bash '" + sh + "'"]
        let settings: [String: Any] = ["hooks": ["PreToolUse": [["matcher": "Edit|Write|MultiEdit|NotebookEdit|Bash", "hooks": [hook]]]]]
        try? J.enc(settings).write(toFile: js, atomically: true, encoding: .utf8)
        return js
    }()
}

// ---------- Une session Claude gardée ouverte par conversation (--input-format stream-json) ----------
final class Runner {
    let key: String
    var sig = "", model = "", handKind = ""
    var busy = false, asking = false, hand = false, stopping = false, lite = false, forgotten = false
    var done = 0, total = 0
    var proc: Process?
    var stdin: FileHandle?
    var err = ""
    var readers: [LineReader] = []          // gardés en vie tant que le processus tourne
    var used = Date(), flagAt = Date.distantPast
    init(key: String) { self.key = key }
}

// lecteur ligne par ligne d'un tuyau (les lignes JSON de Claude peuvent être très longues)
final class LineReader {
    private var buf = Data()
    let onLine: (String) -> Void
    init(_ h: FileHandle, onLine: @escaping (String) -> Void) {
        self.onLine = onLine
        h.readabilityHandler = { [weak self] fh in
            let d = fh.availableData
            guard let self = self else { return }
            if d.isEmpty { fh.readabilityHandler = nil; if !self.buf.isEmpty { let s = String(decoding: self.buf, as: UTF8.self); self.buf.removeAll(); onLine(s) }; return }
            self.buf.append(d)
            while let i = self.buf.firstIndex(of: 0x0A) {
                let line = self.buf.subdata(in: self.buf.startIndex..<i)
                self.buf.removeSubrange(self.buf.startIndex...i)
                onLine(String(decoding: line, as: UTF8.self))
            }
        }
    }
}
