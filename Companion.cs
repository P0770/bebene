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

[assembly: System.Reflection.AssemblyTitle("Bébène")]
[assembly: System.Reflection.AssemblyProduct("Bébène")]
[assembly: System.Reflection.AssemblyVersion("1.50.0.0")]

static class Program {
    public const string Version = "1.50";
    public static bool OpenSetup;
    public static readonly uint Pid = (uint)Process.GetCurrentProcess().Id;   // identifiant de Bébène (lu une seule fois)   // --setup : ouvrir directement l'assistant d'installation

    // ---- Diagnostic : journal + détection des blocages de l'interface ----
    public static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "bebene.log");
    static readonly object logLock = new object();
    public static void Log(string what, string detail) {
        try {
            lock (logLock) {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2000000) File.Delete(LogFile);
                File.AppendAllText(LogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + what + "\r\n" + detail + "\r\n\r\n", Encoding.UTF8);
            }
        } catch { }
    }
    static long beat = DateTime.UtcNow.Ticks;
    static void StartWatchdog(Control c) {
        var ui = Thread.CurrentThread;
        bool reported = false;
        new Thread(() => {
            while (true) {
                Thread.Sleep(1000);
                try { if (c.IsHandleCreated) c.BeginInvoke((Action)(() => Interlocked.Exchange(ref beat, DateTime.UtcNow.Ticks))); } catch { }
                double late = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref beat)).TotalSeconds;
                if (late < 3) { reported = false; continue; }
                if (reported) continue;
                reported = true;
                string stack = "(pile indisponible)";
#pragma warning disable 618
                try { ui.Suspend(); try { stack = new StackTrace(ui, false).ToString(); } finally { ui.Resume(); } } catch (Exception ex) { stack += " " + ex.Message; }
#pragma warning restore 618
                Log("BLOCAGE de l'interface depuis " + late.ToString("0") + " s — pile du thread principal :", stack);
            }
        }) { IsBackground = true, Name = "watchdog" }.Start();
    }
    [STAThread]
    static void Main(string[] args) {
        if (args.Length == 3 && args[0] == "--spelltest") { // test : --spelltest "texte" sortie.txt
            var sb = new StringBuilder();
            foreach (var x in Speller.Check(args[1])) sb.AppendLine(x.Word + " -> " + x.Repl);
            File.WriteAllText(args[2], "langue=" + Speller.Lang + "\r\n" + sb, Encoding.UTF8); return;
        }
        if (args.Length == 2 && args[0] == "--paneltest") {   // test : affiche la discussion 5 s sans prendre le clavier et la capture
            Application.EnableVisualStyles();
            ChatPanel.NoActivate = true;
            var d = new Companion(false);
            d.Shown += (s, e) => {
                d.Hide(); d.Panel.Location = new Point(30, 30); d.Panel.Show();
                var t = new System.Windows.Forms.Timer { Interval = 4500 };
                t.Tick += (s2, e2) => {
                    t.Stop();
                    var r = d.Panel.Bounds; r.Inflate(14, 14);
                    using (var b = new Bitmap(r.Width, r.Height)) { using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size); b.Save(args[1], ImageFormat.Png); }
                    Application.Exit();
                };
                t.Start();
            };
            Application.Run(d); return;
        }
        if (args.Length == 3 && args[0] == "--mailtest") {   // test : analyse des fichiers .eml d'un dossier
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(args[1], "*.eml")) {
                var m = Mime.Parse(File.ReadAllBytes(f));
                sb.AppendLine(Path.GetFileName(f) + " | sujet: " + m.Subject + " | reçu: " + m.Received.ToString("yyyy-MM-dd"));
                var ev = Mailbox.Extract(m);
                if (ev.Count == 0) sb.AppendLine("   (rien)");
                foreach (var e in ev) sb.AppendLine("   -> " + e.Kind + " " + (e.AllDay ? e.Start.ToString("yyyy-MM-dd") + " (journée)" : e.Start.ToString("yyyy-MM-dd HH:mm")) + " | " + e.Title + " | lieu: " + e.Loc);
            }
            File.WriteAllText(args[2], sb.ToString(), Encoding.UTF8); return;
        }
        if (args.Length == 2 && args[0] == "--guardtest") { File.WriteAllText(args[1], ChatPanel.GuardSettings()); return; }   // test : prépare le gardien
        if (args.Length == 2 && args[0] == "--setuptest") {   // test : vérifications de l'assistant + création du paquet
            var sb = new StringBuilder();
            foreach (var s in Setup.Check()) sb.AppendLine(s["state"] + "\t" + s["id"] + "\t" + s["detail"]);
            File.WriteAllText(args[1], sb.ToString(), Encoding.UTF8); return;
        }
        if (args.Length == 3 && args[0] == "--manualtest") {   // test : ajout / retrait / remise, puis réglages restaurés
            string bm = Settings.Str("ManualEvents", "[]"), bh = Settings.Str("HiddenEvents", "");
            try {
                Calendars.TestIcs = args[1];
                var done = new ManualResetEvent(false); Calendars.Refresh(() => done.Set()); done.WaitOne(60000);
                var sb = new StringBuilder();
                Func<string> list = () => { var l = new List<string>(); foreach (var e in Calendars.Snapshot()) if (e.Kind.Length > 0 && e.ForList) l.Add(e.Kind + ":" + e.Title + (e.Manual ? "(main)" : "")); return string.Join(" | ", l.ToArray()); };
                sb.AppendLine("départ   : " + list());
                Manual.Add(new Dictionary<string, object> { { "k", "rdv" }, { "t", "Coiffeur" }, { "d", DateTime.Today.AddDays(3).ToString("yyyy-MM-dd") }, { "h", "14:30" }, { "l", "Rue du Marché" } });
                sb.AppendLine("+ ajout  : " + list());
                foreach (var e in Calendars.Snapshot()) if (e.Title == "Examen Analyse II") { Manual.Hide(e); break; }
                sb.AppendLine("- retrait: " + list());
                foreach (var k in Manual.Hidden()) Manual.Unhide(k);
                sb.AppendLine("remis    : " + list());
                using (var b = new UpWidget().Snapshot()) b.Save(args[2] + ".png", ImageFormat.Png);
                File.WriteAllText(args[2], sb.ToString(), Encoding.UTF8);
            } finally { Settings.Set("ManualEvents", bm); Settings.Set("HiddenEvents", bh); }
            return;
        }
        if (args.Length == 2 && args[0] == "--bench") { new Companion(false).Bench(args[1]); return; }   // test : temps de rendu d une image du chat
        if (args.Length == 2 && args[0] == "--swingtest") { new Companion(false).SwingTest(args[1]); return; }
        if (args.Length == 3 && args[0] == "--remindtest") {   // test : quels rappels partiraient maintenant + rendu de la carte
            Calendars.TestIcs = args[1];
            var done = new ManualResetEvent(false); Calendars.Refresh(() => done.Set()); done.WaitOne(60000);
            var sb = new StringBuilder(); string when; CalEvent ev, firstEv = null;
            while ((ev = Reminders.Due(out when)) != null) { sb.AppendLine(when + " -> " + ev.Kind + " " + ev.Title + " " + ev.Start.ToString("g")); if (firstEv == null) firstEv = ev; }
            if (firstEv == null) firstEv = new CalEvent { Title = "Examen Analyse II", Kind = "exam", Start = DateTime.Today.AddDays(1).AddHours(9), Loc = "HES-SO, salle B12", ForList = true };
            File.WriteAllText(args[2] + ".txt", sb.Length > 0 ? sb.ToString() : "(aucun rappel dû)", Encoding.UTF8);
            using (var b = new ReminderCard { Ev = firstEv, When = "demain" }.Snapshot())
            using (var sheet = new Bitmap(b.Width + 40, b.Height + 30)) using (var g = Graphics.FromImage(sheet)) {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, sheet.Width, sheet.Height), Color.FromArgb(70, 110, 160), Color.FromArgb(150, 90, 120), 30f)) g.FillRectangle(lg, 0, 0, sheet.Width, sheet.Height);
                g.DrawImage(b, 20, 15); sheet.Save(args[2], ImageFormat.Png);
            }
            return;
        }
        if (args.Length == 3 && args[0] == "--sidepreview") {   // test : dessine les volets sur un faux bureau
            Calendars.TestIcs = args[1];
            var done = new ManualResetEvent(false); Calendars.Refresh(() => done.Set()); done.WaitOne(60000);
            using (var a = new CalWidget().Snapshot()) using (var b = new UpWidget().Snapshot())
            using (var sheet = new Bitmap(a.Width + b.Width + 60, Math.Max(a.Height, b.Height) + 20))
            using (var g = Graphics.FromImage(sheet)) {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, sheet.Width, sheet.Height), Color.FromArgb(70, 110, 160), Color.FromArgb(150, 90, 120), 30f)) g.FillRectangle(lg, 0, 0, sheet.Width, sheet.Height);
                g.DrawImage(a, 10, 10); g.DrawImage(b, a.Width + 50, 10);
                var tl = new ArrowTab(true); var tr = new ArrowTab(false) { Open = true };   // les flèches « », fermée puis ouverte
                using (var x = tl.Snapshot()) g.DrawImage(x, a.Width + 14, 40);
                using (var x = tr.Snapshot()) g.DrawImage(x, a.Width + 14, 100);
                sheet.Save(args[2], ImageFormat.Png);
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--caltest") {   // test : lit les calendriers configurés et écrit le résultat
            var done = new ManualResetEvent(false);
            Calendars.Refresh(() => done.Set()); done.WaitOne(60000);
            File.WriteAllText(args[1], Calendars.Json(), Encoding.UTF8); return;
        }
        if (args.Length == 2 && args[0] == "--spelldemo") {
            Application.EnableVisualStyles();
            var d = new Companion(false);
            var t1 = new System.Windows.Forms.Timer { Interval = 2500 };
            t1.Tick += (s, e) => { t1.Stop(); d.Spell.Demo(args[1]); };
            var t2 = new System.Windows.Forms.Timer { Interval = 12000 };
            t2.Tick += (s, e) => Application.Exit();
            d.Shown += (s, e) => { t1.Start(); t2.Start(); };
            Application.Run(d); return;
        }
        if (args.Length == 2 && args[0] == "--updtest") {   // test : dernière version sur GitHub + téléchargement (sans installer)
            var sb = new StringBuilder();
            try { Updater.Check(); sb.AppendLine("local=" + Version + " github=" + Updater.Latest + " dispo=" + Updater.Available + " via_gh=" + Updater.ViaGh + " zip=" + Updater.Asset);
                  string x = Updater.Install(true); sb.AppendLine("décompressé : " + string.Join(", ", Array.ConvertAll(Directory.GetFiles(x), Path.GetFileName))); }
            catch (Exception ex) { sb.AppendLine("ERREUR " + ex.Message); }
            File.WriteAllText(args[1], sb.ToString(), Encoding.UTF8); return;
        }
        if (args.Length == 2 && args[0] == "--readytest") { new Companion(false).ReadyPreview(args[1]); return; }
        if (args.Length == 2 && args[0] == "--asktest") { new Companion(false).ReadyPreview(args[1], "question"); return; }
        if (args.Length == 3 && args[0] == "--handtest") { var hc = new Companion(false); hc.handKind = args[2]; hc.ReadyPreview(args[1], "hand"); return; }   // souris | ecran
        if (args.Length == 2 && args[0] == "--runtest") {   // tâches : 2 conversations en parallèle, puis un 2e message dans la même session
            var rc = new Companion(false); var hnd = rc.Handle; var log = new StringBuilder(); int dones = 0; var t0 = DateTime.Now;
            ChatPanel.PostHook = s => {
                if (s.StartsWith("rdone\n")) lock (log) { dones++; log.AppendLine(((int)(DateTime.Now - t0).TotalMilliseconds) + " ms " + s.Replace("\n", " | ")); }
                else if (s.StartsWith("rline\n") && s.Contains("\"type\":\"result\"")) lock (log) log.AppendLine("   result " + Regex.Match(s, "\"result\":\"([^\"]*)\"").Groups[1].Value + " · " + s.Split('\n')[1] + " · pid " + rc.Panel.RunnerPid(s.Split('\n')[1]));
            };
            var tm = new System.Windows.Forms.Timer { Interval = 300 }; int step = 0;
            tm.Tick += (s, e) => {
                if (step == 0) { rc.Panel.TestSend("A", "Reponds juste: un"); rc.Panel.TestSend("B", "Reponds juste: deux"); step = 1; }
                else if (step == 1 && dones >= 2) { rc.Panel.TestSend("A", "Reponds juste: trois"); step = 2; }
                else if (step == 2 && dones >= 3 || (DateTime.Now - t0).TotalSeconds > 150) { tm.Stop(); File.WriteAllText(args[1], log.ToString()); rc.Panel.KillAll(); Application.Exit(); }
            };
            tm.Start(); Application.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--thinktest") { new Companion(false).ThinkPreview(args[1]); return; }
        if (args.Length == 2 && args[0] == "--progtest") { var pc = new Companion(false); pc.Progress(3, 5); pc.ReadyPreview(args[1], "thinking"); return; }
        if (args.Length == 2 && args[0] == "--screentest") { File.WriteAllText(args[1], ChatPanel.ScreenCommand(0) + "\n" + ChatPanel.ScreenDir(), new UTF8Encoding(false)); return; }   // test : commande de capture d écran
        if (args.Length == 3 && args[0] == "--sessdirtest") { File.WriteAllText(args[2], ChatPanel.SessionDir(args[1]) ?? "(introuvable)", new UTF8Encoding(false)); return; }   // test : dossier d origine d une session
        if (args.Length == 3 && args[0] == "--tpltest") {                   // test : analyse d un modèle (Claude écrit PROFIL.md), puis état
            if (Templates.Has(args[1])) {
                var ev = new ManualResetEvent(false); int n = 0;
                Templates.Analyze(args[1], () => { if (Interlocked.Increment(ref n) >= 2) ev.Set(); });
                ev.WaitOne(16 * 60000);
            }
            File.WriteAllText(args[2], new JavaScriptSerializer().Serialize(Templates.Status()) + "\n" + Templates.PersonaLine(), new UTF8Encoding(false)); return;
        }
        if (args.Length == 2 && args[0] == "--preview") { new Companion(false).Preview(args[1]); return; } // planche des poses
        bool first;
        using (new Mutex(true, "ClaudeCompanion_Singleton", out first)) {
            if (!first) { // déjà lancée : on demande à l'instance en cours d'ouvrir le centre de contrôle
                try { EventWaitHandle.OpenExisting("ClaudeCompanion_Open").Set(); } catch { }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("Exception (interface)", e.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("Exception fatale", Convert.ToString(e.ExceptionObject));
            Log("Démarrage", "v" + Version);
            OpenSetup = Array.IndexOf(args, "--setup") >= 0;
            Updater.Init();     // version qui vient d être installée ? (pastille verte)
            Util.FreshPath();   // programmes installés après l ouverture de session (RTK, Node…) : visibles par Bébène et par Claude
            var c = new Companion(Array.IndexOf(args, "--background") < 0);
            StartWatchdog(c);
            Application.Run(c);
        }
    }
}

static class Settings {
    const string Key = @"Software\ClaudeCompanion", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static object Get(string n, object def) {
        using (var k = Registry.CurrentUser.OpenSubKey(Key)) { object v = k == null ? null : k.GetValue(n); return v ?? def; }
    }
    public static string Str(string n, string def) { return Convert.ToString(Get(n, def)); }
    public static int Int(string n, int def) { try { return Convert.ToInt32(Get(n, def)); } catch { return def; } }
    public static bool Bool(string n, bool def) { return Int(n, def ? 1 : 0) == 1; }
    public static void Set(string n, object v) {
        if (v is bool) v = (bool)v ? 1 : 0;
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(n, v);
    }
    public static string Name { get { var s = Str("Name", "Bébène").Trim(); return s.Length > 0 ? s : "Bébène"; } }
    public static bool AutoStart {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ClaudeCompanion") != null; }
        set {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (value) k.SetValue("ClaudeCompanion", "\"" + Application.ExecutablePath + "\" --background");
                else k.DeleteValue("ClaudeCompanion", false);
            }
        }
    }
}

