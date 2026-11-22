import AppKit
import WebKit

// ---------- Le chat : fenêtre transparente sans bord, dessin dans chat.html (Canvas, port de Companion.cs) ----------
// La page calcule tout (comportement, dessin) ; ici on lui donne 30 fois par seconde la souris et l'écran,
// et on applique ce qu'elle renvoie : position de la fenêtre, zone cliquable, clic.
final class CatWindow: NSObject {
    let page: WebPage
    let window: NSPanel
    weak var app: AppDelegate?
    var size = NSSize(width: 192, height: 156)
    var timer: Timer?
    var lastState = ""

    init(app: AppDelegate) {
        self.app = app
        page = WebPage(page: "chat.html")
        window = NSPanel(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.isReleasedWhenClosed = false
        window.hidesOnDeactivate = false
        window.becomesKeyOnlyIfNeeded = true
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        applyLevel()
        page.web.frame = NSRect(origin: .zero, size: size)
        page.web.autoresizingMask = [.width, .height]
        window.contentView = page.web
        page.onMessage = { [weak self] m in self?.onMessage(m) }
        page.web.onFiles = { [weak self] files in
            guard let self = self, let app = self.app else { return }
            app.panel.show(near: self.window.frame)
            app.panel.post("paths\n" + files.joined(separator: "\n"))
            self.page.web.evaluateJavaScript("__drop()", completionHandler: nil)
        }
    }

    func applyLevel() { window.level = Prefs.bool("TopMost", true) ? .floating : .normal }
    func applyScale() { sendSettings() }
    func sendSettings() {
        page.web.evaluateJavaScript("__settings(" + J.enc(["scale": Prefs.int("Scale", 100), "activity": Prefs.int("Activity", 50)]) + ")", completionHandler: nil)
    }
    func show() {
        window.orderFrontRegardless()
        if timer == nil {
            timer = Timer(timeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.tick() }
            RunLoop.main.add(timer!, forMode: .common)                       // continue pendant un menu ou un glisser
        }
    }
    func hide() { window.orderOut(nil); timer?.invalidate(); timer = nil }
    func clampToScreen() {}                                                  // la page recale le chat à chaque tick
    func setState(_ s: String, done: Int, total: Int) {
        let kind = app?.panel.runners.values.first(where: { $0.hand })?.handKind ?? ""
        page.web.evaluateJavaScript("__state(" + J.str(s) + ",\(done),\(total)," + J.str(kind) + ")", completionHandler: nil)
    }
    func say(_ text: String) { setState("ready", done: 0, total: 0) }

    // coordonnées : la page travaille en points, origine en haut à gauche de l'écran principal (comme Windows)
    var topY: CGFloat { NSScreen.screens.first?.frame.maxY ?? 900 }
    func tl(_ r: NSRect) -> [CGFloat] { [r.minX, topY - r.maxY, r.maxX, topY - r.minY] }

    func tick() {
        let m = NSEvent.mouseLocation
        let center = NSPoint(x: window.frame.midX, y: window.frame.midY)
        let scr = NSScreen.screens.first(where: { NSMouseInRect(center, $0.frame, false) }) ?? NSScreen.main ?? NSScreen.screens[0]
        var o: [String: Any] = ["cx": m.x, "cy": topY - m.y, "wa": tl(scr.visibleFrame), "btn": NSEvent.pressedMouseButtons & 1,
                                "pv": app?.panel.visible == true ? 1 : 0, "x": Prefs.int("X", -1) >= 0 ? Prefs.int("X", 0) as Any : NSNull() as Any]
        if let p = app?.panel, p.visible { o["pb"] = tl(p.window.frame) }
        page.web.evaluateJavaScript("window.__tick && __tick(" + J.enc(o) + ")", completionHandler: nil)
    }

    func onMessage(_ m: String) {
        let p = m.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        switch p[0] {
        case "ready": page.markReady(); sendSettings()
        case "size":
            if p.count > 2, let w = Double(p[1]), let h = Double(p[2]) {
                size = NSSize(width: w, height: h)
                var f = window.frame; f.size = size; window.setFrame(f, display: true)
            }
        case "pos":
            if p.count > 2, let x = Double(p[1]), let y = Double(p[2]) {
                window.setFrameOrigin(NSPoint(x: x, y: topY - y - size.height))
            }
        case "hit": window.ignoresMouseEvents = !(p.count > 1 && p[1] == "1")
        case "x": if p.count > 1, let x = Int(p[1]) { Prefs.set("X", x) }
        case "click": app?.panel.toggle(near: window.frame)
        default: break
        }
    }
}
