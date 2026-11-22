import AppKit
import WebKit

// WKWebView qui reçoit les fichiers glissés depuis le Finder : WebKit ne donne pas leur chemin à la page,
// donc on les lit ici et on les envoie à la page comme sur Windows (« paths\n<chemin>\n<chemin> »).
final class BridgeWebView: WKWebView {
    var onFiles: (([String]) -> Void)?
    static func fileURLs(_ pb: NSPasteboard) -> [String] {
        let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        return urls.map { $0.path }
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if !BridgeWebView.fileURLs(sender.draggingPasteboard).isEmpty { return .copy }
        return super.draggingEntered(sender)
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        if !BridgeWebView.fileURLs(sender.draggingPasteboard).isEmpty { return .copy }
        return super.draggingUpdated(sender)
    }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let files = BridgeWebView.fileURLs(sender.draggingPasteboard)
        if !files.isEmpty { onFiles?(files); return true }
        return super.performDragOperation(sender)
    }
}

// Une page HTML de Bébène (discussion ou réglages) avec le pont de messages.
final class WebPage: NSObject, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate {
    let web: BridgeWebView
    var onMessage: ((String) -> Void)?
    private(set) var ready = false
    private var pending: [String] = []

    init(page: String, initScript: String? = nil) {
        let cfg = WKWebViewConfiguration()
        let uc = WKUserContentController()
        if let pont = try? String(contentsOfFile: (Paths.res as NSString).appendingPathComponent("pont.js"), encoding: .utf8) {
            uc.addUserScript(WKUserScript(source: pont, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        }
        if let s = initScript { uc.addUserScript(WKUserScript(source: s, injectionTime: .atDocumentStart, forMainFrameOnly: true)) }
        cfg.userContentController = uc
        cfg.preferences.setValue(true, forKey: "developerExtrasEnabled")      // clic droit › Inspecter (débogage)
        web = BridgeWebView(frame: .zero, configuration: cfg)
        super.init()
        uc.add(WeakHandler(self), name: "bebene")
        web.navigationDelegate = self
        web.uiDelegate = self
        web.setValue(false, forKey: "drawsBackground")                       // fond transparent : le flou natif de la fenêtre passe dessous
        let url = URL(fileURLWithPath: (Paths.res as NSString).appendingPathComponent(page))
        web.loadFileURL(url, allowingReadAccessTo: URL(fileURLWithPath: "/"))
    }

    // natif → page (file d'attente tant que la page n'a pas dit « ready »)
    func post(_ s: String) {
        if !Thread.isMainThread { DispatchQueue.main.async { self.post(s) }; return }
        if !ready { if pending.count >= 200 { pending.removeFirst() }; pending.append(s); return }
        web.evaluateJavaScript("window.__bebeneRecv(" + J.str(s) + ")", completionHandler: nil)
    }
    func markReady() { ready = true; let l = pending; pending.removeAll(); l.forEach(post) }

    func userContentController(_ uc: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let s = message.body as? String else { return }
        if s.hasPrefix("log\n") { Log.write("Page", String(s.dropFirst(4))); return }
        onMessage?(s)
    }
    // liens externes : navigateur par défaut ; la page elle-même ne quitte jamais son fichier local
    func webView(_ w: WKWebView, decidePolicyFor a: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if let u = a.request.url, u.scheme != "file", u.scheme != "about" { NSWorkspace.shared.open(u); decisionHandler(.cancel); return }
        decisionHandler(.allow)
    }
    func webView(_ w: WKWebView, createWebViewWith c: WKWebViewConfiguration, for a: WKNavigationAction, windowFeatures f: WKWindowFeatures) -> WKWebView? {
        if let u = a.request.url { NSWorkspace.shared.open(u) }
        return nil
    }
    // <input type=file> éventuels
    func webView(_ w: WKWebView, runOpenPanelWith p: WKOpenPanelParameters, initiatedByFrame f: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        let op = NSOpenPanel(); op.allowsMultipleSelection = p.allowsMultipleSelection; op.canChooseDirectories = p.allowsDirectories
        op.begin { r in completionHandler(r == .OK ? op.urls : nil) }
    }
    // la page de Windows n'utilise pas alert/confirm, mais au cas où : jamais de blocage
    func webView(_ w: WKWebView, runJavaScriptAlertPanelWithMessage m: String, initiatedByFrame f: WKFrameInfo, completionHandler: @escaping () -> Void) { completionHandler() }
    func webView(_ w: WKWebView, runJavaScriptConfirmPanelWithMessage m: String, initiatedByFrame f: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) { completionHandler(true) }
}

// évite le cycle de rétention WKUserContentController → handler
final class WeakHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ t: WKScriptMessageHandler) { target = t }
    func userContentController(_ uc: WKUserContentController, didReceive m: WKScriptMessage) { target?.userContentController(uc, didReceive: m) }
}

// Fenêtre Mac « verre » : flou natif sous la page, barre de titre fondue dans le contenu
func glassWindow(size: NSSize, title: String, web: WKWebView, resizable: Bool = true) -> NSWindow {
    var style: NSWindow.StyleMask = [.titled, .closable, .fullSizeContentView, .miniaturizable]
    if resizable { style.insert(.resizable) }
    let w = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: style, backing: .buffered, defer: false)
    w.title = title
    w.titlebarAppearsTransparent = true
    w.titleVisibility = .hidden
    w.isMovableByWindowBackground = false
    w.isReleasedWhenClosed = false
    w.minSize = NSSize(width: 380, height: 420)
    let fx = NSVisualEffectView()
    fx.material = .underWindowBackground; fx.blendingMode = .behindWindow; fx.state = .active
    web.translatesAutoresizingMaskIntoConstraints = false
    fx.addSubview(web)
    NSLayoutConstraint.activate([
        web.leadingAnchor.constraint(equalTo: fx.leadingAnchor), web.trailingAnchor.constraint(equalTo: fx.trailingAnchor),
        web.topAnchor.constraint(equalTo: fx.topAnchor), web.bottomAnchor.constraint(equalTo: fx.bottomAnchor)])
    w.contentView = fx
    return w
}