static class Util {
    public static string Short(string p) { return p.Length > 40 ? "…" + p.Substring(p.Length - 39) : p; }
    // Met un argument entre guillemets selon les règles de la ligne de commande Windows
    public static string Quote(string s) {
        var sb = new StringBuilder("\""); int bs = 0;
        foreach (char c in s) {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', bs); sb.Append(c); }
            bs = 0;
        }
        sb.Append('\\', bs * 2); sb.Append('"');
        return sb.ToString();
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    // Verre dépoli Windows 11 (acrylique) derrière toute la fenêtre
    public static void Glass(IntPtr h) {
        try {
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 }; DwmExtendFrameIntoClientArea(h, ref m);
            int v = 3; DwmSetWindowAttribute(h, 38, ref v, 4);   // DWMWA_SYSTEMBACKDROP_TYPE = acrylique
            v = 2; DwmSetWindowAttribute(h, 33, ref v, 4);       // coins arrondis Windows 11 (rayon natif, lissé)
            v = 0x00405A7E; DwmSetWindowAttribute(h, 34, ref v, 4); // un seul contour, orange discret (RGB 126,90,64)
        } catch { }
    }

    // Recharge le PATH depuis le registre (Machine + Utilisateur) : un programme installé après l'ouverture de session
    // (winget install rtk, node…) n'est sinon pas trouvé par Bébène ni par les processus qu'il lance (claude, hooks).
    public static void FreshPath() {
        try {
            string m = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            string u = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            string cur = Environment.GetEnvironmentVariable("Path") ?? "";
            var parts = new List<string>();
            foreach (var p in (m + ";" + u + ";" + cur).Split(';')) {
                string e = Environment.ExpandEnvironmentVariables(p.Trim());
                if (e.Length > 0 && !parts.Exists(x => string.Equals(x.TrimEnd('\\'), e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) parts.Add(e);
            }
            Environment.SetEnvironmentVariable("Path", string.Join(";", parts.ToArray()));
        } catch { }
    }

    public static void DarkWindow(IntPtr h, bool rounded) {
        int v = 1; DwmSetWindowAttribute(h, 20, ref v, 4);                 // barre de titre sombre
        v = 0x001B1818; DwmSetWindowAttribute(h, 35, ref v, 4);            // couleur de la barre de titre
        if (rounded) { v = 2; DwmSetWindowAttribute(h, 33, ref v, 4); v = 0x00403A3A; DwmSetWindowAttribute(h, 34, ref v, 4); }
    }
    // un seul environnement WebView2 pour toutes les fenêtres (un seul navigateur en arrière-plan), sans les services
    // inutiles pour des pages locales : mises à jour de composants (DRM vidéo, listes de filtrage… ~45 Mo), synchro, traduction
    static System.Threading.Tasks.Task<CoreWebView2Environment> webEnv;
    static System.Threading.Tasks.Task<CoreWebView2Environment> WebEnv() {
        if (webEnv != null) return webEnv;
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion");
        foreach (var junk in new[] { "WidevineCdm", "component_crx_cache", "Subresource Filter", "OnDeviceHeadSuggestModel", "ZxcvbnData", "hyphen-data" }) {
            string d = Path.Combine(data, "EBWebView", junk);
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }     // déjà téléchargés par une ancienne version : on fait le ménage
        }
        var opt = new CoreWebView2EnvironmentOptions("--disable-component-update --disable-background-networking --disable-sync "
            + "--disable-features=Translate,msSmartScreenProtection,AutofillServerCommunication,msEdgeShoppingUI,msWebOOUI");
        return webEnv = CoreWebView2Environment.CreateAsync(null, data, opt);
    }
    // fenêtre cachée : WebView2 libère ce qu'il peut (caches, mémoire du rendu) ; la page continue de recevoir les messages
    public static void WebMemory(WebView2 web, bool visible) {
        try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.MemoryUsageTargetLevel = visible ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low; }
        catch { }
    }

    public static async void InitWeb(WebView2 web, string page, EventHandler<CoreWebView2WebMessageReceivedEventArgs> onMsg) {
        try {
            var env = await WebEnv();
            await web.EnsureCoreWebView2Async(env);
            var cw = web.CoreWebView2;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.WebMessageReceived += onMsg;
            cw.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
            cw.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("file:")) { e.Cancel = true; OpenExternal(e.Uri); } };
            cw.Navigate(new Uri(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), page)).AbsoluteUri);
        } catch (Exception ex) {
            Program.Log("WebView2", ex.ToString());
            // PC sans le composant WebView2 (rare, Windows 10 ancien) : sans lui, ni la discussion ni l'assistant ne s'affichent
            if (MessageBox.Show("Le composant d'affichage WebView2 n'a pas pu démarrer :\n" + ex.Message + "\n\nL'installer maintenant ? (gratuit, Microsoft)", "Bébène",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); } catch { }
        }
    }
    static void OpenExternal(string uri) { if (uri.StartsWith("http")) try { Process.Start(uri); } catch { } }
}

class DarkColors : ProfessionalColorTable {
    static readonly Color Bg = Color.FromArgb(31, 31, 35), Hi = Color.FromArgb(217, 119, 87), Line = Color.FromArgb(58, 58, 64);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Line; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color SeparatorDark { get { return Line; } }
    public override Color SeparatorLight { get { return Bg; } }
    public override Color CheckBackground { get { return Line; } }
    public override Color CheckSelectedBackground { get { return Hi; } }
}

// ---------- Bébène : chat noir en pixel art, animé de façon procédurale ----------
class Pose {
    public float bx, by, bw, bh, ba, hx, hy, ht, eye, eyeShift, haunch;
    public float fhx, fhy, ffx, ffy, ffx2, ffy2, bhx, bhy, fbx, fby, fbx2, fby2;
    public float tbx, tby, ta, tc;
    public float tf; // queue devant le corps (en boule)
    static readonly System.Reflection.FieldInfo[] F = typeof(Pose).GetFields();
    public Pose(params float[] v) { for (int i = 0; i < v.Length && i < F.Length; i++) F[i].SetValue(this, v[i]); }
    public Pose Clone() { return (Pose)MemberwiseClone(); }
    public void Lerp(Pose o, float k) { foreach (var f in F) { float a = (float)f.GetValue(this); f.SetValue(this, a + ((float)f.GetValue(o) - a) * k); } }
}

