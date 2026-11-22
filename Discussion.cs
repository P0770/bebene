using System;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// ---------- La fenêtre de discussion ----------
class ChatPanel : Form {
    public static bool NoActivate;   // tests : afficher sans prendre le clavier
    protected override bool ShowWithoutActivation { get { return NoActivate; } }
    readonly Companion owner;
    WebView2 web;                                                    // libérée après 10 min fermée (FreeWebIfIdle), recréée à l'ouverture
    public string WorkDir;

    public ChatPanel(Companion c) {
        owner = c;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = SavedSize(); MinimumSize = new Size(ChatW, ChatH);
        calTimer.Tick += (s, e) => RefreshCalendars();                 // rafraîchit les calendriers toutes les 30 min
        calTimer.Start();
        InitSide(); InitRunners();
        updTimer.Tick += (s, e) => CheckUpdate(); updTimer.Start();
        var firstUpd = new System.Windows.Forms.Timer { Interval = 15000 };
        firstUpd.Tick += (s, e) => { firstUpd.Stop(); firstUpd.Dispose(); CheckUpdate(); }; firstUpd.Start();
        VisibleChanged += (s, e) => { if (Visible && (DateTime.Now - lastUpd).TotalHours > 1) CheckUpdate(); };
        VisibleChanged += (s, e) => Util.WebMemory(web, Visible);
        VisibleChanged += (s, e) => { if (Visible) { ProbeQuota(false); if (owner.State == "ready") owner.SetState("happy"); } };    // quota (s'il date) à chaque ouverture
        quotaTimer.Tick += (s, e) => { if (Visible) ProbeQuota(false); }; quotaTimer.Start();
        VisibleChanged += (s, e) => { if (Visible) { if (web == null) MakeWeb(); DailyBrief(); } else hiddenAt = DateTime.Now; };
        Mailbox.LoadCache();                                         // derniers résultats tout de suite
        // analyse auto : 1 fois par jour au maximum (premier démarrage ou première ouverture du jour) ; sinon seulement via le bouton
        VisibleChanged += (s, e) => { if (Visible) DailyMailScan(); };
        var first = new System.Windows.Forms.Timer { Interval = 20000 };
        first.Tick += (s, e) => { first.Stop(); first.Dispose(); DailyMailScan(); };
        first.Start();
        RefreshCalendars();
        BackColor = Color.Black;                                     // noir = zone où Windows dessine le verre dépoli
        WorkDir = Settings.Str("WorkDir", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Directory.Exists(WorkDir)) WorkDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MakeWeb();
    }
    // la page reçoit l'historique avant ses propres scripts (window.__CONVS) : affichage immédiat, sans attendre un message
    void MakeWeb() {
        pageReady = false;
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(web);
        Util.InitWeb(web, "ui.html", OnMessage, "window.__CONVS=" + ConvStore.AllJson() + ";");
    }
    // discussion fermée depuis 10 min, rien en cours : on rend la mémoire de la page (~300 Mo). Tout est déjà enregistré (conversations en fichiers).
    DateTime hiddenAt = DateTime.Now;
    void FreeWebIfIdle() {
        if (web == null || Visible || (DateTime.Now - hiddenAt).TotalMinutes < 10 || compactProc != null) return;
        foreach (var r in runners.Values) if (r.Busy || r.Asking || r.Hand) return;
        var w = web; web = null; pageReady = false; lock (pending) pending.Clear();
        try { Controls.Remove(w); w.Dispose(); } catch (Exception ex) { Program.Log("WebView2", ex.Message); }
    }
    // point du jour : une fois par jour, à la première ouverture (réglage DailyBrief)
    void DailyBrief() {
        string day = DateTime.Today.ToString("yyyy-MM-dd");
        if (!Settings.Bool("DailyBrief", true) || Settings.Str("BriefDay", "") == day) return;
        Settings.Set("BriefDay", day);
        Post("brief\n" + Brief.Json());
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Util.DarkWindow(Handle, true); Util.Glass(Handle); }

    // ---- clic ailleurs = la discussion se ferme (sauf si elle est épinglée, ou si on clique sur Bébène / ses fenêtres) ----
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    bool OwnedByMe(IntPtr w) {                                              // fenêtre de la discussion elle-même : dialogue, liste déroulante du WebView, menu…
        for (int i = 0; i < 8 && w != IntPtr.Zero; i++) {
            if (w == Handle) return true;
            IntPtr o = GetWindow(w, 4);                                     // GW_OWNER
            w = o != IntPtr.Zero ? o : GetAncestor(w, 1);                   // GA_PARENT
        }
        return false;
    }
    DateTime settingsAsked = DateTime.MinValue;
    public bool Pinned = Settings.Bool("Pinned", false);
    protected override void OnDeactivate(EventArgs e) {
        base.OnDeactivate(e);
        if (Pinned || NoActivate) return;
        var t = new System.Windows.Forms.Timer { Interval = 120 };          // laisse Windows activer la nouvelle fenêtre
        t.Tick += (s, e2) => {
            t.Stop(); t.Dispose();
            if (!Visible || Pinned || ContainsFocus) return;
            var fg = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(fg, out pid);
            if (fg == IntPtr.Zero || OwnedByMe(fg)) return;                 // rien au premier plan (transition) ou fenêtre de la discussion : on reste
            if (fg == owner.SettingsHwnd && (DateTime.Now - settingsAsked).TotalSeconds < 3) return;   // ⚙ cliqué ici : ce n est pas « ailleurs »
            if (pid == Program.Pid && fg != owner.SettingsHwnd) return;    // le chat, un post-it, un dialogue de Bébène… (les réglages comptent comme « ailleurs »)
            Hide();
        };
        t.Start();
    }

    public void FocusInput() { if (web != null) web.Focus(); Post("focus"); }

    public static Action<string> PostHook;                                  // tests : messages interceptés au lieu d aller à la page
    public void Post(string s) {
        if (PostHook != null) { PostHook(s); return; }
        // la fenêtre peut ne jamais avoir été affichée : on passe par le contrôle WebView, qui a toujours son handle
        Control ui = IsHandleCreated ? (Control)this : (web != null && web.IsHandleCreated ? web : null);
        if (ui == null || !pageReady) { Queue(s); return; }                  // page pas prête (démarrage, page libérée) : envoyé à son « ready »
        if (ui.InvokeRequired) { try { ui.BeginInvoke((Action)(() => Post(s))); } catch { } return; }
        if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(s); else Queue(s);
    }
    volatile bool pageReady; readonly List<string> pending = new List<string>();
    void Queue(string s) { lock (pending) { if (pending.Count >= 200) pending.RemoveAt(0); pending.Add(s); } }
    void Flush() {
        List<string> l; lock (pending) { l = new List<string>(pending); pending.Clear(); }
        foreach (var s in l) Post(s);
    }

    // ---- projets ClaudeHub et conversations flash ----
    public bool Flash; public string Project = "";
    static string Hub { get { return Setup.Hub; } }   // OneDrive personnel si possible, sinon dossier utilisateur
    static string ProjectsDir { get { return Path.Combine(Hub, "projets"); } }
    static string SafeName(string s) { return Regex.Replace(Calendars.Norm(s ?? "").Trim(), @"[^a-z0-9._-]+", "-").Trim('-', '.'); }
    static List<string> ListProjects() {
        var r = new List<string>();
        try { foreach (var d in Directory.GetDirectories(ProjectsDir)) { string n = Path.GetFileName(d); if (n != "claude-companion") r.Add(n); } } catch { }   // Bébène lui-même se gère dans le terminal
        r.Sort(); return r;
    }
    // dossier où Claude travaille : le projet choisi, sinon le dossier des réglages
    // Claude Code range une session sous le dossier ou elle a commence (~/.claude/projects/<dossier>/<id>.jsonl) :
    // la reprendre ailleurs echoue. Le projet affiche dans l historique peut changer (rattachement, deplacement),
    // donc on reprend toujours depuis le dossier d origine, lu dans la session (premier "cwd").
    static readonly Dictionary<string, string> sessionDirs = new Dictionary<string, string>();
    public static string SessionDir(string sid) {
        if (!Regex.IsMatch(sid ?? "", "^[0-9a-fA-F-]{36}$")) return null;
        string known; lock (sessionDirs) if (sessionDirs.TryGetValue(sid, out known)) return known;
        string found = null;
        try {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            foreach (var d in Directory.GetDirectories(root)) {
                string fpath = Path.Combine(d, sid + ".jsonl");
                if (!File.Exists(fpath)) continue;
                using (var fs = new FileStream(fpath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8)) {
                    string line; int n = 0;
                    while ((line = r.ReadLine()) != null && n++ < 200) {
                        var m = Regex.Match(line, "\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (m.Success) { found = Regex.Unescape(m.Groups[1].Value); break; }
                    }
                }
                break;
            }
        } catch (Exception ex) { Program.Log("Session", ex.Message); }
        if (found != null && !Directory.Exists(found)) found = null;
        if (found != null) lock (sessionDirs) sessionDirs[sid] = found;
        return found;
    }
    string RunDir(string sid, bool flash, string project) { return SessionDir(sid) ?? DirFor(flash, project); }
    string CurrentDir() { return DirFor(Flash, Project); }
    string DirFor(bool flash, string project) {
        if (!flash && project.Length > 0) { string d = Path.Combine(ProjectsDir, project); if (Directory.Exists(d)) return d; }
        return WorkDir;
    }
    // efface la trace locale d'une conversation flash (transcription de Claude Code)
    public static void Forget(string sid) {
        if (!Regex.IsMatch(sid ?? "", "^[0-9a-fA-F-]{36}$")) return;
        try {
            foreach (var f in Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"), sid + ".jsonl", SearchOption.AllDirectories))
                File.Delete(f);
        } catch (Exception ex) { Program.Log("Flash", ex.ToString()); }
    }

    // ---- quota restant : mini-requête silencieuse (quelques tokens, ~1 s) qui renvoie les infos de limite officielles ----
    DateTime lastProbe = DateTime.MinValue, lastQuotaAt = DateTime.MinValue; volatile bool probing;
    readonly System.Windows.Forms.Timer quotaTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
    public void ProbeQuota(bool force) {
        if (probing || AnyBusy) return;                                   // une conversation en cours renvoie déjà ces infos
        // chaque mesure lance un claude -p (processus Node + quelques tokens) : seulement si l'info date de plus de 30 min ;
        // force = demandé par la page (fenêtre de quota écoulée) : au plus une fois toutes les 5 min
        if (!force && (DateTime.Now - lastQuotaAt).TotalMinutes < 30) return;
        if ((DateTime.Now - lastProbe).TotalMinutes < 5) return;
        probing = true; lastProbe = DateTime.Now;
        new Thread(() => {
            try {
                var psi = new ProcessStartInfo("claude", "-p --model haiku --setting-sources \"\" --strict-mcp-config --no-session-persistence --tools \"\" "
                    + "--system-prompt \"Reponds juste: ok\" --output-format stream-json --verbose") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath() };
                psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = "off";
                using (var p = Process.Start(psi)) {
                    KillAfter(p, 60000);
                    p.StandardInput.Write("."); p.StandardInput.Close();
                    p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null) {
                        var m = Regex.Match(line, "\"rate_limit_info\":(\\{.*\\}),\"uuid\"");
                        if (line.Contains("\"rate_limit_event\"") && m.Success) { lastQuotaAt = DateTime.Now; Post("quota\n" + m.Groups[1].Value); }
                    }
                    p.WaitForExit(30000);
                }
            } catch (Exception ex) { Program.Log("Quota", ex.Message); }
            probing = false;
        }) { IsBackground = true, Name = "quota" }.Start();
    }

    // ---- réponse enregistrée en .txt dans Téléchargements (nom lisible, jamais d écrasement) ----
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    public static string DownloadsDir() {
        IntPtr p;
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0) {
            try { string d = Marshal.PtrToStringUni(p); if (Directory.Exists(d)) return d; } finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
    public static string SaveTxt(string name, string text) {
        name = Regex.Replace(name ?? "", "[\\x00-\\x1f<>:\"/\\\\|?*]+", " ");
        name = Regex.Replace(name, "\\s+", " ").Trim().TrimEnd('.', ' ');
        if (name.Length > 90) name = name.Substring(0, 90).TrimEnd();
        if (name.Length == 0) name = "Réponse de " + Settings.Name;
        string dir = DownloadsDir(); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, name + ".txt");
        for (int i = 2; File.Exists(f); i++) f = Path.Combine(dir, name + " (" + i + ").txt");
        File.WriteAllText(f, (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));   // BOM : accents lus partout (Bloc-notes, anciens éditeurs)
        return f;
    }