class Companion : Form {
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point pos, ref Size sz, IntPtr src, ref Point srcPos, int key, ref BLEND b, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    // Pinceaux et stylos du dessin, créés une seule fois (avant : ~40 objets GDI+ créés puis détruits à chaque image, 30 fois par seconde).
    // Uniquement sur le fil de l'interface, et jamais dans un using (ils sont partagés).
    static readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
    static readonly Dictionary<long, Pen> pens = new Dictionary<long, Pen>();
    static SolidBrush Br(Color c) {
        SolidBrush b; int k = c.ToArgb();
        if (!brushes.TryGetValue(k, out b)) brushes[k] = b = new SolidBrush(c);
        return b;
    }
    static Pen Pn(Color c, float w, bool round = false) {
        Pen p; long k = ((long)c.ToArgb() << 32) ^ ((long)BitConverter.ToInt32(BitConverter.GetBytes(w), 0) & 0x7fffffff) ^ (round ? 1L << 31 : 0);
        if (!pens.TryGetValue(k, out p)) { p = new Pen(c, w); if (round) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; } pens[k] = p; }
        return p;
    }

    // Toile en "vrais" pixels, agrandie sans lissage (Px = taille d'un pixel à l'écran)
    const int CW = 64, CH = 52;
    //                           corps                 tête+yeux+cuisse            patte av. (hanche, pied, pied loin)   patte arr.               queue
    static readonly Pose SIT     = new Pose(31, 40, 15, 18, 0,   32, 25, 0, 1, 0, 8,      33, 36, 34, 50, 30, 50,   27, 44, 26, 50, 37, 50,   25, 48, 180, 12);
    static readonly Pose STAND   = new Pose(30, 36, 26, 11, 0,   44, 27, 0, 1, 0.6f, 0,   38, 39, 39, 50, 36, 50,   22, 39, 22, 50, 19, 50,   17, 33, 230, 6);
    static readonly Pose LIE     = new Pose(30, 45, 28, 10, 0,   43, 40, 8, 0, 1, 6,      40, 47, 48, 50, 46, 50,   22, 47, 24, 50, 21, 50,   17, 47, 185, -2);
    static readonly Pose GROOM   = new Pose(31, 40, 15, 18, 0,   31, 26, -12, 0, 0, 8,    33, 36, 36, 31, 30, 50,   27, 44, 26, 50, 37, 50,   25, 48, 180, 12);
    static readonly Pose LICK    = new Pose(31, 40, 15, 18, 0,  25, 33, -38, 0, 0, 8,    33, 36, 34, 50, 30, 50,   27, 44, 26, 50, 37, 50,   25, 48, 180, 12, 0);   // tête tournée vers le flanc : toilette du corps
    static readonly Pose SCRATCH = new Pose(31, 40, 15, 18, 0,  32, 26, 14, 0, 0, 6,     33, 36, 34, 50, 30, 50,   30, 40, 47, 20, 37, 50,   25, 48, 180, 12, 0);   // patte arrière levée derrière l oreille
    static readonly Pose STRETCH = new Pose(30, 40, 28, 10, 12,  46, 38, 10, 1, 0.6f, 0,  40, 43, 50, 50, 47, 50,   21, 35, 21, 50, 18, 50,   17, 31, 250, 2);
    static readonly Pose HANG    = new Pose(32, 30, 12, 20, 0,   32, 14, 0, 1, 0, 0,      33, 25, 34, 36, 30, 36,   32, 38, 35, 46, 29, 46,   32, 40, 95, 3);
    static readonly Pose FALL    = new Pose(32, 30, 12, 20, 0,   32, 14, 0, 1, 0, 0,      33, 25, 40, 34, 24, 34,   32, 38, 40, 46, 24, 46,   32, 40, 270, 4);

    // positions de sommeil
    static readonly Pose CURL    = new Pose(31, 46, 24, 13, 0,  40, 43, 25, 0, 0, 0,      36, 47, 38, 50, 35, 50,   24, 47, 22, 50, 26, 50,   20, 50, 10, -10, 1);
    static readonly Pose SPRAWL  = new Pose(30, 46, 30, 9, 0,   46, 43, 50, 0, 0, 0,      38, 46, 50, 47, 49, 49,   22, 46, 12, 47, 11, 49,   17, 46, 170, 3, 0);
    static readonly Pose LOAF    = new Pose(30, 44, 26, 13, 0,  41, 36, 0, 0, 0.5f, 0,    36, 47, 37, 50, 39, 50,   24, 47, 25, 50, 22, 50,   22, 50, 0, -2, 1);
    static readonly Pose[] SLEEPS = { LIE, CURL, SPRAWL, LOAF };
    Pose sleepPose = LIE;
    float peekT; Point peekCur;                                    // œil entrouvert pendant le sommeil

    static readonly Color FUR = Color.FromArgb(255, 24, 24, 32), FAR = Color.FromArgb(255, 13, 13, 19), HI = Color.FromArgb(255, 58, 60, 88),
        OUT = Color.FromArgb(255, 48, 50, 80), EYE = Color.FromArgb(255, 112, 214, 96), EYEDK = Color.FromArgb(255, 46, 140, 64),
        INNER = Color.FromArgb(255, 112, 58, 80), NOSE = Color.FromArgb(255, 236, 140, 160), COLLAR = Color.FromArgb(255, 217, 96, 70),
        BELL = Color.FromArgb(255, 250, 210, 90), WHISK = Color.FromArgb(255, 118, 120, 150), HEART = Color.FromArgb(255, 238, 92, 116);

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Random rnd = new Random();
    public readonly ChatPanel Panel;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem showItem;
    SettingsWindow settingsWin;
    public string SessionId = "";

    int Px = 3, W = CW * 3, H = CH * 3, scalePct = 100;
    string state = "idle", act = "sit";
    float t, happyT, actT, actDur = 4, targetX, vy, phase, walkAmp, landT, jumpT = -1, petT, heartT, lookX, lookY, earBack;
    int face = 1, blink, earTw;
    bool down, moved, held, falling, run;
    Point grab;
    DateTime lastTouch = DateTime.Now;
    Pose cur = SIT.Clone();
    readonly float[] tang = new float[9];

    public Companion(bool openSettings) {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = Settings.Bool("TopMost", true);
        SetScale(Settings.Int("Scale", 100));
        var wa = Screen.PrimaryScreen.WorkingArea;
        int x = Settings.Int("X", wa.Right - W - 40);
        if (x < SystemInformation.VirtualScreen.Left || x > SystemInformation.VirtualScreen.Right - W) x = wa.Right - W - 40;
        Location = new Point(x, wa.Bottom - 51 * Px);
        for (int i = 0; i < tang.Length; i++) tang[i] = SIT.ta + SIT.tc * i;

        Panel = new ChatPanel(this);
        Spell = new SpellAssist(this);
        var notesT = new System.Windows.Forms.Timer { Interval = 1500 };                    // post-it : chargés juste après le démarrage
        notesT.Tick += (s, e) => {
            notesT.Stop(); notesT.Dispose();
            Notes.Changed += () => Panel.Post("notes\n" + Notes.Json());
            Notes.Init(this);
            Panel.VisibleChanged += (s2, e2) => Notes.Refresh();
        };
        notesT.Start();
        remindTimer.Tick += (s, e) => CheckReminders(); remindTimer.Start();
        spellItem = new ToolStripMenuItem("Correcteur d'orthographe") { Checked = Settings.Bool("SpellCheck", false) };
        spellItem.Click += (s, e) => SetSpell(!spellItem.Checked);

        var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Color.White, Font = new Font("Segoe UI", 10f), ShowImageMargin = false, Padding = new Padding(4) };
        showItem = new ToolStripMenuItem("Afficher le chat sur le bureau") { Checked = Settings.Bool("ShowCat", true) };
        showItem.Click += (s, e) => SetCatVisible(!showItem.Checked);
        openItem = new ToolStripMenuItem("Ouvrir la discussion", null, (s, e) => ShowPanel());
        m.Items.Add(openItem);
        RegisterHotkeys();                                       // affiche le raccourci dans le menu (si la fenêtre existe déjà)
        m.Items.Add(new ToolStripMenuItem("Nouvelle conversation", null, (s, e) => { ShowPanel(); Panel.Post("new"); }));
        m.Items.Add(new ToolStripMenuItem("Ouvrir Claude Code ici", null, (s, e) => OpenTerminal(false)));
        m.Items.Add(new ToolStripMenuItem("Nouveau post-it", null, (s, e) => Notes.New()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(showItem);
        m.Items.Add(spellItem);
        m.Items.Add(new ToolStripMenuItem("Réglages…", null, (s, e) => ShowSettings()));
        m.Items.Add(new ToolStripMenuItem("Quitter", null, (s, e) => Quit()));
        ContextMenuStrip = m;

        tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath), Text = "Bébène", ContextMenuStrip = m, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        tray.BalloonTipClicked += (s, e) => { ShowPanel(); if (!string.IsNullOrEmpty(toastKey)) Panel.Post("openconv\n" + toastKey); };   // notification → la bonne conversation
        ExportAvatar();

        AllowDrop = true;
        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) => {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) { ShowPanel(); Panel.Post("paths\n" + string.Join("\n", files)); }
        };

        timer.Tick += (s, e) => { Tick(); if (Visible) Draw(); };
        VisibleChanged += (s, e) => ForceRedraw();                         // réaffiché : on renvoie l image même si elle n a pas changé
        timer.Start();

        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudeCompanion_Open");
        new Thread(() => { while (true) { ev.WaitOne(); try { BeginInvoke((Action)ShowSettings); } catch { } } }) { IsBackground = true }.Start();

        Load += (s, e) => BeginInvoke((Action)(() => {
            if (!Settings.Bool("ShowCat", true)) Hide();
            // premier lancement sur un PC pas encore prêt (lancé depuis le zip, ou pas de compte Claude) : l'assistant s'ouvre tout seul
            bool notReady = !Setup.InstalledHere || !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".claude\.credentials.json"));
            if (Program.OpenSetup || notReady) ShowSettings("setup");
            else if (openSettings) ShowSettings();
        }));
    }

    void SetScale(int pct) {
        scalePct = Math.Max(60, Math.Min(160, pct));
        Px = scalePct <= 80 ? 2 : scalePct >= 120 ? 4 : 3;
        W = CW * Px; H = CH * Px;
        Size = new Size(W, H);
    }

    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }
    // Raccourcis configurables (réglages > Système) : texte « Ctrl+Alt+Shift+Win+Touche » (nom de touche .NET : A, D1, F5, Space…), vide = aucun
    public static readonly string[] HotkeyDefaults = { "Alt+Space", "Ctrl+Alt+C" };
    public bool[] HotkeyOk = new bool[2];
    ToolStripMenuItem openItem;
    public static bool ParseHotkey(string s, out uint mod, out uint vk) {
        mod = 0; vk = 0;
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var part in s.Split('+')) {
            switch (part) {
                case "Ctrl": mod |= 2; break;
                case "Alt": mod |= 1; break;
                case "Shift": mod |= 4; break;
                case "Win": mod |= 8; break;
                default:
                    Keys k;
                    if (vk != 0 || !Regex.IsMatch(part, "^([A-Z]|D[0-9]|F([1-9]|1[0-9]|2[0-4])|Space|Oem[A-Za-z0-9]+|Insert|Home|End|PageUp|Next|Pause|NumPad[0-9])$")
                        || !Enum.TryParse(part, out k)) return false;
                    vk = (uint)k; break;
            }
        }
        // une touche, et un modificateur obligatoire sauf pour les touches de fonction
        return vk != 0 && (mod != 0 || (vk >= (uint)Keys.F1 && vk <= (uint)Keys.F24));
    }
    public static string HotkeyLabel(string s) {
        if (string.IsNullOrEmpty(s)) return "";
        return Regex.Replace(s.Replace("Space", "Espace"), @"\bD([0-9])\b", "$1");
    }
    public void RegisterHotkeys() {
        if (!IsHandleCreated) return;
        var labels = new List<string>();
        for (int i = 0; i < 2; i++) {
            UnregisterHotKey(Handle, i + 1);
            uint mod, vk;
            string s = Settings.Str("Hotkey" + (i + 1), HotkeyDefaults[i]);
            HotkeyOk[i] = ParseHotkey(s, out mod, out vk) && RegisterHotKey(Handle, i + 1, mod | 0x4000, vk);
            if (HotkeyOk[i]) labels.Add(HotkeyLabel(s));
        }
        if (openItem != null) openItem.ShortcutKeyDisplayString = labels.Count > 0 ? labels[0] : "";
    }
    // pendant qu on tape une nouvelle combinaison dans les réglages, les raccourcis actuels ne doivent pas l intercepter
    public void PauseHotkeys() { if (IsHandleCreated) { UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); } }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x0312) TogglePanel(); base.WndProc(ref m); }

    public string State { get { return state; } }
    float readyT, askT;
    // "hand" : une tâche a besoin de ta souris / clavier ("souris") ou de voir une fenêtre ("ecran") → il tapote de la patte, bulle avec l icône
    internal string handKind = "souris"; int progDone, progTotal;
    internal Rectangle thinkTo = Rectangle.Empty;                          // tests : fenêtre de discussion simulée (bulle de réflexion orientée vers elle)
    public void SetHand(string kind) { handKind = kind == "ecran" ? "ecran" : "souris"; if (state != "hand") { SetState("hand"); jumpT = 0; } }
    public void Progress(int done, int total) { progDone = done; progTotal = total; }
    // notification Windows (tâche finie, question, accord, besoin de toi) ; un clic ouvre la conversation concernée
    string toastKey;
    public void Notify(string kind, string title, string key) {
        string head = kind == "ask" ? "❓ Une question pour toi" : kind == "perm" ? "🔐 Accord demandé" : kind == "hand" ? "🐾 " + Settings.Name + " a besoin de toi" : kind == "pause" ? "⏸ Tâche en pause : quota presque épuisé" : "✅ Tâche terminée";
        toastKey = key;
        try { tray.ShowBalloonTip(6000, head, string.IsNullOrEmpty(title) ? "Conversation" : (title.Length > 120 ? title.Substring(0, 120) + "…" : title), ToolTipIcon.None); } catch { }
    }
    // "ready" : la réponse est arrivée pendant que la discussion était fermée → Bébène, content, attend qu'on l'ouvre
    public void SetState(string s) {
        state = s; happyT = 0; readyT = 0; askT = 0;
        if (s == "happy" || s == "ready") { jumpT = 0; heartT = 1.4f; }
    }

    public void Preview(string path) {
        tray.Visible = false;
        var poses = new[] { SIT, STAND, GROOM, STRETCH, LIE, CURL, SPRAWL, LOAF, SCRATCH, SIT, STRETCH, LICK, CURL, LOAF };
        var acts = new[] { "sit", "walk", "groom", "stretch", "lie", "lie", "lie", "lie", "scratch", "rub", "stretch", "lick", "lie", "lie" };
        var times = new[] { 0f, 0f, 0f, 0.15f, 0f, 0f, 0f, 0f, 0f, 0f, 0.5f, 0f, 0f, 0f };   // 2 moments de l'étirement : début et bâillement
        using (var sheet = new Bitmap(W * 4, H * 4))
        using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(255, 40, 44, 52));
            for (int i = 0; i < poses.Length; i++) {
                act = acts[i]; actDur = 2f; actT = times[i] * actDur; petT = act == "rub" ? 0.5f : 0f; t = 0.3f;
                peekT = i >= 12 ? 1f : 0f;                                     // 2 dernières : œil méfiant entrouvert
                for (int k = 0; k < tang.Length; k++) tang[k] = poses[i].ta + poses[i].tc * k;
                var p = poses[i].Clone();
                if (act == "rub") { p.ht += 12; p.hx += 1.3f; p.ba += 3.5f; }
                using (var b = RenderSmall(p, 1, 0, true)) g.DrawImage(b, (i % 4) * W, (i / 4) * H);
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }

    // test : Bébène content qui attend qu on lise sa réponse (deux sens + avec cœur)
    public void ReadyPreview(string path, string st = "ready") {
        tray.Visible = false; state = st; act = "sit"; t = 0.3f;
        using (var sheet = new Bitmap(W * 4, H)) using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(255, 40, 44, 52));
            for (int i = 0; i < 4; i++) {
                heartT = i >= 2 && st == "ready" ? 0.9f : 0; t = 0.3f + i * (st == "ready" ? 0.4f : 0.9f); held = i == 3;   // 4e : porté par la peau du cou
                var pose = held ? HANG : SIT;
                for (int k = 0; k < tang.Length; k++) tang[k] = pose.ta + pose.tc * k;
                using (var b = RenderSmall(pose.Clone(), i == 1 ? -1 : 1, i == 2 ? 6 : 0, true)) g.DrawImage(b, i * W, 0);
            }
            held = false;
            sheet.Save(path, ImageFormat.Png);
        }
    }

    // test : bulle de réflexion orientée vers la discussion (en haut à gauche, en haut à droite, juste au-dessus, discussion fermée)
    public void ThinkPreview(string path) {
        tray.Visible = false; state = "thinking"; act = "sit";
        using (var sheet = new Bitmap(W * 4, H)) using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(255, 40, 44, 52));
            var rects = new[] { new Rectangle(Left - 700, Top - 700, 460, 660), new Rectangle(Left + W + 240, Top - 700, 460, 660), new Rectangle(Left - 200, Top - 700, 460, 640), Rectangle.Empty };
            for (int i = 0; i < 4; i++) {
                t = 1.1f + i * 4 / 3f; thinkTo = rects[i];
                for (int k = 0; k < tang.Length; k++) tang[k] = SIT.ta + SIT.tc * k;
                using (var b = RenderSmall(SIT.Clone(), i == 0 ? -1 : 1, 0, true)) g.DrawImage(b, i * W, 0);
            }
            thinkTo = Rectangle.Empty;
            sheet.Save(path, ImageFormat.Png);
        }
    }

    void SetAct(string a, float dur) { act = a; actT = 0; actDur = dur; }
    void Sleep(float dur) { sleepPose = SLEEPS[rnd.Next(SLEEPS.Length)]; SetAct("lie", dur); }

    // ---- Comportement ----
    void Next(Rectangle wa) {
        var c = Cursor.Position;
        bool near = Math.Abs(c.X - (Left + W / 2)) < 450 && c.Y > wa.Bottom - 260;
        double idle = (DateTime.Now - lastTouch).TotalMinutes;
        // curseur d activité (réglages) : 0 = bouge à peine et dort beaucoup quand on ne s occupe pas de lui, 100 = très remuant
        float a = Settings.Int("Activity", 50) / 100f;
        float move = 0.08f + 1.84f * a, lazy = 1.6f - 1.2f * a;              // a = 0,5 → comportement d origine (1 et 1)
        if (idle > 0.5 + 5 * a && rnd.Next(100) < 85 - 60 * a) { Sleep((25 + rnd.Next(40)) * lazy); return; }
        float[] w = { near ? 14 * move : 0, 30 * move, 8, 8, Math.Max(3, 15 * (2.2f - 2.4f * a)), 7, 6, 5, 7 * lazy };
        float sum = 0; foreach (var x in w) sum += x;
        float r = (float)rnd.NextDouble() * sum; int k = 0;
        while (k < w.Length - 1 && r >= w[k]) { r -= w[k]; k++; }
        switch (k) {
            case 0: SetAct("chase", 6); return;
            case 1: {                                                                     // peu actif : petits trajets seulement
                int reach = (int)(120 + 1600 * a), lo = Math.Max(wa.Left, Left - reach), hi = Math.Min(wa.Right - W, Left + reach);
                targetX = rnd.Next(lo, Math.Max(lo + 1, hi)); } SetAct(rnd.Next(5) == 0 && a > 0.2f ? "run" : "walk", 99); return;
            case 2: SetAct("groom", 3 + rnd.Next(4)); return;                             // toilette du visage
            case 3: SetAct("lick", 3 + rnd.Next(4)); return;                              // toilette du corps
            case 4: Sleep((12 + rnd.Next(25)) * lazy); return;
            case 5: SetAct("stretch", 2.4f); return;
            case 6: SetAct("scratch", 1.6f + (float)rnd.NextDouble() * 1.5f); return;   // se gratte derrière l'oreille
            case 7: SetAct("rub", 3 + rnd.Next(3)); return;                               // se frotte en ronronnant
        }
        SetAct("sit", (3 + rnd.Next(6)) * lazy);
    }

    // ---- fenêtre que l'utilisateur utilise (hors Bébène), pour que Claude sache de quel fichier on parle ----
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static string LastWindow = ""; public static long LastHwnd;
    int fgTick;
    void TrackForeground() {
        if (++fgTick % 30 != 0) return;                                     // ~1 fois par seconde
        var h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
        if (h == IntPtr.Zero || pid == Program.Pid) return;
        var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
        string tt = sb.ToString().Trim();
        if (tt.Length > 0 && tt != "Program Manager") { LastWindow = tt; LastHwnd = h.ToInt64(); }
    }

    void Tick() {
        const float dt = 0.033f;
        t += dt; actT += dt;
        TrackForeground();
        if (blink > 0) blink--; else if (rnd.Next(110) == 0) blink = 4;
        if (earTw > 0) earTw--; else if (rnd.Next(160) == 0) earTw = 5;
        if (landT > 0) landT -= dt;
        if (jumpT >= 0) { jumpT += dt; if (jumpT > 1.6f) jumpT = -1; }
        if (petT > 0) petT -= dt;
        if (heartT > 0) heartT -= dt;
        if (state == "happy") { happyT += dt; if (happyT > 1.6f) state = "idle"; }
        if (state == "ready") { readyT += dt; if (readyT > 3.5f) { readyT = 0; jumpT = 0; heartT = 1.4f; } }   // petit saut + cœur toutes les 3,5 s
        // "question" : Claude attend une info capitale → Bébène se gratte la tête, regarde à gauche puis à droite, oreilles qui bougent
        if (state == "question") { askT += dt; if (askT > 4.5f) { askT = 0; if (!held && !falling) SetAct("scratch", 1.1f); earTw = 5; } }

        var wa = Screen.FromPoint(new Point(Left + W / 2, Top + H / 2)).WorkingArea;
        int groundTop = wa.Bottom - 51 * Px;
        bool busy = state == "thinking" || state == "ready" || state == "question" || state == "hand" || Panel.Visible;
        if (state == "hand") { askT += dt; if (askT > 5f) { askT = 0; if (!held && !falling) jumpT = 0; earTw = 5; } }   // besoin de toi : petit saut toutes les 5 s
        Pose tgt = SIT; int dx = 0;

        if (held) tgt = HANG;
        else if (falling) {
            vy += 0.9f * Px; int ny = Top + (int)vy;
            if (ny >= groundTop) { ny = groundTop; falling = false; vy = 0; landT = 0.25f; SetAct("sit", 3); }
            Top = ny; tgt = FALL;
        } else {
            if (Top != groundTop) Top = groundTop;
            if (!held && (Left < wa.Left - W / 2 || Left > wa.Right - W / 2)) Left = Math.Max(wa.Left, Math.Min(wa.Right - W, Left)); // écran débranché / résolution changée
            if (busy && act != "sit" && act != "groom" && act != "lick" && !(state == "question" && act == "scratch")) SetAct("sit", 2);
            if (Panel.Visible) face = Panel.Left < Left ? -1 : 1;
            switch (act) {
                case "groom": tgt = GROOM; if (actT > actDur) SetAct("sit", 2 + rnd.Next(4)); break;
                case "scratch": tgt = SCRATCH; if (actT > actDur) SetAct("sit", 2 + rnd.Next(4)); break;
                case "lick": tgt = LICK; if (actT > actDur) SetAct("sit", 2 + rnd.Next(4)); break;
                case "rub":
                    tgt = SIT;
                    if (heartT <= 0 && rnd.Next(70) == 0) heartT = 1.4f;               // un petit cœur de temps en temps
                    if (actT > actDur && petT <= 0) SetAct("sit", 2 + rnd.Next(4));
                    break;
                case "lie":
                    tgt = sleepPose;
                    if (actT > 5 && rnd.Next(450) == 0 && peekT <= 0) sleepPose = SLEEPS[rnd.Next(SLEEPS.Length)]; // change de position en dormant
                    // méfiance : en boule ou en loaf, il entrouvre un œil pour voir ce que fait l utilisateur (plus souvent si la souris bouge)
                    if (peekT > 0) peekT -= dt;
                    else if ((sleepPose == CURL || sleepPose == LOAF) && actT > 3) {
                        var cpk = Cursor.Position; bool moving = Math.Abs(cpk.X - peekCur.X) + Math.Abs(cpk.Y - peekCur.Y) > 25; peekCur = cpk;
                        if (rnd.Next(moving ? 90 : 700) == 0) peekT = 1.6f + (float)rnd.NextDouble() * 1.4f;
                    }
                    if (!busy && actT > actDur) SetAct("stretch", 1.8f); break;
                case "stretch": tgt = STRETCH; if (actT > actDur) SetAct("sit", 2 + rnd.Next(4)); break;
                case "walk": case "run": case "chase":
                    tgt = STAND;
                    run = act != "walk";
                    if (act == "chase") targetX = Cursor.Position.X - W / 2;
                    targetX = Math.Max(wa.Left, Math.Min(wa.Right - W, targetX));
                    float sp = (run ? 4.2f : 1.6f) * Px / 3f, d = targetX - Left;
                    if (Math.Abs(d) <= sp * 2) SetAct("sit", 3 + rnd.Next(5));
                    else { face = d > 0 ? 1 : -1; dx = (int)Math.Round(face * sp); if (dx == 0) dx = face; Left += dx; }
                    if (act == "chase" && actT > 6) SetAct("sit", 3);
                    break;
                default: tgt = SIT; if (!busy && actT > actDur) Next(wa); break;
            }
        }

        phase += Math.Abs(dx) / (6f * Px) * (float)Math.PI;
        walkAmp += ((dx != 0 ? 1f : 0f) - walkAmp) * 0.25f;
        cur.Lerp(tgt, held || falling ? 0.4f : 0.12f);

        // regard vers la souris
        var cp = Cursor.Position;
        float hsx = Left + (face > 0 ? cur.hx : CW - cur.hx) * Px, hsy = Top + cur.hy * Px;
        float lx = Math.Max(-0.9f, Math.Min(0.9f, (cp.X - hsx) / 150f)) * face, ly = Math.Max(-1f, Math.Min(1f, (cp.Y - hsy) / 150f));
        if (state == "question" && !held) { lx = (float)Math.Sin(t * 1.4f) * 0.9f; ly = -0.7f; }   // perplexe : regarde en l air, d un côté puis de l autre
        lookX += (lx - lookX) * 0.2f; lookY += (ly - lookY) * 0.2f;
        earBack += (((held || falling) ? 2.5f : petT > 0 ? 1.2f : 0f) - earBack) * 0.2f;

        // ---- physique quand on le tient par la peau du cou : il pend comme un pendule ----
        // la main accélère d'un côté → le corps part de l'autre (inertie), puis revient (pesanteur), avec amortissement.
        // secousse verticale → le corps s'étire / se tasse.
        {
            var hand = FakeHand.HasValue ? FakeHand.Value : Cursor.Position;
            if (held) {
                float vx = (hand.X - prevHand.X) / (float)Px, vy2 = (hand.Y - prevHand.Y) / (float)Px;
                float ax = vx - prevVx, ay = vy2 - prevVy; prevVx = vx; prevVy = vy2;
                swingV += ax * 1.15f;
                stretchV += -ay * 0.35f;
            } else { prevVx = prevVy = 0; }
            prevHand = hand;
            swingV += -swing * (held ? 0.11f : 0.2f); swingV *= held ? 0.86f : 0.75f;
            swing = Math.Max(-50f, Math.Min(50f, swing + swingV));
            stretchV += -stretch * 0.25f; stretchV *= 0.78f;
            stretch = Math.Max(-3.5f, Math.Min(5f, stretch + stretchV));
        }

        // queue : chaîne avec inertie
        float amp = state == "thinking" ? 24 : state == "ready" ? 20 : state == "question" || state == "hand" ? 14 : act == "lie" ? 4 : held ? 16 : walkAmp > 0.3f ? 7 : 10;
        float freq = state == "thinking" ? 6 : state == "ready" ? 4.5f : 1.8f;
        for (int i = 0; i < tang.Length; i++) {
            float target = cur.ta + cur.tc * i + amp * (float)Math.Sin(t * freq - i * 0.55f) * (i + 1) / tang.Length;
            tang[i] += (target - tang[i]) * Math.Max(0.06f, 0.3f - i * 0.025f);
        }
    }

    // ---- Dessin ----
    // le chat est souvent immobile (assis, endormi) : une image identique a la precedente n est pas renvoyee a l ecran
    byte[] lastFrame, curFrame;
    public void ForceRedraw() { lastFrame = null; }
    void Draw() {
        using (var img = BuildImage()) {
            var bd = img.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);   // prémultiplié = format de UpdateLayeredWindow
            int n = Math.Abs(bd.Stride) * img.Height;
            if (curFrame == null || curFrame.Length != n) curFrame = new byte[n];
            Marshal.Copy(bd.Scan0, curFrame, 0, n);
            img.UnlockBits(bd);
            if (lastFrame != null && lastFrame.Length == n && SameBytes(lastFrame, curFrame)) return;
            var tmp = lastFrame; lastFrame = curFrame; curFrame = tmp;
            Push(lastFrame, img.Width, img.Height);
        }
    }
    static unsafe bool SameBytes(byte[] a, byte[] b) {
        fixed (byte* pa = a, pb = b) {
            long* la = (long*)pa, lb = (long*)pb; int n = a.Length / 8;
            for (int i = 0; i < n; i++) if (la[i] != lb[i]) return false;
            for (int i = n * 8; i < a.Length; i++) if (pa[i] != pb[i]) return false;
        }
        return true;
    }
    Bitmap BuildImage() {
        var p = cur.Clone();
        float br = (float)Math.Sin(t * (act == "lie" ? 1.6 : 2.4)) * 0.6f;
        p.bh += br; p.by -= br / 2;
        if (landT > 0) { p.bh *= 0.85f; p.bw *= 1.12f; p.by += 1.5f; p.hy += 2; }
        if (walkAmp > 0.01f) {
            float A = (run ? 5f : 3.5f) * walkAmp, L = 2.5f * walkAmp;
            Step(ref p.ffx, ref p.ffy, phase, A, L); Step(ref p.ffx2, ref p.ffy2, phase + Math.PI, A, L);
            Step(ref p.fbx, ref p.fby, phase + Math.PI, A, L); Step(ref p.fbx2, ref p.fby2, phase, A, L);
            float bob = (float)Math.Abs(Math.Sin(phase)) * 0.9f * walkAmp;
            p.by -= bob; p.hy -= bob * 0.7f; p.fhy -= bob; p.bhy -= bob;
            if (run) p.ba += (float)Math.Sin(phase) * 4;
        }
        if (act == "groom" && !held && !falling) { p.ffy += (float)Math.Sin(t * 11) * 1.3f; p.ht += (float)Math.Sin(t * 11) * 3; }
        if (act == "lick" && !held && !falling) {                          // coups de langue sur le flanc : la tête bouge par à-coups
            float lk = (float)Math.Sin(t * 9); p.hy += Math.Max(0, lk) * 1.2f; p.ht += lk * 5; p.hx += lk * 0.5f;
        }
        if (act == "scratch" && !held && !falling) {                       // grattage rapide derrière l oreille
            float sc = (float)Math.Sin(t * 22); p.fbx += sc * 1.3f; p.fby += (float)Math.Cos(t * 22) * 1.3f; p.ht += sc * 2.5f;
        }
        if (act == "rub" && !held && !falling) {                           // se frotte : la tête et le corps se balancent doucement
            float rb = (float)Math.Sin(t * 3.2); p.ht += rb * 14; p.hx += rb * 1.5f; p.ba += rb * 4; p.eyeShift += rb * 0.6f;
        }
        if (state == "hand" && !held && !falling && act != "lie") {                 // tapote de la patte avant : « hé, j ai besoin de toi »
            float k = Math.Max(0f, (float)Math.Sin(t * 6)); p.ffy -= 4 * k; p.ffx += 1.5f * k;
        }
        float jumpY = jumpT >= 0 && jumpT < 0.5f ? (float)Math.Sin(jumpT / 0.5f * Math.PI) * 9 : 0;
        if (held || falling || Math.Abs(stretch) > 0.05f || Math.Abs(swingV) > 0.05f) {
            // étirement du corps et pattes qui traînent derrière le mouvement (inertie secondaire)
            p.bh += stretch; p.by += stretch * 0.5f; p.fby += stretch; p.fby2 += stretch; p.ffy += stretch * 0.6f; p.ffy2 += stretch * 0.6f;
            float lag = Math.Max(-3f, Math.Min(3f, -swingV * 0.35f)) * face;
            p.ffx += lag; p.ffx2 += lag; p.fbx += lag * 1.4f; p.fbx2 += lag * 1.4f;
        }

        return RenderSmall(p, face, jumpY, true);
    }

    // inclinaison autour de la main (peau du cou), puis miroir, puis saut
    float swing, swingV, stretch, stretchV, prevVx, prevVy;
    Point prevHand; public Point? FakeHand;   // FakeHand : tests uniquement

    // test : on tient le chat, la main file vers la droite puis s'arrête → planche d'images
    // mesure : temps moyen pour calculer + dessiner une image du chat (sans l envoyer à l écran), par activité
    public void Bench(string path) {
        tray.Visible = false; var sb = new StringBuilder(); var sw = new System.Diagnostics.Stopwatch();
        foreach (var a in new[] { "sit", "walk", "lie", "groom" }) {
            SetAct(a, 999); if (a == "walk") targetX = Left - 2000;
            for (int i = 0; i < 30; i++) { Tick(); using (BuildImage()) { } }
            int gc = GC.CollectionCount(0); sw.Restart();
            for (int i = 0; i < 400; i++) { Tick(); using (BuildImage()) { } SetAct(a, 999); }
            sw.Stop();
            sb.AppendLine(a + " " + (sw.Elapsed.TotalMilliseconds / 400).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms/image, GC0 " + (GC.CollectionCount(0) - gc));
        }
        // image complète comme en vrai : calcul + comparaison + envoi à l écran (si elle a changé)
        SetAct("sit", 999); var h = Handle; pushes = 0; sw.Restart();
        for (int i = 0; i < 400; i++) { Tick(); Draw(); SetAct("sit", 999); }
        sw.Stop();
        sb.AppendLine("draw " + (sw.Elapsed.TotalMilliseconds / 400).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms/tick, envois " + pushes + "/400");
        File.WriteAllText(path, sb.ToString());
    }
    public void SwingTest(string path) {
        tray.Visible = false; held = true; cur = HANG.Clone(); face = 1;
        FakeHand = new Point(500, 500); Tick();
        var frames = new List<Bitmap>(); int x = 500;
        for (int i = 0; i < 16; i++) {
            if (i < 4) x += 40;                       // la main accélère vers la droite…
            FakeHand = new Point(x, 500); Tick();     // …puis s'arrête net
            if (i % 2 == 1) frames.Add(BuildImage());
        }
        using (var sheet = new Bitmap(W * frames.Count, H)) using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(70, 90, 120));
            for (int i = 0; i < frames.Count; i++) { g.DrawImage(frames[i], i * W, 0); frames[i].Dispose(); }
            sheet.Save(path, ImageFormat.Png);
        }
    }
    void Frame(Graphics g, int f, float jumpY) {
        g.ResetTransform();
        if (Math.Abs(swing) > 0.3f) { g.TranslateTransform(32, 19); g.RotateTransform(swing); g.TranslateTransform(-32, -19); }   // pivot = peau du cou
        if (f < 0) using (var mx = new Matrix(-1, 0, 0, 1, CW, 0)) g.MultiplyTransform(mx);
        g.TranslateTransform(0, -jumpY);
    }

    static void Step(ref float fx, ref float fy, double ph, float A, float L) {
        fx += (float)Math.Sin(ph) * A; fy -= (float)Math.Max(0, Math.Cos(ph)) * L;
    }

    static readonly int[] Sides = { -1, 1 };
    static void Leg(Graphics g, Color c, float hx, float hy, float fx, float fy, float w) {
        g.DrawLine(Pn(c, w, true), hx, hy, fx, fy - 1.5f);
        g.FillEllipse(Br(c), fx - 2f, fy - 3f, 5f, 3f);
    }

    Bitmap small;
    Bitmap RenderSmall(Pose p, int f, float jumpY, bool extras) {
        // 1) corps en vrai 8 bits (1 pixel du dessin = Px pixels à l'écran)
        if (small == null) small = new Bitmap(CW, CH, PixelFormat.Format32bppArgb);   // réutilisé d'une image à l'autre
        var bmp = small;
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);
            Frame(g, f, jumpY);
            { var fur = Br(FUR);
                Leg(g, FAR, p.fhx - 1, p.fhy, p.ffx2, p.ffy2, 3f);
                Leg(g, FAR, p.bhx - 1, p.bhy, p.fbx2, p.fby2, 3f);
                var pts = new PointF[tang.Length + 1];
                pts[0] = new PointF(p.tbx, p.tby);
                for (int i = 0; i < tang.Length; i++) {
                    double a = tang[i] * Math.PI / 180;
                    pts[i + 1] = new PointF(pts[i].X + (float)Math.Cos(a) * 2.3f, pts[i].Y + (float)Math.Sin(a) * 2.3f);
                }
                Action<Color, float> stroke = (c, extra) => {   // queue continue, qui s'affine vers le bout
                    for (int i = 0; i < tang.Length; i++)
                        g.DrawLine(Pn(c, 3.4f - i * 0.17f + extra, true), pts[i], pts[i + 1]);
                };
                Action tail = () => { if (p.tf >= 0.5f) stroke(OUT, 1.2f); stroke(FUR, 0); };
                if (p.tf < 0.5f) tail();
                var st = g.Save();
                g.TranslateTransform(p.bx, p.by); g.RotateTransform(p.ba);
                g.FillEllipse(fur, -p.bw / 2, -p.bh / 2, p.bw, p.bh);
                g.Restore(st);
                if (p.tf >= 0.5f) tail();
                if (p.haunch > 1.5f) g.FillEllipse(fur, p.bhx - p.haunch / 2, p.bhy - p.haunch * 0.55f, p.haunch, p.haunch * 0.95f);
                Leg(g, FUR, p.fhx, p.fhy, p.ffx, p.ffy, 3.4f);
                Leg(g, FUR, p.bhx, p.bhy, p.fbx, p.fby, 3.4f);
                DrawHead(g, p, false);
            }
        }
        PixelPass(bmp);
        // 2) visage dessiné à la même résolution 8 bits, après le contour
        using (var g = Graphics.FromImage(bmp)) {
            Frame(g, f, jumpY);
            g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
            DrawHead(g, p, true);
        }
        // 3) agrandissement sans lissage
        var big = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big)) {
            g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, new Rectangle(0, 0, W, H));
        }
        if (extras) DrawExtras(big, p, f, jumpY);
        return big;
    }

    void DrawHead(Graphics g, Pose p, bool face) {
        var st = g.Save();
        g.TranslateTransform(p.hx, p.hy); g.RotateTransform(p.ht);
        if (face) { DrawFace(g, p); g.Restore(st); return; }
        float eb = earBack, tw = earTw > 0 ? 2f : 0f;
        { var fur = Br(FUR); var inner = Br(INNER);
            // grandes oreilles dressées, volontairement un peu différentes (moins symétrique = plus vivant) :
            // la gauche un peu plus haute et ouverte vers l'extérieur, la droite un peu plus courte et penchée vers l'intérieur
            g.FillPolygon(fur, new[] { new PointF(-8f, -1), new PointF(-8.1f + eb, -14.3f + eb * 3.5f + tw), new PointF(-0.9f, -5.2f) });
            g.FillPolygon(fur, new[] { new PointF(8f, -1.2f), new PointF(6.9f - eb, -13.4f + eb * 3.3f), new PointF(1.1f, -5f) });
            g.FillPolygon(inner, new[] { new PointF(-6.3f, -3.8f), new PointF(-6.8f + eb, -11.1f + eb * 2.9f + tw), new PointF(-3f, -5.8f) });
            g.FillPolygon(inner, new[] { new PointF(6.2f, -3.7f), new PointF(5.9f - eb, -10.3f + eb * 2.7f), new PointF(3.2f, -5.6f) });
            g.FillEllipse(fur, -8, -6.5f, 16, 13);
            g.FillEllipse(fur, -9.5f, -1, 6, 5.5f); g.FillEllipse(fur, 3.5f, -1, 6, 5.5f);
        }
        g.FillRectangle(Br(COLLAR), -4.5f, 5.5f, 9, 1.6f);
        // médaillon : un petit poisson argenté sur le collier
        { var fish = Br(Color.FromArgb(255, 170, 205, 235)); var fin = Br(Color.FromArgb(255, 120, 160, 205));
            g.FillRectangle(fish, -2, 7, 3, 2);                  // corps
            g.FillRectangle(fin, 1, 6.5f, 1, 1); g.FillRectangle(fin, 1, 8.5f, 1, 1);   // queue en V
            g.FillRectangle(Br(Color.FromArgb(255, 30, 40, 60)), -2, 7, 1, 1);
        }
        { var wp = Pn(WHISK, 1f);
            foreach (int s in Sides) { g.DrawLine(wp, s * 6f, 2.5f, s * 10.5f, 1.5f); g.DrawLine(wp, s * 6f, 4f, s * 10.5f, 4.5f); }
        }
        g.Restore(st);
    }

    void DrawFace(Graphics g, Pose p) {
        // bâillement pendant l'étirement : 0 → 1 → 0 sur la durée de l'étirement
        float yawn = act == "stretch" && !held && !falling ? (float)Math.Sin(Math.Min(1.0, actT / Math.Max(0.1f, actDur)) * Math.PI) : 0f;
        bool happyEyes = petT > 0 || state == "happy" || state == "ready" || act == "rub" || act == "scratch";
        bool dilated = held || falling || state == "thinking" || act == "chase";
        float es = p.eyeShift + lookX, open = blink > 0 ? 0 : p.eye;
        bool peek = peekT > 0.25f && act == "lie" && !held && !falling;     // 0,25 s de marge : l œil se referme avant la fin
        { var eye = Br(EYE); var dkB = Br(EYEDK); var light = Br(Color.FromArgb(255, 186, 246, 160));
          var pup = Br(Color.FromArgb(255, 10, 8, 14)); var w = Br(Color.White); var blush = Br(Color.FromArgb(150, 214, 96, 128));
            // Yeux = petit sprite fixe de 5×6 pixels, posé sur des positions ENTIÈRES :
            // les deux yeux sont toujours identiques (avant, des ovales à virgule déformaient l'œil droit).
            int shift = (int)Math.Round(es), lx = Math.Max(-1, Math.Min(1, (int)Math.Round(lookX * 1.2f))), ly = Math.Max(-1, Math.Min(1, (int)Math.Round(lookY)));
            foreach (int s in Sides) {
                int ex = s * 4 + shift, ey = -1;                         // centre de l'œil
                Action<Brush, int, int, int, int> R = (b, x, y, ww, hh) => g.FillRectangle(b, ex + x, ey + y, ww, hh);
                if (peek && s == 1) {                                 // un seul œil entrouvert, paupière lourde, pupille vers la souris
                    R(dkB, -2, -1, 5, 1);                                // paupière
                    R(eye, -2, 0, 5, 1);                                 // fente d iris vert
                    R(pup, lx, 0, 1, 1);
                }
                else if (happyEyes) { R(eye, -2, 0, 1, 1); R(eye, -1, -1, 3, 1); R(eye, 2, 0, 1, 1); }           // ^ ^
                else if (open < 0.5f || yawn > 0.3f) { R(dkB, -2, 0, 1, 1); R(dkB, -1, 1, 3, 1); R(dkB, 2, 0, 1, 1); }    // yeux fermés
                else {
                    R(dkB, -1, -3, 3, 1);                                // haut plus foncé
                    R(eye, -2, -2, 5, 3);                                // iris vert
                    R(light, -2, 1, 5, 1); R(light, -1, 2, 3, 1);        // bas plus clair
                    int pw = dilated ? 3 : 1, px0 = -pw / 2 + lx;        // pupille fendue (s'élargit quand il a peur ou réfléchit)
                    R(pup, px0, -2 + Math.Max(0, ly), pw, 3);
                    R(w, -2 + Math.Max(0, lx), -2, 1, 1);                // reflet
                }
                R(blush, s > 0 ? 3 : -4, 3, 2, 1);                       // joues roses
            }
        }
        { var b = Br(NOSE);
            g.FillEllipse(b, -0.9f + es * 0.5f, 2.9f, 1.8f, 1.1f);
            if ((act == "groom" && Math.Sin(t * 11) > 0) || (act == "lick" && Math.Sin(t * 9) > 0.2)) g.FillEllipse(b, -0.5f + es * 0.5f, 4.4f, 1, 1.3f);   // langue
        }
        int mx = (int)Math.Round(es * 0.5f);
        if (yawn > 0.15f) {                                                   // grande bouche ouverte qui bâille, petite langue rose
            int mh = 1 + (int)Math.Round(yawn * 2.4f);
            g.FillRectangle(Br(Color.FromArgb(255, 70, 24, 40)), mx - 1, 4, 3, mh);
            g.FillRectangle(Br(NOSE), mx, 3 + mh, 1, 1);
        } else { var mp = Pn(Color.FromArgb(255, 70, 70, 96), 0.4f);   // petite bouche en « w »
            g.DrawArc(mp, -1.4f + es * 0.5f, 3.6f, 1.4f, 1.1f, 0, 180);
            g.DrawArc(mp, 0f + es * 0.5f, 3.6f, 1.4f, 1.1f, 0, 180);
        }
    }

    // Contour d'un pixel + reflet sur le dessus du pelage (style pixel art)
    static int[] ppIn, ppOut;
    static void PixelPass(Bitmap b) {
        int w = b.Width, h = b.Height, n = w * h;
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        if (ppIn == null || ppIn.Length != n) { ppIn = new int[n]; ppOut = new int[n]; }
        var px = ppIn; var o = ppOut;
        Marshal.Copy(d.Scan0, px, 0, n); Array.Copy(px, o, n);
        int fur = FUR.ToArgb(), far = FAR.ToArgb(), hi = HI.ToArgb(), outl = OUT.ToArgb();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            int i = y * w + x, c = px[i];
            if (((uint)c >> 24) == 0) {
                if ((x > 0 && (px[i - 1] == fur || px[i - 1] == far)) || (x < w - 1 && (px[i + 1] == fur || px[i + 1] == far)) ||
                    (y > 0 && (px[i - w] == fur || px[i - w] == far)) || (y < h - 1 && (px[i + w] == fur || px[i + w] == far))) o[i] = outl;
            } else if (c == fur && y > 0 && ((uint)px[i - w] >> 24) == 0) o[i] = hi;
        }
        Marshal.Copy(o, 0, d.Scan0, n); b.UnlockBits(d);
    }

    // petits chiffres 3×5 pixels (et « / ») pour la progression
    static readonly string[] Digits = { "XXXX.XX.XX.XXXX", ".X.XX..X..X.XXX", "XXX..XXXXX..XXX", "XXX..XXXX..XXXX", "X.XX.XXXX..X..X",
                                        "XXXX..XXX..XXXX", "XXXX..XXXX.XXXX", "XXX..X..X..X..X", "XXXX.XXXXX.XXXX", "XXXX.XXXX..XXXX" };
    static void PixText(Graphics g, Brush b, string s, int x, int y) {
        foreach (char ch in s) {
            string m = ch == '/' ? "..X..X.X.X..X.." : ch >= '0' && ch <= '9' ? Digits[ch - '0'] : null;
            if (m != null) for (int i = 0; i < 15; i++) if (m[i] == 'X') g.FillRectangle(b, x + i % 3, y + i / 3, 1, 1);
            x += 4;
        }
    }
    void DrawExtras(Bitmap bmp, Pose p, int f, float jumpY) {
        float hx = f > 0 ? p.hx : CW - p.hx, hy = p.hy - jumpY;
        int dir = hx > CW / 2 + 8 ? -1 : 1;
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.None;
            g.ScaleTransform(Px, Px);
            if (state == "thinking") {
                int k = (int)(t * 3) % 4;
                // bulle de réflexion : petits ronds qui grossissent en allant vers la discussion (Bébène réfléchit à ce qui s y passe)
                float ux = dir * 0.8f, uy = -0.6f;
                Rectangle to = thinkTo != Rectangle.Empty ? thinkTo : Panel.Visible ? Panel.Bounds : Rectangle.Empty;
                if (to != Rectangle.Empty) {
                    float sx = Left + hx * Px, sy = Top + hy * Px;               // tête, en coordonnées écran
                    float tx = Math.Max(to.Left, Math.Min(to.Right, sx)), ty = Math.Max(to.Top, Math.Min(to.Bottom, sy));
                    float vx = tx - sx, vy = ty - sy, n = (float)Math.Sqrt(vx * vx + vy * vy);
                    if (n > 1) { ux = vx / n; uy = vy / n; if (uy > -0.35f) { uy = -0.35f; ux = Math.Sign(vx == 0 ? dir : vx) * 0.94f; } }   // toujours un peu vers le haut
                }
                using (var b = new SolidBrush(Color.FromArgb(240, 235, 235, 245)))
                    for (int i = 0; i < Math.Min(k, 3); i++) {
                        int sz = i + 1; float d = 7 + i * 4.5f;
                        int x = (int)Math.Max(0, Math.Min(CW - sz, hx + ux * d - sz / 2f)), y = (int)Math.Max(0, Math.Min(CH - sz, hy - 4 + uy * d - sz / 2f));
                        g.FillRectangle(b, x, y, sz, sz);
                    }
                if (progTotal > 0) {                                     // progression de la tâche : « 3/5 » au-dessus de la tête
                    string txt = Math.Min(progDone, 99) + "/" + Math.Min(progTotal, 99);
                    int w = txt.Length * 4 - 1, x0 = (int)hx - w / 2, y0 = (int)Math.Max(0, hy - 22);
                    using (var bg = new SolidBrush(Color.FromArgb(235, 30, 30, 36))) g.FillRectangle(bg, x0 - 2, y0 - 2, w + 4, 9);
                    using (var b = new SolidBrush(Color.FromArgb(255, 235, 154, 120))) PixText(g, b, txt, x0, y0);
                }
            }
            if (state == "hand") {                                       // bulle avec une souris (ou un œil) : « prête-moi ta souris / montre-moi »
                int bob = (int)(t * 3f) % 2;
                string[] rows = handKind == "ecran"
                    ? new[] { ".XXXXXXXXX.", "XXXXXXXXXXX", "XXXOOOOOXXX", "XXOXXOXXOXX", "XOXXOOOXXOX", "XXOXXOXXOXX", "XXXOOOOOXXX", "XXXXXXXXXXX", ".XXXXXXXXX.", "..XX.......", ".X........." }
                    : new[] { ".XXXXXXXXX.", "XXXXOOOXXXX", "XXXOXOXOXXX", "XXXOXOXOXXX", "XXXOOOOOXXX", "XXXOXXXOXXX", "XXXOXXXOXXX", "XXXXOOOXXXX", ".XXXXXXXXX.", "..XX.......", ".X........." };
                int x0 = (int)(hx + dir * (held ? 14 : 10)) - (dir > 0 ? 1 : 10), y0 = (held ? (int)Math.Max(0, hy - 10) : (int)Math.Max(0, hy - 26)) - bob;
                using (var w = new SolidBrush(Color.FromArgb(245, 240, 238, 245))) using (var o = new SolidBrush(Color.FromArgb(255, 217, 119, 87)))
                    for (int rr = 0; rr < rows.Length; rr++) for (int c = 0; c < 11; c++) {
                        char ch = rows[rr][dir > 0 ? c : 10 - c];
                        if (ch == 'X') g.FillRectangle(w, x0 + c, y0 + rr, 1, 1); else if (ch == 'O') g.FillRectangle(o, x0 + c, y0 + rr, 1, 1);
                    }
            }
            if (state == "ready") {                                      // bulle « ! » : une réponse t attend (aussi quand on le porte)
                int bob = (int)(t * 2.5f) % 2;
                string[] rows = { ".XXXXXXX.", "XXXXOXXXX", "XXXXOXXXX", "XXXXOXXXX", "XXXXXXXXX", "XXXXOXXXX", ".XXXXXXX.", "..XX.....", ".X......." };
                int x0 = (int)(hx + dir * (held ? 14 : 10)) - (dir > 0 ? 1 : 8), y0 = (held ? (int)Math.Max(0, hy - 9) : (int)Math.Max(0, hy - 24)) - bob;   // porté : à côté de la tête
                using (var w = new SolidBrush(Color.FromArgb(245, 240, 238, 245))) using (var o = new SolidBrush(Color.FromArgb(255, 217, 119, 87)))
                    for (int rr = 0; rr < rows.Length; rr++) for (int c = 0; c < 9; c++) {
                        char ch = rows[rr][dir > 0 ? c : 8 - c];
                        if (ch == 'X') g.FillRectangle(w, x0 + c, y0 + rr, 1, 1); else if (ch == 'O') g.FillRectangle(o, x0 + c, y0 + rr, 1, 1);
                    }
            }
            if (state == "question") {                                   // bulle « ? » qui change de côté, avec un petit « ? » qui monte
                int side = ((int)(t / 1.6f) % 2 == 0) ? dir : -dir, bob = (int)(t * 2.5f) % 2;
                string[] rows = { ".XXXXXXX.", "XXXOOOXXX", "XXOXXXOXX", "XXXXXOXXX", "XXXXOXXXX", "XXXXXXXXX", "XXXXOXXXX", ".XXXXXXX.", "..XX.....", ".X......." };
                int x0 = (int)(hx + side * (held ? 14 : 10)) - (side > 0 ? 1 : 8), y0 = (held ? (int)Math.Max(0, hy - 10) : (int)Math.Max(0, hy - 25)) - bob;
                using (var w = new SolidBrush(Color.FromArgb(245, 240, 238, 245))) using (var o = new SolidBrush(Color.FromArgb(255, 217, 119, 87)))
                    for (int rr = 0; rr < rows.Length; rr++) for (int c = 0; c < 9; c++) {
                        char ch = rows[rr][side > 0 ? c : 8 - c];
                        if (ch == 'X') g.FillRectangle(w, x0 + c, y0 + rr, 1, 1); else if (ch == 'O') g.FillRectangle(o, x0 + c, y0 + rr, 1, 1);
                    }
                float ph = (t * 0.6f) % 1f;                                   // petit « ? » qui s envole de l autre côté
                int qx = (int)(hx - side * (7 + ph * 4)), qy = (int)(hy - 9 - ph * 12), qa = (int)(230 * (1 - ph));
                string[] q = { "XXX", "..X", ".X.", "...", ".X." };
                using (var b = new SolidBrush(Color.FromArgb(qa, 240, 238, 245)))
                    for (int rr = 0; rr < q.Length; rr++) for (int c = 0; c < 3; c++) if (q[rr][c] == 'X') g.FillRectangle(b, qx + c, Math.Max(0, qy + rr), 1, 1);
            }
            if (act == "lie" && actT > 2.5f && !held && !falling) {
                for (int i = 0; i < 3; i++) {
                    float ph = (t * 0.5f + i / 3f) % 1f;
                    int zx = (int)(hx + dir * (6 + ph * 7)), zy = (int)(hy - 8 - ph * 14), a = (int)(255 * (1 - ph));
                    using (var b = new SolidBrush(Color.FromArgb(a, 220, 225, 255))) {
                        g.FillRectangle(b, zx, zy, 3, 1); g.FillRectangle(b, zx + 1, zy + 1, 1, 1); g.FillRectangle(b, zx, zy + 2, 3, 1);
                    }
                }
            }
            // ronron : petites ondes qui vibrent à côté du poitrail (caresses, quand il se frotte, ou en dormant caressé)
            if ((petT > 0 || act == "rub") && !held && !falling) {
                float bx = f > 0 ? p.bx : CW - p.bx; int side = -dir;
                int k = (int)(t * 12) % 2;                                   // vibration rapide
                using (var b = new SolidBrush(Color.FromArgb(200, 225, 225, 240))) {
                    for (int i = 0; i < 3; i++) {
                        int ox = (int)(bx + side * (11 + i * 3)) + (k == 0 ? 0 : side), oy = (int)(p.by - 4 - jumpY) + i;
                        g.FillRectangle(b, ox, oy, 1, 1); g.FillRectangle(b, ox + side, oy + 1, 1, 2); g.FillRectangle(b, ox, oy + 3, 1, 1);   // « ) »
                    }
                }
            }
            if (heartT > 0) {
                float rise = (1.4f - heartT) * 6;
                int x0 = (int)(hx + (state == "ready" ? -dir : dir) * 9) - 2, y0 = (int)(hy - 11 - rise);
                string[] rows = { ".X.X.", "XXXXX", ".XXX.", "..X.." };
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, heartT)), HEART)))
                    for (int r = 0; r < rows.Length; r++) for (int c = 0; c < 5; c++) if (rows[r][c] == 'X') g.FillRectangle(b, x0 + c, y0 + r, 1, 1);
            }
        }
    }

    int pushes;
    // Surface GDI gardée d'une image à l'autre (avant : GetHbitmap créait et copiait un nouveau bitmap à chaque envoi).
    // On y copie directement les octets déjà lus dans Draw (BGRA prémultiplié, lignes de haut en bas).
    IntPtr dibDC, dib, dibOld, dibBits; int dibW, dibH;
    void Push(byte[] frame, int w, int h) {
        pushes++;
        if (dib == IntPtr.Zero || dibW != w || dibH != h) {
            FreeDib();
            var bi = new BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };   // hauteur négative = de haut en bas
            dibDC = CreateCompatibleDC(IntPtr.Zero);
            dib = CreateDIBSection(dibDC, ref bi, 0, out dibBits, IntPtr.Zero, 0);
            dibOld = SelectObject(dibDC, dib); dibW = w; dibH = h;
        }
        Marshal.Copy(frame, 0, dibBits, Math.Min(frame.Length, w * h * 4));
        var size = new Size(w, h); var src = Point.Empty; var pos = Location;
        var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref pos, ref size, dibDC, ref src, 0, ref bl, 2);
    }
    void FreeDib() {
        if (dib == IntPtr.Zero) return;
        SelectObject(dibDC, dibOld); DeleteObject(dib); DeleteDC(dibDC); dib = dibDC = IntPtr.Zero;
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { FreeDib(); base.OnFormClosed(e); }

    // Avatar (fenêtres HTML) et icône de notification tirés du sprite
    // icône de l app (bebene.ico, plusieurs tailles) à la taille voulue
    public static Icon AppIcon(Size s) {
        try { return new Icon(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "bebene.ico"), s); }
        catch { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
    }
    void ExportAvatar() {
        try {
            using (var s = RenderSmall(SIT, 1, 0, false)) {
                var crop = new Rectangle(13 * Px, 11 * Px, 38 * Px, 40 * Px);
                using (var big = new Bitmap(crop.Width, crop.Height)) {
                    using (var g = Graphics.FromImage(big)) { g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(s, new Rectangle(0, 0, big.Width, big.Height), crop, GraphicsUnit.Pixel); }
                    big.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "avatar.png"), ImageFormat.Png);
                }
                tray.Icon = AppIcon(SystemInformation.SmallIconSize);   // l'icône de l'app, à la bonne taille (nette)
            }
        } catch { }
    }

    // ---- Interaction ----
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) { down = true; moved = false; grab = e.Location; lastTouch = DateTime.Now; }
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (down) {
            if (!moved && Math.Abs(e.X - grab.X) + Math.Abs(e.Y - grab.Y) > 5) { moved = true; held = true; falling = false; }
            if (held) { var c = Cursor.Position; Location = new Point(c.X - 32 * Px, c.Y - 19 * Px); if (Panel.Visible) PlacePanel(); }
        } else {                       // caresse
            if (petT <= 0 && heartT <= 0) heartT = 1.4f;
            petT = 0.8f; lastTouch = DateTime.Now;
            if ((act == "sit" || act == "groom") && !held && !falling) SetAct("rub", 2.5f);   // caressé : il se frotte contre ta main en ronronnant
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left || !down) return;
        down = false;
        if (moved) { held = false; falling = true; vy = 0; Settings.Set("X", Left); }
        else { if (act == "lie") SetAct("stretch", 1.5f); TogglePanel(); }
    }

    // ---- App ----
    public void Quit() { Settings.Set("X", Left); Spell.Enable(false); if (Panel.Flash) ChatPanel.Forget(SessionId); tray.Visible = false; Application.Exit(); }
    public void RefreshSettings() { if (settingsWin != null && !settingsWin.IsDisposed && settingsWin.Visible) settingsWin.Refresh2(); }

    public SpellAssist Spell;
    // ---- rappels d examens et de rendez-vous ----
    ReminderCard remindCard; readonly System.Windows.Forms.Timer remindTimer = new System.Windows.Forms.Timer { Interval = 60000 };
    void CheckReminders() {
        if (remindCard != null && remindCard.Visible) return;                    // une carte à la fois
        string when; var e = Reminders.Due(out when); if (e == null) return;
        if (remindCard == null) { remindCard = new ReminderCard(); remindCard.OnDone = () => CheckReminders(); }
        remindCard.Ev = e; remindCard.When = when;
        var wa = Screen.FromControl(this).WorkingArea;
        int x = Visible ? Left + W / 2 - 160 : wa.Right - 340, y = Visible ? Top - 116 : wa.Bottom - 130;
        remindCard.ShowAt(new Point(Math.Max(wa.Left + 4, Math.Min(x, wa.Right - 324)), Math.Max(wa.Top + 4, y)));
        if (state != "ready") SetState("happy");                            // le chat saute pour attirer l attention (sans perdre « réponse prête »)
    }
    ToolStripMenuItem spellItem;
    void SetSpell(bool on) {
        if (!IsHandleCreated) return;
        Settings.Set("SpellCheck", on); spellItem.Checked = on; Spell.Enable(on);
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); if (Settings.Bool("SpellCheck", false)) Spell.Enable(true); }

    public IntPtr SettingsHwnd { get { return settingsWin != null && !settingsWin.IsDisposed && settingsWin.IsHandleCreated ? settingsWin.Handle : IntPtr.Zero; } }
    public void ShowSettings() { ShowSettings(null); }
    public void ShowSettings(string section) {
        if (settingsWin == null || settingsWin.IsDisposed) settingsWin = new SettingsWindow(this);
        if (!settingsWin.Visible) settingsWin.Show();
        settingsWin.BringFront();                                             // devant toutes les fenêtres, discussion comprise
        settingsWin.Refresh2();
        if (section != null) settingsWin.Goto(section);
    }

    void SetCatVisible(bool v) {
        Settings.Set("ShowCat", v); showItem.Checked = v;
        if (v) { Show(); Draw(); } else Hide();
    }

    public void OpenTerminal(bool resume) {
        string args = "", model = Settings.Str("Model", "");
        if (Regex.IsMatch(model, "^[a-z0-9.-]+$")) args += " --model " + model;
        if (resume && Regex.IsMatch(SessionId, "^[0-9a-fA-F-]{36}$")) args += " --resume " + SessionId;
        string title = ("Claude Code — " + Util.Short(Panel.WorkDir)).Replace("'", "''").Replace("\"", "");
        try {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoExit -Command \"$Host.UI.RawUI.WindowTitle='" + title + "'; claude" + args + "\"") {
                WorkingDirectory = Panel.WorkDir, UseShellExecute = true });
        } catch (Exception ex) { MessageBox.Show(ex.Message, "Bébène"); }
    }

    // catalogue des fonctions (fonctions.json, livré avec l app et complété à chaque amélioration)
    static object Features() {
        try { return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "fonctions.json"), Encoding.UTF8)); }
        catch { return new object[0]; }
    }
    public string SettingsJson() {
        var d = new Dictionary<string, object> {
            { "name", Settings.Name }, { "model", Settings.Str("Model", "") }, { "scale", scalePct }, { "activity", Settings.Int("Activity", 50) },
            { "showCat", Settings.Bool("ShowCat", true) }, { "topMost", TopMost }, { "autoStart", Settings.AutoStart },
            { "prompt", Settings.Str("Prompt", "") }, { "hotkey1", Settings.Str("Hotkey1", HotkeyDefaults[0]) }, { "hotkey2", Settings.Str("Hotkey2", HotkeyDefaults[1]) }, { "hotkeyOk", HotkeyOk }, { "stream", Settings.Bool("Stream", true) }, { "light", Settings.Bool("Light", false) }, { "autoModel", Settings.Bool("AutoModel", true) },{ "caveman", Settings.Bool("Caveman", true) }, { "chrome", Settings.Bool("Chrome", true) }, { "postits", Settings.Bool("PostIts", true) }, { "prepare", Settings.Bool("Prepare", true) }, { "spell", Settings.Bool("SpellCheck", false) }, { "autoAllow", Settings.Str("AutoAllow", "") }, { "remindExam", Settings.Bool("RemindExam", true) }, { "remindRdv", Settings.Bool("RemindRdv", true) }, { "remindWhen", Settings.Str("RemindWhen", "both") }, { "features", Features() }, { "manual", Manual.Raw() }, { "hidden", new List<string>(Manual.Hidden()) }, { "calendars", Calendars.Config() }, { "locFilter", Settings.Str("LocFilter", "") }, { "gmailUser", Mailbox.GmailUser }, { "gmailSet", Mailbox.GmailReady }, { "outlookOn", Mailbox.OutlookOn }, { "mailStatus", Mailbox.Status }, { "mailRunning", Mailbox.Running },{ "workDir", Panel.WorkDir }, { "hasSession", SessionId.Length > 0 },
            { "version", Program.Version }
        };
        return new JavaScriptSerializer().Serialize(d);
    }

    public void ApplySettings(string json) {
        Dictionary<string, object> d;
        try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); } catch { return; }
        foreach (var kv in d) {
            switch (kv.Key) {
                case "name":
                    string n = Convert.ToString(kv.Value).Trim(); if (n.Length > 30) n = n.Substring(0, 30);
                    Settings.Set("Name", n); Panel.Post("name\n" + Settings.Name); break;
                case "model":
                    string md = Convert.ToString(kv.Value);
                    if (md == "" || Regex.IsMatch(md, "^[a-z0-9.-]+$")) Settings.Set("Model", md); break;
                case "prompt":
                    string pr = Convert.ToString(kv.Value); if (pr.Length > 4000) pr = pr.Substring(0, 4000);
                    Settings.Set("Prompt", pr); break;
                case "scale":
                    int sc = Convert.ToInt32(kv.Value); Settings.Set("Scale", sc);
                    int center = Left + W / 2; SetScale(sc); Left = center - W / 2;
                    break;
                case "showCat": SetCatVisible(Convert.ToBoolean(kv.Value)); break;
                case "activity": Settings.Set("Activity", Math.Max(0, Math.Min(100, Convert.ToInt32(kv.Value)))); break;
                case "topMost": TopMost = Convert.ToBoolean(kv.Value); Settings.Set("TopMost", TopMost); break;
                case "caveman": Settings.Set("Caveman", Convert.ToBoolean(kv.Value)); break;
                case "stream": Settings.Set("Stream", Convert.ToBoolean(kv.Value)); break;
                case "autoModel": Settings.Set("AutoModel", Convert.ToBoolean(kv.Value)); break;
                case "light": Settings.Set("Light", Convert.ToBoolean(kv.Value)); Panel.PostTheme(); break;
                case "hotkey1": case "hotkey2": {
                    string hk = Convert.ToString(kv.Value).Trim(); uint hm, hv;
                    if (hk == "" || ParseHotkey(hk, out hm, out hv)) Settings.Set(kv.Key == "hotkey1" ? "Hotkey1" : "Hotkey2", hk);
                    RegisterHotkeys(); break;
                }
                case "hotkeyPause": PauseHotkeys(); break;
                case "hotkeyResume": RegisterHotkeys(); break;
                case "chrome": Settings.Set("Chrome", Convert.ToBoolean(kv.Value)); break;
                case "postits": Settings.Set("PostIts", Convert.ToBoolean(kv.Value)); Notes.Refresh(); Panel.Post("notes\n" + Notes.Json()); break;
                case "prepare": Settings.Set("Prepare", Convert.ToBoolean(kv.Value)); break;
                case "spell": SetSpell(Convert.ToBoolean(kv.Value)); break;
                case "autoAllow": { var ok = new List<string>(); foreach (var a in Convert.ToString(kv.Value).Split(',')) if (Regex.IsMatch(a, "^[A-Za-z]+$")) ok.Add(a); Settings.Set("AutoAllow", string.Join(",", ok.ToArray())); } break;
                case "remindExam": Settings.Set("RemindExam", Convert.ToBoolean(kv.Value)); break;
                case "remindRdv": Settings.Set("RemindRdv", Convert.ToBoolean(kv.Value)); break;
                case "remindWhen": { string w = Convert.ToString(kv.Value); if (w == "both" || w == "veille" || w == "heure") Settings.Set("RemindWhen", w); } break;
                case "manualAdd": Manual.Add(kv.Value as Dictionary<string, object>); Panel.PostCalendars(); break;
                case "manualDel": Manual.Remove(Convert.ToString(kv.Value)); Panel.PostCalendars(); break;
                case "unhide": Manual.Unhide(Convert.ToString(kv.Value)); Panel.PostCalendars(); break;
                case "calendars": Settings.Set("Calendars", new JavaScriptSerializer().Serialize(kv.Value)); Panel.RefreshCalendars(); break;
                case "locFilter": Settings.Set("LocFilter", Convert.ToString(kv.Value).Trim()); Panel.PostCalendars(); break;
                case "gmailUser": Settings.Set("GmailUser", Convert.ToString(kv.Value).Trim()); break;
                case "gmailPass": { string pw = Convert.ToString(kv.Value).Trim(); if (pw.Length > 0) Mailbox.SetGmailPass(pw); } break;
                case "gmailClear": Settings.Set("GmailPass", ""); Settings.Set("GmailUser", ""); break;
                case "outlookOn": Settings.Set("OutlookMail", Convert.ToBoolean(kv.Value)); break;
                case "mailScan": Panel.ScanMail(); break;
                case "autoStart": Settings.AutoStart = Convert.ToBoolean(kv.Value); break;
            }
        }
    }

    public void TogglePanel() { if (Panel.Visible) Panel.Hide(); else ShowPanel(); }

    public void ShowPanel() {
        PlacePanel();
        if (!Panel.Visible) Panel.Show();
        Panel.Activate();
        Panel.FocusInput();
    }

    public void PlacePanel() {
        if (Panel.Moved) return;                         // déplacée à la main : elle reste où on l a mise
        var wa = Screen.FromControl(this).WorkingArea;
        if (!Visible) { Panel.Location = new Point(wa.Right - Panel.Width - 16, wa.Bottom - Panel.Height - 16); return; }
        int x = Left - Panel.Width + 10 * Px;
        if (x < wa.Left) x = Right - 10 * Px;
        int y = Math.Max(wa.Top + 8, Math.Min(Top + 4 * Px - Panel.Height, wa.Bottom - Panel.Height - 8));
        Panel.Location = new Point(x, y);
    }
}