    // ---- titre de conversation résumé par Claude (Haiku, quelques tokens, en arrière-plan) ----
    void MakeTitle(string id, string excerpt) {
        new Thread(() => {
            try {
                var psi = new ProcessStartInfo("claude", "-p --model haiku --setting-sources \"\" --strict-mcp-config --no-session-persistence --tools \"\" "
                    + "--system-prompt \"Tu donnes un titre a une conversation entre un utilisateur et son assistant. Reponds UNIQUEMENT par le titre, en francais : "
                    + "2 a 5 mots, 40 caracteres maximum, qui nomment le sujet concret (objet, fichier, nom propre, cours, projet) plutot que l action. "
                    + "Commence par le mot le plus distinctif. Interdits : mots vagues (Aide, Question, Demande, Discussion, Conversation, Probleme, Assistance), guillemets, point final, emoji. "
                    + "Si d autres titres sont donnes, le tien doit s en distinguer clairement.\" --output-format text") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath() };
                psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = "off";
                using (var p = Process.Start(psi)) {
                    KillAfter(p, 60000);
                    var bytes = Encoding.UTF8.GetBytes(excerpt);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.Close();
                    p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                    string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(30000);
                    o = (o ?? "").Trim().Split('\n')[0].Trim().Trim('"', '«', '»', '*', '#', '.', ' ');
                    if (o.Length > 60) { int sp = o.LastIndexOf(' ', 58); o = (sp > 20 ? o.Substring(0, sp) : o.Substring(0, 58)).TrimEnd(',', ';', ':', '-', ' ') + "…"; }
                    if (o.Length > 1 && p.ExitCode == 0) Post("title\n" + id + "\n" + o);
                }
            } catch (Exception ex) { Program.Log("Titre", ex.Message); }
        }) { IsBackground = true, Name = "titre" }.Start();
    }

    // ---- mises à jour ----
    readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer { Interval = 6 * 3600 * 1000 };
    DateTime lastUpd = DateTime.MinValue; volatile bool updChecking;
    string UpdJson() {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "current", Program.Version }, { "latest", Updater.Latest },
            { "available", Updater.Available }, { "ok", Updater.ShowOk }, { "url", Updater.Url }, { "notes", Updater.Notes } });
    }
    void CheckUpdate() {
        if (updChecking) return; updChecking = true; lastUpd = DateTime.Now;
        new Thread(() => {
            try { Updater.Check(); } catch (Exception ex) { Program.Log("Mise à jour", ex.Message); }
            updChecking = false; Post("upd\n" + UpdJson());
        }) { IsBackground = true, Name = "maj-verif" }.Start();
    }

    // ---- compactage : résume le début d une longue conversation (Haiku) pour que chaque message coûte moins de tokens ----
    Process compactProc; string compactKey;
    void Compact(string key, string sid) {
        Runner rr; runners.TryGetValue(key ?? "", out rr);
        if ((rr != null && rr.Busy) || compactProc != null || compactKey != null || !Regex.IsMatch(sid ?? "", "^[0-9a-fA-F-]{36}$")) { Post("compacted\n" + key + "\n0\n0\n0"); return; }
        if (rr != null) Retire(rr);                                          // la session ouverte repartira du résumé au prochain message
        compactKey = key;
        var psi = new ProcessStartInfo("claude", "-p --model haiku --resume " + sid + " --output-format stream-json --verbose --settings " + Util.Quote(GuardSettings())) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = RunDir(sid, Flash, Project) };
        psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = "off";
        new Thread(() => {
            long pre = 0, post = 0; bool ok = false;
            try {
                using (var p = Process.Start(psi)) {
                    compactProc = p; KillAfter(p, 240000);
                    p.StandardInput.Write("/compact"); p.StandardInput.Close();
                    p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null) {
                        if (line.Contains("\"compact_result\":\"success\"")) ok = true;
                        var a = Regex.Match(line, "\"pre_tokens\":(\\d+)"); if (a.Success) long.TryParse(a.Groups[1].Value, out pre);
                        var b = Regex.Match(line, "\"post_tokens\":(\\d+)"); if (b.Success) long.TryParse(b.Groups[1].Value, out post);
                    }
                    p.WaitForExit(10000);
                }
            } catch (Exception ex) { Program.Log("Compactage", ex.Message); }
            compactProc = null; compactKey = null;
            Post("compacted\n" + key + "\n" + (ok ? 1 : 0) + "\n" + pre + "\n" + post);
        }) { IsBackground = true, Name = "compact" }.Start();
    }

    static void KillAfter(Process p, int ms) {
        new Thread(() => { try { if (!p.WaitForExit(ms)) p.Kill(); } catch { } }) { IsBackground = true, Name = "délai" }.Start();
    }

    // ---- agrandir / réduire par les bords : on suit la souris tant que le bouton gauche est enfoncé ----
    System.Windows.Forms.Timer rszT;
    static Size SavedSize() {
        var p = Settings.Str("ChatSize", "").Split(',');
        int w, h;
        if (p.Length == 2 && int.TryParse(p[0], out w) && int.TryParse(p[1], out h)) return new Size(Math.Max(ChatW, w), Math.Max(ChatH, h));
        return new Size(ChatW, ChatH);
    }
    void StartResize(string dir) {
        if (rszT != null) return;
        ReleaseCapture();
        var start = Cursor.Position; var b0 = Bounds; var wa = Screen.FromControl(this).WorkingArea;
        Moved = true;                                                     // pendant le geste, la fenêtre ne se recale pas sur le chat
        rszT = new System.Windows.Forms.Timer { Interval = 15 };
        rszT.Tick += (s, e) => {
            if ((Control.MouseButtons & MouseButtons.Left) == 0) {
                rszT.Stop(); rszT.Dispose(); rszT = null;
                Settings.Set("ChatSize", Width + "," + Height); LayoutSide(); return;
            }
            var c = Cursor.Position; int dx = c.X - start.X, dy = c.Y - start.Y;
            int l = b0.Left, t = b0.Top, r = b0.Right, btm = b0.Bottom;
            if (dir.Contains("w")) l = Math.Max(wa.Left, Math.Min(r - ChatW, b0.Left + dx));
            if (dir.Contains("e")) r = Math.Min(wa.Right, Math.Max(l + ChatW, b0.Right + dx));
            if (dir.Contains("n")) t = Math.Max(wa.Top, Math.Min(btm - ChatH, b0.Top + dy));
            if (dir.Contains("s")) btm = Math.Min(wa.Bottom, Math.Max(t + ChatH, b0.Bottom + dy));
            var nb = Rectangle.FromLTRB(l, t, r, btm);
            if (nb != Bounds) Bounds = nb;
        };
        rszT.Start();
    }
    public void PostTheme() { Post("theme\n" + (Settings.Bool("Light", false) ? "light" : "dark")); }
    public const int ChatW = 460, ChatH = 660;          // taille minimale (et par défaut) de la discussion
    // ---- déplacer la fenêtre par son en-tête ; elle reprend sa place à la fermeture, sauf si elle est épinglée ----
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public bool Moved;
    void DragWindow() {
        var before = Location;
        ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN sur la barre de titre
        if (Location != before) Moved = true;
    }
    readonly System.Windows.Forms.Timer calTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
    // ---- volets latéraux (flèches + calendrier + dates importantes) ----
    ArrowTab tabL, tabR; CalWidget calW; UpWidget upW;
    void InitSide() {
        tabL = new ArrowTab(true); tabR = new ArrowTab(false); calW = new CalWidget(); upW = new UpWidget();
        tabL.Clicked = () => { Settings.Set("DashCal", !Settings.Bool("DashCal", false)); LayoutSide(); };
        tabR.Clicked = () => { Settings.Set("DashUp", !Settings.Bool("DashUp", false)); LayoutSide(); };
        calW.Changed = LayoutSide;
        upW.OpenSettings = () => owner.ShowSettings("manual");
        upW.Changed = LayoutSide;
        VisibleChanged += (s, e) => { if (!Visible && !Pinned) { Moved = false; calW.Moved = false; upW.Moved = false; } LayoutSide(); };
        LocationChanged += (s, e) => LayoutSide(false);   // simple déplacement : on bouge les volets sans les redessiner
        SizeChanged += (s, e) => LayoutSide(false);
    }
    public void LayoutSide() { LayoutSide(true); }
    void LayoutSide(bool redraw) {
        if (tabL == null) return;
        if (!Visible) { tabL.Hide(); tabR.Hide(); calW.Hide(); upW.Hide(); return; }
        bool c = Settings.Bool("DashCal", false), u = Settings.Bool("DashUp", false);
        var wa = Screen.FromControl(this).WorkingArea;
        int mid = Top + Height / 2;
        tabL.Open = c; tabR.Open = u;
        tabL.Place(new Point(Left - 30, mid - 24), redraw);
        tabR.Place(new Point(Right + 4, mid - 24), redraw);
        if (c) calW.Place(calW.Moved ? calW.Location : new Point(Math.Max(wa.Left, tabL.Left - 6 - calW.Width), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - calW.Height))), redraw); else calW.Hide();
        if (u) upW.Place(upW.Moved ? upW.Location : new Point(Math.Min(wa.Right - upW.Width, tabR.Right + 6), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - upW.Height))), redraw); else upW.Hide();
    }
    public void ScanMail() {
        Mailbox.Scan(() => { try { BeginInvoke((Action)(() => { PostCalendars(); owner.RefreshSettings(); })); } catch { } });
        LayoutSide(); owner.RefreshSettings();
    }
    void DailyMailScan() {
        if (!(Mailbox.GmailReady || Mailbox.OutlookOn) || Mailbox.DoneToday) return;
        ScanMail();
    }

    public void PostCalendars() {
        if (InvokeRequired) { try { BeginInvoke((Action)PostCalendars); } catch { } return; }
        Post("cal\n" + Calendars.Json()); LayoutSide();
    }
    public void RefreshCalendars() { Post("calbusy"); Calendars.Refresh(PostCalendars); }

    public bool PickFolder(IWin32Window parent = null) {
        using (var d = new FolderBrowserDialog { SelectedPath = WorkDir, Description = "Dossier dans lequel Claude va travailler" }) {
            if (d.ShowDialog(parent ?? this) != DialogResult.OK) return false;
            WorkDir = d.SelectedPath;
            Settings.Set("WorkDir", WorkDir);
            Post("dir\n" + WorkDir);
            return true;
        }
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
        string m;
        try { m = e.TryGetWebMessageAsString(); } catch { return; }
        var parts = m.Split(new[] { '\n' }, 5);
        switch (parts[0]) {
            case "files":
                var paths = new List<string>();
                if (e.AdditionalObjects != null)
                    foreach (var o in e.AdditionalObjects) { var f = o as CoreWebView2File; if (f != null) paths.Add(f.Path); }
                if (paths.Count > 0) Post("paths\n" + string.Join("\n", paths.ToArray()));
                break;
            case "ready":
                pageReady = true;
                Post("pinned\n" + (Pinned ? 1 : 0)); PostTheme();
                Post("dir\n" + WorkDir); Post("name\n" + Settings.Name);
                PostCalendars(); ProbeQuota(false); Post("upd\n" + UpdJson());               // quota : mesuré seulement s'il date
                Flush(); if (Visible) Post("focus"); break;
            case "convsave": { var sp = m.Split(new[] { '\n' }, 3); if (sp.Length == 3) ConvStore.Save(sp[1], sp[2]); break; }   // convsave\n<id>\n<json>
            case "convdel": ConvStore.Delete(parts.Length > 1 ? parts[1] : ""); break;
            case "pasteimg": {                                                 // image collée (Ctrl+V) : PNG dans le dossier que Claude peut lire
                string b64 = m.Length > 9 ? m.Substring(9) : "";
                try {
                    int c = b64.IndexOf(','); if (c >= 0 && c < 100) b64 = b64.Substring(c + 1);
                    var bytes = Convert.FromBase64String(b64);
                    string f = Path.Combine(ScreenDir(), "image-collee-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms)) img.Save(f, ImageFormat.Png);
                    Post("pasted\n" + f);
                } catch (Exception ex) { Program.Log("Image collée", ex.Message); Post("saveerr\nimage illisible"); }
                break;
            }
            case "remindcancel": MyReminders.Cancel(m.Length > 13 ? m.Substring(13) : ""); break;
            case "calrefresh": RefreshCalendars(); break;
            case "pin": Pinned = parts.Length > 1 && parts[1] == "1"; Settings.Set("Pinned", Pinned); break;
            case "sid": owner.SessionId = parts.Length > 1 ? parts[1] : ""; break;
            case "terminal": owner.OpenTerminal(true); break;
            case "settings": settingsAsked = DateTime.Now; owner.ShowSettings(); break;   // ouverts depuis la discussion : elle reste ouverte
            case "hide": Hide(); break;
            case "title": {                                                    // title\n<id conversation>\n<extrait>
                int i1 = m.IndexOf('\n'), i2 = i1 < 0 ? -1 : m.IndexOf('\n', i1 + 1);
                if (i2 > 0) MakeTitle(m.Substring(i1 + 1, i2 - i1 - 1), m.Substring(i2 + 1));
                break; }
            case "savetxt": {                                                  // savetxt\n<nom sans extension>\n<texte>
                var sp = m.Split(new[] { '\n' }, 3);
                if (sp.Length == 3) try { Post("saved\n" + SaveTxt(sp[1], sp[2])); } catch (Exception ex) { Program.Log("Téléchargement", ex.Message); Post("saveerr\n" + ex.Message); }
                break; }
            case "reveal": { string pth = m.Length > 7 ? m.Substring(7) : ""; if (File.Exists(pth)) try { Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch { } break; }
            case "copy": { string txt = m.Length > 5 ? m.Substring(5) : ""; if (txt.Length > 0) try { Clipboard.SetText(txt); } catch (Exception ex) { Program.Log("Copie", ex.Message); } break; }
            case "drag": DragWindow(); break;
            case "resize": StartResize(parts.Length > 1 ? parts[1] : "se"); break;
            case "compact": Compact(parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : ""); break;   // compact\n<clé>\n<sid>
            case "updcheck": CheckUpdate(); break;
            case "updseen": Settings.Set("UpdatedShown", ""); break;
            case "updopen": try { Process.Start(Updater.Url.Length > 0 ? Updater.Url : "https://github.com/" + Updater.Repo + "/releases"); } catch { } break;
            case "updinstall":
                Post("updstatus\nbusy\nTéléchargement de Bébène " + Updater.Latest + "…");
                new Thread(() => {
                    string err = null;
                    try { Updater.Install(); } catch (Exception ex) { err = ex.Message; Program.Log("Mise à jour", ex.ToString()); }
                    if (err != null) { Post("updstatus\nerr\n" + err); return; }
                    Post("updstatus\nrestart\n");
                    try { BeginInvoke((Action)(() => owner.Quit())); } catch { }
                }) { IsBackground = true, Name = "maj" }.Start();
                break;
            case "quotaprobe": ProbeQuota(false); break;
            case "folder": PickFolder(); break;
            case "projects": Post("projects\n" + new JavaScriptSerializer().Serialize(ListProjects())); break;
            case "mode":                                   // mode\n<flash 0/1>\n<projet>
                Flash = parts.Length > 1 && parts[1] == "1";
                Project = parts.Length > 2 ? SafeName(parts[2]) : "";
                Post("dir\n" + CurrentDir()); break;
            case "newproject": {
                string n = SafeName(parts.Length > 1 ? parts[1] : "");
                if (n.Length == 0) break;
                string d = Path.Combine(ProjectsDir, n);
                try {
                    Directory.CreateDirectory(d);
                    if (!File.Exists(Path.Combine(d, "NOTES.md")))
                        File.WriteAllText(Path.Combine(d, "NOTES.md"), "# " + n + "\n\n## État\n- Créé le " + DateTime.Today.ToString("yyyy-MM-dd") + " depuis Bébène.\n", new UTF8Encoding(false));
                } catch (Exception ex) { Program.Log("Projet", ex.ToString()); break; }
                Post("projects\n" + new JavaScriptSerializer().Serialize(ListProjects()));
                Post("project\n" + n);
                break;
            }
            case "ensureproject": {                                             // la reponse annonce un projet : il existe dans le menu #
                string n = SafeName(parts.Length > 1 ? parts[1] : "");
                if (n.Length == 0 || n == "claude-companion") break;
                try { Directory.CreateDirectory(Path.Combine(ProjectsDir, n)); } catch (Exception ex) { Program.Log("Projet", ex.Message); }
                Post("projects\n" + new JavaScriptSerializer().Serialize(ListProjects()));
                break;
            }
            case "forget": Forget(parts.Length > 1 ? parts[1] : ""); break;
            case "notes": Post("notes\n" + Notes.Json()); break;
            case "notenew": Notes.New(); break;
            case "noteshow": Notes.Show(parts.Length > 1 ? parts[1] : ""); break;
            case "notehide": Notes.Hide(parts.Length > 1 ? parts[1] : ""); break;
            case "notedel": Notes.Delete(parts.Length > 1 ? parts[1] : ""); break;
            case "stop": StopRun(parts.Length > 1 ? parts[1] : ""); break;                       // stop\n<clé>
            case "calm": Calm(parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null); break;   // question / demande abandonnée
            case "send": {                                 // send\n<clé>\n<auto>\n<sid>\n<autorisés>\n<flash>\n<projet>\n<texte>
                var sp = m.Split(new[] { '\n' }, 8);
                if (sp.Length == 8) Send(sp[1], sp[7], sp[2] == "1", sp[3], sp[4], sp[5] == "1", sp[6]);
                break;
            }
            case "progress": {                             // progress\n<clé>\n<fait>\n<total> (liste d étapes TodoWrite)
                Runner pr; int dn, tt;
                if (parts.Length > 3 && runners.TryGetValue(parts[1], out pr) && int.TryParse(parts[2], out dn) && int.TryParse(parts[3], out tt)) { pr.Done = dn; pr.Total = tt; UpdateCat(false, 0); }
                break;
            }
            case "notify": {                               // notify\n<type>\n<actif 0/1>\n<clé>\n<titre>
                var sp = m.Split(new[] { '\n' }, 5);
                if (sp.Length == 5 && (!Visible || sp[2] != "1")) owner.Notify(sp[1], sp[4], sp[3]);
                break;
            }
        }
    }

    // « Arrêter » : tue Claude ET les commandes qu'il a lancées (sinon elles continuent en fond)

    // ---- Gardien : Bébène ne peut pas modifier son propre programme (code, installation, réglages, données) ----
    // Chaque conversation de Bébène charge un hook PreToolUse qui bloque toute écriture ou commande visant Bébène.
    // Seul Claude Code lancé par l'utilisateur dans le terminal (sans ce hook) fait évoluer Bébène.
    const string GuardScript =
        "# Gardien de Bébène : bloque toute modification de son propre programme depuis ses conversations\r\n" +
        "$in = [Console]::In.ReadToEnd() | ConvertFrom-Json\r\n" +
        "$t = $in.tool_input\r\n" +
        "$s = (@($t.command, $t.file_path, $t.notebook_path, $t.path, $in.cwd) -join ' ').Replace('\\', '/').ToLower()\r\n" +
        "foreach ($p in @('claudehub/projets/claude-companion', 'claude-companion', 'claudecompanion')) {\r\n" +
        "  if ($s.Contains($p)) {\r\n" +
        "    [Console]::Error.WriteLine('Bloqué : Bébène ne peut pas modifier son propre programme, ses réglages ou ses données. Pour faire évoluer Bébène, utilise Claude Code dans le terminal.')\r\n" +
        "    exit 2\r\n" +
        "  }\r\n" +
        "}\r\n" +
        "exit 0\r\n";
    static string guardSettings;
    public static string GuardSettings() {
        if (guardSettings != null && File.Exists(guardSettings)) return guardSettings;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion");
        Directory.CreateDirectory(dir);
        string ps1 = Path.Combine(dir, "garde.ps1"), js = Path.Combine(dir, "garde-settings.json");
        File.WriteAllText(ps1, GuardScript, new UTF8Encoding(true));
        var hook = new Dictionary<string, object> { { "type", "command" }, { "timeout", 15 },
            { "command", "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ps1.Replace('\\', '/') + "\"" } };
        var settings = new Dictionary<string, object> { { "hooks", new Dictionary<string, object> { { "PreToolUse", new object[] {
            new Dictionary<string, object> { { "matcher", "Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell" }, { "hooks", new object[] { hook } } } } } } } };
        File.WriteAllText(js, new JavaScriptSerializer().Serialize(settings), new UTF8Encoding(false));
        return guardSettings = js;
    }

    // « Regarder l'écran » : capture de la fenêtre que l'utilisateur avait devant lui (même si la discussion la recouvre),
    // ou de tout l'écran. Claude lance ce script puis lit l'image. Hors des dossiers de Bébène (le gardien bloquerait la commande).
    const string ScreenScript =
        "param([long]$Hwnd = 0, [switch]$Ecran)\r\n" +
        "Add-Type -AssemblyName System.Drawing, System.Windows.Forms\r\n" +
        "Add-Type -TypeDefinition @'\r\n" +
        "using System; using System.Runtime.InteropServices;\r\n" +
        "public static class BW {\r\n" +
        "  [DllImport(\"user32.dll\")] public static extern bool SetProcessDPIAware();\r\n" +
        "  [DllImport(\"user32.dll\")] public static extern bool IsWindow(IntPtr h);\r\n" +
        "  [DllImport(\"user32.dll\")] public static extern bool IsIconic(IntPtr h);\r\n" +
        "  [DllImport(\"user32.dll\")] public static extern bool GetWindowRect(IntPtr h, out RECT r);\r\n" +
        "  [DllImport(\"user32.dll\")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);\r\n" +
        "  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }\r\n" +
        "}\r\n" +
        "'@\r\n" +
        "[BW]::SetProcessDPIAware() | Out-Null\r\n" +
        "$dir = Join-Path $env:TEMP 'BebeneEcran'; New-Item -ItemType Directory -Force $dir | Out-Null\r\n" +
        "$out = Join-Path $dir 'ecran.png'; $h = [IntPtr]$Hwnd; $bmp = $null; $quoi = 'tout l ecran'\r\n" +
        "if (-not $Ecran -and $Hwnd -ne 0 -and [BW]::IsWindow($h) -and -not [BW]::IsIconic($h)) {\r\n" +
        "  $r = New-Object BW+RECT; [BW]::GetWindowRect($h, [ref]$r) | Out-Null\r\n" +
        "  $w = $r.R - $r.L; $hh = $r.B - $r.T\r\n" +
        "  if ($w -gt 50 -and $hh -gt 50) {\r\n" +
        "    $bmp = New-Object Drawing.Bitmap $w, $hh; $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()\r\n" +
        "    $ok = [BW]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()\r\n" +
        "    if ($ok) { $quoi = 'la fenetre active' } else { $bmp.Dispose(); $bmp = $null }\r\n" +
        "  }\r\n" +
        "}\r\n" +
        "if (-not $bmp) {\r\n" +
        "  $v = [Windows.Forms.SystemInformation]::VirtualScreen\r\n" +
        "  $bmp = New-Object Drawing.Bitmap $v.Width, $v.Height; $g = [Drawing.Graphics]::FromImage($bmp)\r\n" +
        "  $g.CopyFromScreen($v.Left, $v.Top, 0, 0, $bmp.Size); $g.Dispose()\r\n" +
        "}\r\n" +
        "if ($bmp.Width -gt 1600) {\r\n" +
        "  $s = 1600 / $bmp.Width; $sm = New-Object Drawing.Bitmap 1600, ([int]($bmp.Height * $s))\r\n" +
        "  $g = [Drawing.Graphics]::FromImage($sm); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($bmp, 0, 0, $sm.Width, $sm.Height); $g.Dispose()\r\n" +
        "  $bmp.Dispose(); $bmp = $sm\r\n" +
        "}\r\n" +
        "$bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()\r\n" +
        "Write-Output \"Capture de $quoi : $out (lis cette image ; si elle est vide ou ne montre qu un logo, relance avec -Ecran a la place de -Hwnd)\"\r\n";
    static string screenScript;
    public static string ScreenTool() {
        if (screenScript != null && File.Exists(screenScript)) return screenScript;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BebeneOutils");
        Directory.CreateDirectory(dir);
        string ps1 = Path.Combine(dir, "ecran.ps1");
        File.WriteAllText(ps1, ScreenScript, new UTF8Encoding(true));
        return screenScript = ps1;
    }
    public static string ScreenDir() { string d = Path.Combine(Path.GetTempPath(), "BebeneEcran"); Directory.CreateDirectory(d); return d; }
    public static string ScreenCommand(long hwnd) {
        return "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScreenTool().Replace('\\', '/') + "\"" + (hwnd != 0 ? " -Hwnd " + hwnd : " -Ecran");
    }

    // ---- tâches en parallèle : une session Claude gardée ouverte par conversation (--input-format stream-json) ----
    // Chaque message part dans la session de SA conversation ; jusqu à MaxBusy conversations travaillent en même temps.
    // Une session reste ouverte entre deux messages (pas de redémarrage = réponse plus rapide), et se ferme après 10 min sans rien.
    // Si les réglages de lancement changent (autorisations, mode autonome, modèle…), la session est relancée avec --resume.
    class Runner {
        public string Key, Sig = "", Model = "", HandKind = ""; public bool Busy, Asking, Hand, Stopping, Lite; public int Done, Total;
        public Process P; public readonly StringBuilder Err = new StringBuilder(); public DateTime Used = DateTime.Now, FlagAt = DateTime.MinValue;
    }
    readonly Dictionary<string, Runner> runners = new Dictionary<string, Runner>();
    const int MaxBusy = 3;
    public bool AnyBusy { get { foreach (var r in runners.Values) if (r.Busy) return true; return false; } }
    readonly System.Windows.Forms.Timer idleT = new System.Windows.Forms.Timer { Interval = 60000 };
    void InitRunners() {
        idleT.Tick += (s, e) => {
            FreeWebIfIdle();
            foreach (var r in new List<Runner>(runners.Values)) {
                if (!r.Busy && r.P != null && (DateTime.Now - r.Used).TotalMinutes > 10) Retire(r);           // session endormie : libère la mémoire
                if ((r.Asking || r.Hand) && !r.Busy && (DateTime.Now - r.FlagAt).TotalMinutes > 30) { r.Asking = r.Hand = false; UpdateCat(false, 0); }
            }
        };
        idleT.Start();
    }
    static void KillProc(Process p) {
        if (p == null) return;
        try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }); }
        catch { try { p.Kill(); } catch { } }
    }
    void Retire(Runner r) { var p = r.P; r.P = null; r.Stopping = false; if (p == null) return; try { p.StandardInput.Close(); } catch { } KillAfter(p, 4000); }
    void StopRun(string key) { Runner r; if (runners.TryGetValue(key ?? "", out r) && r.P != null && r.Busy) { r.Stopping = true; KillProc(r.P); } }
    void KillTree() { foreach (var r in runners.Values) KillProc(r.P); }
    public void KillAll() { KillTree(); }
    public void TestSend(string key, string text) { var lw = Companion.LastWindow; Companion.LastWindow = ""; Send(key, text, false, "", "", true, ""); Companion.LastWindow = lw; }
    public int RunnerPid(string key) { Runner r; return runners.TryGetValue(key, out r) && r.P != null ? r.P.Id : 0; }
    void PostDone(string key, string code, string err) { Post("rdone\n" + key + "\n" + code + "\n" + err); }

    // retour sur le fil de l interface : par la discussion si elle a déjà une fenêtre, sinon par le chat (toujours créé)
    Control UiControl { get { return IsHandleCreated ? (Control)this : owner.IsHandleCreated ? (Control)owner : null; } }
    bool UiNeeded { get { var c = UiControl; return c == null || c.InvokeRequired; } }
    void OnUi(Action a) {
        var c = UiControl;
        if (c == null) { Program.Log("Tâches", "aucune fenêtre pour revenir sur le fil de l'interface"); return; }
        try { c.BeginInvoke(a); } catch (Exception ex) { Program.Log("Tâches", ex.Message); }
    }

    // le chat montre l état le plus important de toutes les tâches : besoin de toi > question > travail > fini
    void UpdateCat(bool finished, int code) {
        if (UiNeeded) { OnUi(() => UpdateCat(finished, code)); return; }
        Runner hand = null; bool ask = false, busy = false; int done = 0, total = 0;
        foreach (var r in runners.Values) {
            if (r.Hand && hand == null) hand = r; if (r.Asking) ask = true;
            if (r.Busy) { busy = true; if (r.Total > 0) { done = r.Done; total = r.Total; } }
        }
        owner.Progress(busy ? done : 0, busy ? total : 0);
        string s = hand != null ? "hand" : ask ? "question" : busy ? "thinking" : finished ? (!Visible ? "ready" : code == 0 ? "happy" : "idle")
                 : (owner.State == "thinking" || owner.State == "question" || owner.State == "hand") ? "idle" : null;
        if (s == "hand") owner.SetHand(hand.HandKind); else if (s != null && s != owner.State) owner.SetState(s);
    }
    void Calm(string key) {
        foreach (var r in runners.Values) if ((key == null || r.Key == key) && !r.Busy) { r.Asking = false; r.Hand = false; }
        UpdateCat(false, 0);
    }

    // réponse rapide : une question simple, sans action ni fichier → Haiku (moins de quota) ; sinon le modèle choisi
    static readonly Regex Heavy = new Regex(@"(fai[st]|cr[ée]e|modifi|corrig|[ée]cri[st]|r[ée]dig|g[ée]n[èe]r|produi|ouvr|rang|d[ée]plac|supprim|rempli|install|code|script|fichier|dossier|document|excel|word|powerpoint|pdf|projet|analys|r[ée]sum|tradui|calcul|regard|[ée]cran|cliqu|cherch|trouv|mail|planifi|organis|compar|d[ée]taill|plan\b|rapport|pr[ée]sentation|tableau|\\|/|\.[a-z]{2,4}\b|```)", RegexOptions.IgnoreCase);
    static bool Simple(string t) { t = (t ?? "").Trim(); return t.Length > 0 && t.Length <= 160 && t.IndexOf('\n') < 0 && !Heavy.IsMatch(t); }
    string PickModel(string text, string sid, Runner r) {
        string m = Settings.Str("Model", "");
        if (Regex.IsMatch(m, "^[a-z0-9.-]+$")) return m;                                   // modèle imposé dans les réglages
        if (!Settings.Bool("AutoModel", true)) return "";
        bool fresh = !Regex.IsMatch(sid ?? "", "^[0-9a-fA-F-]{36}$");
        return Simple(text) && (fresh || (r != null && r.Lite)) ? "haiku" : "";            // une conversation passée au modèle normal y reste
    }

    void Send(string key, string text, bool autonomous, string sessionId, string allowed, bool flash, string project) {
        project = SafeName(project);
        if (compactKey == key) { PostDone(key, "-1", "Je termine de compacter la conversation : réessaie dans un instant."); return; }
        Runner r; runners.TryGetValue(key, out r);
        if (r != null && r.Busy) { PostDone(key, "-1", "Claude est encore en train de répondre dans cette conversation : attends la fin ou clique sur Arrêter."); return; }
        int busyN = 0; foreach (var x in runners.Values) if (x.Busy) busyN++;
        if (busyN >= MaxBusy) { PostDone(key, "-1", MaxBusy + " tâches tournent déjà en parallèle : attends qu'une se termine, ou arrête-en une."); return; }
        string model = PickModel(text, sessionId, r);
        // Le contexte qui change (fenêtre active) va dans le MESSAGE, pas dans le prompt système :
        // un prompt système stable garde l'historique en cache → beaucoup moins de tokens à chaque échange.
        if (Companion.LastWindow.Length > 0)
            text = "[Fenêtre active : " + Companion.LastWindow + " · pour la voir : " + ScreenCommand(Companion.LastHwnd) + "]\n" + text;
        text = "[Maintenant : " + DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", new System.Globalization.CultureInfo("fr-CH")) + "]\n" + text;   // pour les rappels (« dans 20 min »)
        string args = BuildArgs(autonomous, allowed, flash, project, model);
        if (r == null) { r = new Runner { Key = key }; runners[key] = r; }
        if (r.P != null && (r.P.HasExited || r.Sig != args)) Retire(r);                   // réglages changés : on relance la session
        if (r.P == null) {
            string full = args + (Regex.IsMatch(sessionId, "^[0-9a-fA-F-]{36}$") ? " --resume " + sessionId : "");
            if (!StartRunner(r, full, RunDir(sessionId, flash, project))) return;
            r.Sig = args; r.Model = model;
        }
        r.Lite = model == "haiku";
        r.Busy = true; r.Asking = r.Hand = false; r.Stopping = false; r.Done = r.Total = 0; r.Used = DateTime.Now; lock (r.Err) r.Err.Clear();
        string msg = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "type", "user" }, { "message", new Dictionary<string, object> { { "role", "user" }, { "content", text } } } });
        try {
            var bytes = Encoding.UTF8.GetBytes(msg + "\n");
            r.P.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); r.P.StandardInput.BaseStream.Flush();
        } catch (Exception) { }   // Claude s'est arrêté tout de suite : son message d'erreur arrivera par « rdone »
        UpdateCat(false, 0);
    }

    // texte d'un message « assistant » du flux stream-json
    static string AssistantText(string json) {
        var sb = new StringBuilder();
        try {
            var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            var msg = o != null && o.ContainsKey("message") ? o["message"] as Dictionary<string, object> : null;
            var content = msg != null && msg.ContainsKey("content") ? msg["content"] as object[] : null;
            if (content != null) foreach (var c in content) { var cd = c as Dictionary<string, object>; if (cd != null && cd.ContainsKey("text")) sb.Append(Convert.ToString(cd["text"])).Append('\n'); }
        } catch { }
        return sb.ToString();
    }
    static string TopType(string json) {
        try { var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>; object t; return o != null && o.TryGetValue("type", out t) ? Convert.ToString(t) : ""; }
        catch { return ""; }
    }
    bool StartRunner(Runner r, string args, string dir) {
        var psi = new ProcessStartInfo("claude", args) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Caveman (réponses concises) : en mode non interactif, il faut l'activer explicitement
        psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = Settings.Bool("Caveman", true) ? "caveman" : "off";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        string key = r.Key;
        p.OutputDataReceived += (s, e) => {
            string d = e.Data; if (d == null) return;
            // l ordre des clés JSON n est pas fixe : on lit le « type » de premier niveau
            string type = d.Contains("\"type\":\"result\"") || d.Contains("bebene-") ? TopType(d) : "";
            if (type == "assistant") {
                if (d.Contains("bebene-question")) { r.Asking = true; r.FlagAt = DateTime.Now; }
                if (d.Contains("bebene-main")) { r.Hand = true; r.FlagAt = DateTime.Now; r.HandKind = Regex.IsMatch(d, "bebene-main[^`]*(ecran|écran)") ? "ecran" : "souris"; }
                if (d.Contains("bebene-rappel")) MyReminders.AddFrom(AssistantText(d));
            }
            if (d.Contains("\"rate_limit_event\"")) lastQuotaAt = DateTime.Now;              // quota connu : pas besoin de le mesurer
            Post("rline\n" + key + "\n" + d);
            if (type == "result") {                                                            // fin du tour : la session reste ouverte pour le message suivant
                try { OnUi(() => {
                    if (r.P != p || !r.Busy) return;
                    r.Busy = false; r.Used = DateTime.Now; PostDone(key, "0", ""); UpdateCat(true, 0);
                }); } catch { }
            }
        };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (r.Err) r.Err.AppendLine(e.Data); };
        p.Exited += (s, e) => {
            int code = -1; try { p.WaitForExit(); code = p.ExitCode; } catch { }
            try { OnUi(() => {
                if (r.P == p) r.P = null;
                if (r.Busy) {
                    string er; lock (r.Err) er = r.Err.ToString();
                    r.Busy = false; PostDone(key, r.Stopping ? "1" : (code == 0 ? "2" : code.ToString()), r.Stopping ? "" : er);
                    r.Stopping = false; UpdateCat(true, code);
                }
                try { p.Dispose(); } catch { }
            }); } catch { } // l'app se ferme pendant que Claude répond
        };
        try { p.Start(); }
        catch (Exception ex) { PostDone(key, "-1", "Impossible de lancer Claude Code : " + ex.Message); return false; }
        r.P = p;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return true;
    }

    string BuildArgs(bool autonomous, string allowed, bool flash, string project, string model) {
        string args = "-p --input-format stream-json --output-format stream-json --verbose " + (Settings.Bool("Stream", true) ? "--include-partial-messages " : "") + "--permission-mode " + (autonomous ? "bypassPermissions" : "acceptEdits");
        // actions autorisées : celles accordées dans la conversation + celles pour lesquelles l utilisateur ne veut plus d alerte (réglages)
        // (noms simples « Bash » ou serveurs MCP « mcp__claude-in-chrome »)
        var allow = new List<string>();
        foreach (var a in (allowed + "," + Settings.Str("AutoAllow", "")).Split(',')) if (Regex.IsMatch(a, "^[A-Za-z][A-Za-z0-9_-]*$") && !allow.Contains(a)) allow.Add(a);
        // regarder l écran : toujours permis (lecture seule), sans carte d accord
        allow.Add("Bash(powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScreenTool().Replace('\\', '/') + "\"*)");
        args += " --allowedTools " + Util.Quote(string.Join(",", allow.ToArray()));
        args += " --add-dir " + Util.Quote(ScreenDir());
        bool chrome = Settings.Bool("Chrome", true);
        if (chrome) args += " --chrome";                                     // pages web : extension Claude in Chrome
        if (Regex.IsMatch(model ?? "", "^[a-z0-9.-]+$")) args += " --model " + model;
        args += " --append-system-prompt " + Util.Quote(Persona(flash, project, chrome).Replace("\r", " ").Replace("\n", " "));
        args += " --settings " + Util.Quote(GuardSettings());                // gardien : pas de modification de Bébène par lui-même
        return args;
    }

    string Persona(bool flash, string project, bool chrome) {
        return "Tu es " + Settings.Name + ", un petit chat noir qui vit sur le bureau Windows de l'utilisateur et l'aide au quotidien "
            + "(questions, petites modifications de fichiers, recommandations). Réponds en français, de façon claire et concise. "
            + "Tu partages la mémoire centrale ClaudeHub (" + Setup.Hub + ") avec Claude Code : tu peux y chercher (skill memoire), "
            + "retenir des choses, créer et gérer les projets (projets/<nom>/NOTES.md) et retrouver les conversations archivées, en suivant les règles du noyau de mémoire. "
            + "Quand l'utilisateur dit travailler sur un projet, écris la ligne « 📁 Projet : <nom> » pour que la conversation y soit sauvegardée. "
            + (flash ? "CONVERSATION FLASH : l'utilisateur ne veut rien enregistrer. N'écris rien dans la mémoire ClaudeHub, ne crée pas de projet et n'écris jamais la ligne « 📁 Projet ». "
                     : project.Length > 0 ? "Cette conversation fait partie du projet « " + project + " » (dossier " + Path.Combine(ProjectsDir, project) + " ; elle est archivée automatiquement dans le projet). Tiens NOTES.md à jour quand ça avance. " : "")
            + "Le message peut commencer par [Fenêtre active : …] : c'est la fenêtre que l'utilisateur avait devant lui (souvent « le fichier ouvert devant moi »). "
            + "VOIR : quand l'utilisateur parle de ce qu'il a devant lui (« ce champ », « cette page », « ce que je vois », « devant moi », « ici »), regarde D'ABORD l'écran "
            + "(lance la commande indiquée dans [Fenêtre active], puis lis l'image produite) AVANT de chercher dans ses dossiers. "
            + "AGIR : tu peux et tu dois agir à la place de l'utilisateur quand il le demande : remplir un champ ou un formulaire web, écrire dans un document, du code, etc. Ne refuse pas. "
            + (chrome ? "Page web dans Chrome : utilise les outils mcp__claude-in-chrome dans TON PROPRE onglet (tabs_create_mcp, ou l'onglet qu'il te désigne), avec find / form_input pour remplir, sans recharger la page ni rien envoyer sans accord. "
                        + "Si l'extension ne répond pas, ou pour un autre navigateur, passe par UI Automation (skill piloter-applis). " : "")
            + "Pour agir dans un programme (Excel, Word, PowerPoint, Outlook, Inventor ou toute autre appli, fichier ouvert compris), utilise la skill piloter-applis. "
            + "MULTITÂCHE : tu travailles en arrière-plan pendant que l'utilisateur fait autre chose sur son PC. Ne prends PAS sa souris ni son clavier "
            + "(pas de clic ni de frappe simulés, pas de SendInput, pas de déplacement du curseur) et ne mets aucune fenêtre au premier plan : passe par COM, la ligne de commande, les fichiers ou ton propre onglet Chrome. "
            + "Si tu as VRAIMENT besoin de sa souris ou de son clavier, ou de voir une fenêtre que tu ne peux pas capturer (fenêtre réduite, cachée, autre bureau, écran verrouillé), n'insiste pas : "
            + "termine ta réponse par un bloc de code de langage bebene-main contenant du JSON {\"quoi\":\"ce dont tu as besoin et pourquoi, en une phrase\",\"type\":\"souris\"} "
            + "(\"type\":\"ecran\" quand il s'agit de voir une fenêtre ou un écran), puis arrête-toi : Bébène le prévient et tu reprends quand il te le donne. "
            + "RAPPELS : si l'utilisateur te demande de lui rappeler quelque chose à un moment donné (« rappelle-moi dans 20 min », « demain à 8 h »), calcule l'heure à partir de [Maintenant : …] "
            + "et termine ta réponse par un bloc de code de langage bebene-rappel contenant du JSON {\"quand\":\"AAAA-MM-JJTHH:MM\",\"quoi\":\"le rappel, en quelques mots\"} (un bloc par rappel). "
            + "Bébène l'affichera à l'heure dite, même discussion fermée : n'utilise pas d'autre moyen (tâche planifiée, script, alarme). "
            + "TÂCHES LONGUES :dès qu'une tâche a plus de 3 étapes, tiens ta liste d'étapes à jour avec l'outil TodoWrite (l'utilisateur voit la progression). "
            + (Settings.Bool("Prepare", true)
                ? "PRÉPARER AVANT DE PRODUIRE : quand l'utilisateur commence un projet ou parle de vouloir faire un fichier (rapport, présentation, document, classeur, code…), "
                  + "NE CRÉE AUCUN FICHIER de résultat tout de suite. D'abord : écoute, rassemble ce qu'il fournit (textes, fichiers, consignes), lis les modèles de référence utiles, "
                  + "et comprends la tâche et le contenu. Puis propose un PLAN SYNTHÉTIQUE du résultat (structure, contenu de chaque partie, format), "
                  + "et signale les LACUNES et problèmes probables (infos manquantes, incohérences, points à risque). Continue à affiner tant qu'il apporte des éléments. "
                  + "Quand le plan te semble complet, termine ta réponse par un bloc de code de langage bebene-pret contenant du JSON {\"quoi\":\"ce que tu vas produire, en quelques mots\"}. "
                  + "Ne produis le fichier QUE lorsqu'il le demande explicitement (« fais-le », « génère », « produis le fichier », « vas-y »…), puis produis-le en entier selon le plan validé. "
                  + "Exceptions, à faire directement : petites retouches d'un fichier existant, post-it, NOTES.md et mémoire ClaudeHub, ou s'il dit lui-même de produire tout de suite. "
                : "")
            + "QUESTIONS :s'il te manque une information CAPITALE (que tu ne peux ni voir à l'écran, ni trouver dans la mémoire, ni deviner raisonnablement) ou un choix qui change vraiment le résultat, "
            + "avant de produire un document, du code, des fichiers ou de remplir quelque chose : n'invente pas, pose UNE seule question et arrête-toi. "
            + "Format exact, à la toute fin de ta réponse : un bloc de code de langage bebene-question contenant du JSON {\"q\":\"la question\",\"choix\":[\"…\",\"…\"],\"autre\":false} "
            + "avec 1 à 3 choix courts (les plus probables en premier) ; \"autre\":true seulement si la réponse est vraiment ouverte ou compliquée ; \"choix\":[] et \"autre\":true pour une info libre (ex. un nom). "
            + "Une question à la fois : tu recevras la réponse dans le message suivant, puis tu continues (ou tu poses la suivante). "
            + "Jamais pour les détails mineurs ni pour demander une autorisation, ni pour une question simple ou une demande basique : choisis un défaut raisonnable et dis-le. "
            + "RÉPONSES : réponds à ce qui est demandé, puis arrête-toi. Une seule réponse, pas plusieurs variantes ou versions au choix, sauf si l'utilisateur en demande. "
            + "Ne termine JAMAIS par une proposition de suite ou de continuation (« Tu veux que… ? », « Je peux aussi… », « Si tu veux, … », pistes ou options non demandées). "
            + "Tu ne peux PAS modifier ton propre programme (code, installation, réglages, données de Bébène) : c'est verrouillé. Si on te le demande, explique que l'évolution de Bébène se fait avec Claude Code dans le terminal. "
            + Templates.PersonaLine() + Notes.PersonaLine()
            + Settings.Str("Prompt", "");
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else KillTree();
    }
}

