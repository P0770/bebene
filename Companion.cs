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
[assembly: System.Reflection.AssemblyVersion("1.34.0.0")]

static class Program {
    public const string Version = "1.34";
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
        if (args.Length == 2 && args[0] == "--readytest") { new Companion(false).ReadyPreview(args[1]); return; }
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
    public static async void InitWeb(WebView2 web, string page, EventHandler<CoreWebView2WebMessageReceivedEventArgs> onMsg) {
        try {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await web.EnsureCoreWebView2Async(env);
            var cw = web.CoreWebView2;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.WebMessageReceived += onMsg;
            cw.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
            cw.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("file:")) { e.Cancel = true; OpenExternal(e.Uri); } };
            cw.Navigate(new Uri(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), page)).AbsoluteUri);
        } catch (Exception ex) {
            MessageBox.Show("WebView2 n'a pas pu démarrer :\n" + ex.Message, "Bébène");
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
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

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
        remindTimer.Tick += (s, e) => CheckReminders(); remindTimer.Start();
        spellItem = new ToolStripMenuItem("Correcteur d'orthographe") { Checked = Settings.Bool("SpellCheck", false) };
        spellItem.Click += (s, e) => SetSpell(!spellItem.Checked);

        var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Color.White, Font = new Font("Segoe UI", 10f), ShowImageMargin = false, Padding = new Padding(4) };
        showItem = new ToolStripMenuItem("Afficher le chat sur le bureau") { Checked = Settings.Bool("ShowCat", true) };
        showItem.Click += (s, e) => SetCatVisible(!showItem.Checked);
        m.Items.Add(new ToolStripMenuItem("Ouvrir la discussion", null, (s, e) => ShowPanel()) { ShortcutKeyDisplayString = "Alt+Espace" });
        m.Items.Add(new ToolStripMenuItem("Nouvelle conversation", null, (s, e) => { ShowPanel(); Panel.Post("new"); }));
        m.Items.Add(new ToolStripMenuItem("Ouvrir Claude Code ici", null, (s, e) => OpenTerminal(false)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(showItem);
        m.Items.Add(spellItem);
        m.Items.Add(new ToolStripMenuItem("Réglages…", null, (s, e) => ShowSettings()));
        m.Items.Add(new ToolStripMenuItem("Quitter", null, (s, e) => Quit()));
        ContextMenuStrip = m;

        tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath), Text = "Bébène", ContextMenuStrip = m, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        ExportAvatar();

        AllowDrop = true;
        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) => {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) { ShowPanel(); Panel.Post("paths\n" + string.Join("\n", files)); }
        };

        timer.Tick += (s, e) => { Tick(); if (Visible) Draw(); };
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
        RegisterHotKey(Handle, 1, 0x4003, 0x43);                 // Ctrl+Alt+C
        AltSpace = RegisterHotKey(Handle, 2, 0x4001, 0x20);      // Alt+Espace
    }
    public bool AltSpace;
    protected override void WndProc(ref Message m) { if (m.Msg == 0x0312) TogglePanel(); base.WndProc(ref m); }

    public string State { get { return state; } }
    float readyT;
    // "ready" : la réponse est arrivée pendant que la discussion était fermée → Bébène, content, attend qu'on l'ouvre
    public void SetState(string s) {
        state = s; happyT = 0; readyT = 0;
        if (s == "happy" || s == "ready") { jumpT = 0; heartT = 1.4f; }
    }

    public void Preview(string path) {
        tray.Visible = false;
        var poses = new[] { SIT, STAND, GROOM, STRETCH, LIE, CURL, SPRAWL, LOAF, SCRATCH, SIT, STRETCH, LICK };
        var acts = new[] { "sit", "walk", "groom", "stretch", "lie", "lie", "lie", "lie", "scratch", "rub", "stretch", "lick" };
        var times = new[] { 0f, 0f, 0f, 0.15f, 0f, 0f, 0f, 0f, 0f, 0f, 0.5f, 0f };   // 2 moments de l'étirement : début et bâillement
        using (var sheet = new Bitmap(W * 4, H * 3))
        using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(255, 40, 44, 52));
            for (int i = 0; i < poses.Length; i++) {
                act = acts[i]; actDur = 2f; actT = times[i] * actDur; petT = act == "rub" ? 0.5f : 0f; t = 0.3f;
                for (int k = 0; k < tang.Length; k++) tang[k] = poses[i].ta + poses[i].tc * k;
                var p = poses[i].Clone();
                if (act == "rub") { p.ht += 12; p.hx += 1.3f; p.ba += 3.5f; }
                using (var b = RenderSmall(p, 1, 0, true)) g.DrawImage(b, (i % 4) * W, (i / 4) * H);
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }

    // test : Bébène content qui attend qu on lise sa réponse (deux sens + avec cœur)
    public void ReadyPreview(string path) {
        tray.Visible = false; state = "ready"; act = "sit"; t = 0.3f;
        using (var sheet = new Bitmap(W * 4, H)) using (var g = Graphics.FromImage(sheet)) {
            g.Clear(Color.FromArgb(255, 40, 44, 52));
            for (int i = 0; i < 4; i++) {
                heartT = i >= 2 ? 0.9f : 0; t = 0.3f + i * 0.4f; held = i == 3;   // 4e : porté par la peau du cou
                var pose = held ? HANG : SIT;
                for (int k = 0; k < tang.Length; k++) tang[k] = pose.ta + pose.tc * k;
                using (var b = RenderSmall(pose.Clone(), i == 1 ? -1 : 1, i == 2 ? 6 : 0, true)) g.DrawImage(b, i * W, 0);
            }
            held = false;
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
        int r = rnd.Next(100);
        if (idle > 3 && r < 55) { Sleep(25 + rnd.Next(40)); return; }
        if (near && r < 14) { SetAct("chase", 6); return; }
        if (r < 44) { targetX = rnd.Next(wa.Left, Math.Max(wa.Left + 1, wa.Right - W)); SetAct(rnd.Next(5) == 0 ? "run" : "walk", 99); return; }
        if (r < 52) { SetAct("groom", 3 + rnd.Next(4)); return; }                             // toilette du visage
        if (r < 60) { SetAct("lick", 3 + rnd.Next(4)); return; }                              // toilette du corps
        if (r < 75) { Sleep(12 + rnd.Next(25)); return; }
        if (r < 82) { SetAct("stretch", 2.4f); return; }
        if (r < 88) { SetAct("scratch", 1.6f + (float)rnd.NextDouble() * 1.5f); return; }   // se gratte derrière l'oreille
        if (r < 93) { SetAct("rub", 3 + rnd.Next(3)); return; }                               // se frotte en ronronnant
        SetAct("sit", 3 + rnd.Next(6));
    }

    // ---- fenêtre que l'utilisateur utilise (hors Bébène), pour que Claude sache de quel fichier on parle ----
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static string LastWindow = "";
    int fgTick;
    void TrackForeground() {
        if (++fgTick % 30 != 0) return;                                     // ~1 fois par seconde
        var h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
        if (h == IntPtr.Zero || pid == Program.Pid) return;
        var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
        string tt = sb.ToString().Trim();
        if (tt.Length > 0 && tt != "Program Manager") LastWindow = tt;
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

        var wa = Screen.FromPoint(new Point(Left + W / 2, Top + H / 2)).WorkingArea;
        int groundTop = wa.Bottom - 51 * Px;
        bool busy = state == "thinking" || state == "ready" || Panel.Visible;
        Pose tgt = SIT; int dx = 0;

        if (held) tgt = HANG;
        else if (falling) {
            vy += 0.9f * Px; int ny = Top + (int)vy;
            if (ny >= groundTop) { ny = groundTop; falling = false; vy = 0; landT = 0.25f; SetAct("sit", 3); }
            Top = ny; tgt = FALL;
        } else {
            if (Top != groundTop) Top = groundTop;
            if (!held && (Left < wa.Left - W / 2 || Left > wa.Right - W / 2)) Left = Math.Max(wa.Left, Math.Min(wa.Right - W, Left)); // écran débranché / résolution changée
            if (busy && act != "sit" && act != "groom" && act != "lick") SetAct("sit", 2);
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
                    if (actT > 5 && rnd.Next(450) == 0) sleepPose = SLEEPS[rnd.Next(SLEEPS.Length)]; // change de position en dormant
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
        float amp = state == "thinking" ? 24 : state == "ready" ? 20 : act == "lie" ? 4 : held ? 16 : walkAmp > 0.3f ? 7 : 10;
        float freq = state == "thinking" ? 6 : state == "ready" ? 4.5f : 1.8f;
        for (int i = 0; i < tang.Length; i++) {
            float target = cur.ta + cur.tc * i + amp * (float)Math.Sin(t * freq - i * 0.55f) * (i + 1) / tang.Length;
            tang[i] += (target - tang[i]) * Math.Max(0.06f, 0.3f - i * 0.025f);
        }
    }

    // ---- Dessin ----
    void Draw() { using (var img = BuildImage()) Push(img); }
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

    static void Leg(Graphics g, Color c, float hx, float hy, float fx, float fy, float w) {
        using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, hx, hy, fx, fy - 1.5f);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, fx - 2f, fy - 3f, 5f, 3f);
    }

    Bitmap RenderSmall(Pose p, int f, float jumpY, bool extras) {
        // 1) corps en vrai 8 bits (1 pixel du dessin = Px pixels à l'écran)
        var bmp = new Bitmap(CW, CH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);
            Frame(g, f, jumpY);
            using (var fur = new SolidBrush(FUR)) {
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
                        using (var pen = new Pen(c, 3.4f - i * 0.17f + extra) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(pen, pts[i], pts[i + 1]);
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
        bmp.Dispose();
        if (extras) DrawExtras(big, p, f, jumpY);
        return big;
    }

    void DrawHead(Graphics g, Pose p, bool face) {
        var st = g.Save();
        g.TranslateTransform(p.hx, p.hy); g.RotateTransform(p.ht);
        if (face) { DrawFace(g, p); g.Restore(st); return; }
        float eb = earBack, tw = earTw > 0 ? 2f : 0f;
        using (var fur = new SolidBrush(FUR)) using (var inner = new SolidBrush(INNER)) {
            // grandes oreilles dressées, volontairement un peu différentes (moins symétrique = plus vivant) :
            // la gauche un peu plus haute et ouverte vers l'extérieur, la droite un peu plus courte et penchée vers l'intérieur
            g.FillPolygon(fur, new[] { new PointF(-8f, -1), new PointF(-8.1f + eb, -14.3f + eb * 3.5f + tw), new PointF(-0.9f, -5.2f) });
            g.FillPolygon(fur, new[] { new PointF(8f, -1.2f), new PointF(6.9f - eb, -13.4f + eb * 3.3f), new PointF(1.1f, -5f) });
            g.FillPolygon(inner, new[] { new PointF(-6.3f, -3.8f), new PointF(-6.8f + eb, -11.1f + eb * 2.9f + tw), new PointF(-3f, -5.8f) });
            g.FillPolygon(inner, new[] { new PointF(6.2f, -3.7f), new PointF(5.9f - eb, -10.3f + eb * 2.7f), new PointF(3.2f, -5.6f) });
            g.FillEllipse(fur, -8, -6.5f, 16, 13);
            g.FillEllipse(fur, -9.5f, -1, 6, 5.5f); g.FillEllipse(fur, 3.5f, -1, 6, 5.5f);
        }
        using (var b = new SolidBrush(COLLAR)) g.FillRectangle(b, -4.5f, 5.5f, 9, 1.6f);
        // médaillon : un petit poisson argenté sur le collier
        using (var fish = new SolidBrush(Color.FromArgb(255, 170, 205, 235))) using (var fin = new SolidBrush(Color.FromArgb(255, 120, 160, 205))) {
            g.FillRectangle(fish, -2, 7, 3, 2);                  // corps
            g.FillRectangle(fin, 1, 6.5f, 1, 1); g.FillRectangle(fin, 1, 8.5f, 1, 1);   // queue en V
            using (var eyeB = new SolidBrush(Color.FromArgb(255, 30, 40, 60))) g.FillRectangle(eyeB, -2, 7, 1, 1);
        }
        using (var wp = new Pen(WHISK, 1f))
            foreach (int s in new[] { -1, 1 }) { g.DrawLine(wp, s * 6f, 2.5f, s * 10.5f, 1.5f); g.DrawLine(wp, s * 6f, 4f, s * 10.5f, 4.5f); }
        g.Restore(st);
    }

    void DrawFace(Graphics g, Pose p) {
        // bâillement pendant l'étirement : 0 → 1 → 0 sur la durée de l'étirement
        float yawn = act == "stretch" && !held && !falling ? (float)Math.Sin(Math.Min(1.0, actT / Math.Max(0.1f, actDur)) * Math.PI) : 0f;
        bool happyEyes = petT > 0 || state == "happy" || state == "ready" || act == "rub" || act == "scratch";
        bool dilated = held || falling || state == "thinking" || act == "chase";
        float es = p.eyeShift + lookX, open = blink > 0 ? 0 : p.eye;
        using (var eye = new SolidBrush(EYE)) using (var ep = new Pen(EYE, 0.9f)) using (var dk = new Pen(EYEDK, 0.8f))
        using (var dkB = new SolidBrush(EYEDK)) using (var light = new SolidBrush(Color.FromArgb(255, 186, 246, 160)))
        using (var pup = new SolidBrush(Color.FromArgb(255, 10, 8, 14))) using (var w = new SolidBrush(Color.White))
        using (var blush = new SolidBrush(Color.FromArgb(150, 214, 96, 128))) {
            // Yeux = petit sprite fixe de 5×6 pixels, posé sur des positions ENTIÈRES :
            // les deux yeux sont toujours identiques (avant, des ovales à virgule déformaient l'œil droit).
            int shift = (int)Math.Round(es), lx = Math.Max(-1, Math.Min(1, (int)Math.Round(lookX * 1.2f))), ly = Math.Max(-1, Math.Min(1, (int)Math.Round(lookY)));
            foreach (int s in new[] { -1, 1 }) {
                int ex = s * 4 + shift, ey = -1;                         // centre de l'œil
                Action<Brush, int, int, int, int> R = (b, x, y, ww, hh) => g.FillRectangle(b, ex + x, ey + y, ww, hh);
                if (happyEyes) { R(eye, -2, 0, 1, 1); R(eye, -1, -1, 3, 1); R(eye, 2, 0, 1, 1); }           // ^ ^
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
        using (var b = new SolidBrush(NOSE)) {
            g.FillEllipse(b, -0.9f + es * 0.5f, 2.9f, 1.8f, 1.1f);
            if ((act == "groom" && Math.Sin(t * 11) > 0) || (act == "lick" && Math.Sin(t * 9) > 0.2)) g.FillEllipse(b, -0.5f + es * 0.5f, 4.4f, 1, 1.3f);   // langue
        }
        int mx = (int)Math.Round(es * 0.5f);
        if (yawn > 0.15f) {                                                   // grande bouche ouverte qui bâille, petite langue rose
            int mh = 1 + (int)Math.Round(yawn * 2.4f);
            using (var m = new SolidBrush(Color.FromArgb(255, 70, 24, 40))) g.FillRectangle(m, mx - 1, 4, 3, mh);
            using (var tg = new SolidBrush(NOSE)) g.FillRectangle(tg, mx, 3 + mh, 1, 1);
        } else using (var mp = new Pen(Color.FromArgb(255, 70, 70, 96), 0.4f)) {   // petite bouche en « w »
            g.DrawArc(mp, -1.4f + es * 0.5f, 3.6f, 1.4f, 1.1f, 0, 180);
            g.DrawArc(mp, 0f + es * 0.5f, 3.6f, 1.4f, 1.1f, 0, 180);
        }
    }

    // Contour d'un pixel + reflet sur le dessus du pelage (style pixel art)
    static void PixelPass(Bitmap b) {
        int w = b.Width, h = b.Height, n = w * h;
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[n]; Marshal.Copy(d.Scan0, px, 0, n);
        var o = (int[])px.Clone();
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

    void DrawExtras(Bitmap bmp, Pose p, int f, float jumpY) {
        float hx = f > 0 ? p.hx : CW - p.hx, hy = p.hy - jumpY;
        int dir = hx > CW / 2 + 8 ? -1 : 1;
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.None;
            g.ScaleTransform(Px, Px);
            if (state == "thinking") {
                int k = (int)(t * 3) % 4;
                using (var b = new SolidBrush(Color.FromArgb(240, 235, 235, 245)))
                    for (int i = 0; i < Math.Min(k, 3); i++) g.FillRectangle(b, (int)(hx + dir * (8 + i * 4)), (int)Math.Max(0, hy - 12 - i * 3), 2, 2);
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

    void Push(Bitmap bmp) {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var size = new Size(W, H); var src = Point.Empty; var pos = Location;
        var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref bl, 2);
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }

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
    public void RefreshSettings() { if (settingsWin != null && settingsWin.Visible) settingsWin.Refresh2(); }

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

    public void ShowSettings() { ShowSettings(null); }
    public void ShowSettings(string section) {
        if (settingsWin == null) settingsWin = new SettingsWindow(this);
        if (!settingsWin.Visible) settingsWin.Show();
        if (settingsWin.WindowState == FormWindowState.Minimized) settingsWin.WindowState = FormWindowState.Normal;
        settingsWin.Activate();
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
            { "name", Settings.Name }, { "model", Settings.Str("Model", "") }, { "scale", scalePct },
            { "showCat", Settings.Bool("ShowCat", true) }, { "topMost", TopMost }, { "autoStart", Settings.AutoStart },
            { "prompt", Settings.Str("Prompt", "") }, { "caveman", Settings.Bool("Caveman", true) }, { "spell", Settings.Bool("SpellCheck", false) }, { "autoAllow", Settings.Str("AutoAllow", "") }, { "remindExam", Settings.Bool("RemindExam", true) }, { "remindRdv", Settings.Bool("RemindRdv", true) }, { "remindWhen", Settings.Str("RemindWhen", "both") }, { "features", Features() }, { "manual", Manual.Raw() }, { "hidden", new List<string>(Manual.Hidden()) }, { "calendars", Calendars.Config() }, { "locFilter", Settings.Str("LocFilter", "") }, { "gmailUser", Mailbox.GmailUser }, { "gmailSet", Mailbox.GmailReady }, { "outlookOn", Mailbox.OutlookOn }, { "mailStatus", Mailbox.Status }, { "mailRunning", Mailbox.Running },{ "workDir", Panel.WorkDir }, { "hasSession", SessionId.Length > 0 },
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
                case "topMost": TopMost = Convert.ToBoolean(kv.Value); Settings.Set("TopMost", TopMost); break;
                case "caveman": Settings.Set("Caveman", Convert.ToBoolean(kv.Value)); break;
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

// ---------- Correcteur d'orthographe (correcteur intégré de Windows : local, 0 token) ----------
[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellCheckerFactory {
    [PreserveSig] int get_SupportedLanguages(out IntPtr value);
    [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string tag, [MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string tag, out ISpellChecker value);
}
[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellChecker {
    [PreserveSig] int get_LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string value);
    [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
    [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out System.Runtime.InteropServices.ComTypes.IEnumString value);
}
[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEnumSpellingError { [PreserveSig] int Next(out ISpellingError value); }
[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellingError {
    [PreserveSig] int get_StartIndex(out uint v);
    [PreserveSig] int get_Length(out uint v);
    [PreserveSig] int get_CorrectiveAction(out int v);
    [PreserveSig] int get_Replacement([MarshalAs(UnmanagedType.LPWStr)] out string v);
}
[ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")] class SpellCheckerFactoryCo { }

class Fix { public int Start, Len; public string Word, Repl; public List<string> Sugs = new List<string>(); }

static class Speller {
    [ThreadStatic] static ISpellChecker sc;      // un correcteur par thread (COM)
    [ThreadStatic] static bool tried;
    public static string Lang = "";
    static readonly HashSet<string> ignored = new HashSet<string>();
    public static void Ignore(string w) { lock (ignored) ignored.Add(w); }
    static ISpellChecker Get() {
        if (tried) return sc; tried = true;
        try {
            var f = (ISpellCheckerFactory)new SpellCheckerFactoryCo();
            foreach (var tag in new[] { "fr-CH", "fr-FR", "fr", System.Globalization.CultureInfo.CurrentUICulture.Name }) {
                bool ok;
                if (f.IsSupported(tag, out ok) == 0 && ok && f.CreateSpellChecker(tag, out sc) == 0 && sc != null) { Lang = tag; break; }
            }
        } catch { sc = null; }
        return sc;
    }
    public static List<Fix> Check(string text) {
        var list = new List<Fix>();
        try {
            var c = Get(); if (c == null) return list;
            IEnumSpellingError en;
            if (c.Check(text, out en) != 0 || en == null) return list;
            ISpellingError e;
            while (en.Next(out e) == 0 && e != null) {
                uint s, l; int a; e.get_StartIndex(out s); e.get_Length(out l); e.get_CorrectiveAction(out a);
                if (s + l > text.Length) continue;
                var fx = new Fix { Start = (int)s, Len = (int)l, Word = text.Substring((int)s, (int)l) };
                bool ign; lock (ignored) ign = ignored.Contains(fx.Word);
                if (ign) continue;
                if (a == 2) { string r; e.get_Replacement(out r); if (!string.IsNullOrEmpty(r)) fx.Sugs.Add(r); }
                else if (a == 1) {
                    System.Runtime.InteropServices.ComTypes.IEnumString sug;
                    if (c.Suggest(fx.Word, out sug) == 0 && sug != null) {
                        var arr = new string[1]; IntPtr fetched = Marshal.AllocCoTaskMem(4);
                        try { for (int i = 0; i < 3 && sug.Next(1, arr, fetched) == 0; i++) if (arr[0] != fx.Word) fx.Sugs.Add(arr[0]); }
                        finally { Marshal.FreeCoTaskMem(fetched); }
                    }
                }                                                  // a == 3 (mot répété) : pas de l'orthographe
                if (fx.Sugs.Count > 0) { fx.Repl = fx.Sugs[0]; list.Add(fx); }
            }
        } catch { }
        return list;
    }
}

// Petite croix rouge (8 bits) posée sur une faute ; les zones transparentes laissent passer les clics
class SpellMark : Form {
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    static readonly Color Key = Color.FromArgb(255, 1, 254), Red = Color.FromArgb(232, 56, 64), Dark = Color.FromArgb(40, 10, 14);
    readonly bool underline;
    public readonly List<Fix> Fixes;
    public SpellMark(Rectangle word, bool hasWord, List<Fix> fixes, Action<SpellMark> click) {
        Fixes = fixes; underline = hasWord;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Key; TransparencyKey = Key; Cursor = Cursors.Hand; DoubleBuffered = true;
        Bounds = hasWord ? new Rectangle(word.X - 1, word.Y, Math.Max(word.Width, 6) + 14, word.Height + 3) : new Rectangle(word.X, word.Y, 15, 15);
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) click(this); };
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; return cp; } }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } base.WndProc(ref m); }
    public void ShowNoActivate() { ShowWindow(Handle, 4); }          // SW_SHOWNOACTIVATE
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.None;
        int x = Width - 11, y = underline ? Math.Max(1, (Height - 3) / 2 - 5) : 1;   // à droite du mot, à mi-hauteur
        using (var d = new SolidBrush(Dark)) using (var r = new SolidBrush(Red)) {
            // croix pixel : contour sombre puis rouge
            for (int i = -1; i <= 9; i++) {
                int a = Math.Max(0, Math.Min(8, i));
                g.FillRectangle(d, x + a - 1, y + a - 1, 4, 4); g.FillRectangle(d, x + 8 - a - 1, y + a - 1, 4, 4);
            }
            for (int i = 0; i <= 8; i++) { g.FillRectangle(r, x + i, y + i, 2, 2); g.FillRectangle(r, x + 8 - i, y + i, 2, 2); }
            if (underline) for (int i = 1; i < Width - 14; i += 4) g.FillRectangle(r, i, Height - 2, 2, 2);   // pointillés sous le mot
        }
    }
}

class SpellAssist : Form {
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int n, uint flags, IntPtr hkl);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanEx(char ch, IntPtr hkl);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // Le Verr. Maj vu par NOTRE thread peut être faux : on le lit dans la file d'entrée de la fenêtre active
    bool capsOn;
    void RefreshCaps() {
        try {
            uint pid, other = GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
            bool att = other != 0 && other != me && AttachThreadInput(me, other, true);
            capsOn = (GetKeyState(0x14) & 1) != 0;
            if (att) AttachThreadInput(me, other, false);
        } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public RECT rc; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    static readonly string[] NoSpell = { "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "Code", "devenv", "OpenConsole", "KeePass", "KeePassXC", "1Password", "Bitwarden" };
    static readonly Color Bg = Color.FromArgb(31, 31, 35), Line = Color.FromArgb(140, 90, 70), Accent = Color.FromArgb(217, 119, 87), Muted = Color.FromArgb(150, 150, 160);

    readonly Companion owner;
    IntPtr kbHook, msHook; HookProc kbProc, msProc;
    readonly StringBuilder buf = new StringBuilder();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    readonly FlowLayoutPanel body;
    readonly List<SpellMark> marks = new List<SpellMark>();
    readonly System.Collections.Concurrent.BlockingCollection<Action> jobs = new System.Collections.Concurrent.BlockingCollection<Action>();
    char dead; DateTime lastKey; bool dirty, busy, typing; IntPtr fg; int gen;
    string checkedText;

    public SpellAssist(Companion c) {
        owner = c;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Bg; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);
        body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 8, 10, 8), BackColor = Bg, WrapContents = false };
        Controls.Add(body);
        Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
        kbProc = OnKey; msProc = OnMouse;
        timer.Tick += (s, e) => Tick();
        IntPtr forceHandle = Handle;   // le handle doit exister pour recevoir les résultats (BeginInvoke) avant tout affichage
        // UI Automation et COM hors du thread de l'interface (évite les blocages)
        var w = new Thread(() => { foreach (var job in jobs.GetConsumingEnumerable()) try { job(); } catch (Exception ex) { Program.Log("Correcteur", ex.ToString()); } }) { IsBackground = true, Name = "speller" };
        w.SetApartmentState(ApartmentState.MTA); w.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; return cp; } }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } base.WndProc(ref m); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Util.DarkWindow(Handle, true); }

    public void Enable(bool on) {
        if (on && kbHook == IntPtr.Zero) {
            IntPtr mod = GetModuleHandle(null);
            fg = GetForegroundWindow(); RefreshCaps();
            kbHook = SetWindowsHookEx(13, kbProc, mod, 0);
            msHook = SetWindowsHookEx(14, msProc, mod, 0);
            timer.Start();
        } else if (!on && kbHook != IntPtr.Zero) {
            UnhookWindowsHookEx(kbHook); UnhookWindowsHookEx(msHook);
            kbHook = msHook = IntPtr.Zero; timer.Stop(); Reset(); ClearAll();
        }
    }

    void Reset() { buf.Length = 0; dead = '\0'; dirty = false; gen++; }

    void ClearAll() {
        if (Visible) Hide();
        foreach (var m in marks) { m.Close(); m.Dispose(); }
        marks.Clear();
    }

    bool OverUs(Point pt) {
        if (Visible && Bounds.Contains(pt)) return true;
        foreach (var m in marks) if (m.Bounds.Contains(pt)) return true;
        return false;
    }

    IntPtr OnMouse(int code, IntPtr w, IntPtr l) {
        int msg = w.ToInt32();
        if (code >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20A)) {
            var pt = new Point(Marshal.ReadInt32(l), Marshal.ReadInt32(l, 4));
            if (!OverUs(pt)) {
                ClearAll();
                if (msg == 0x20A) { dirty = buf.Length > 0; lastKey = DateTime.Now; gen++; }  // défilement : on replace les croix après
                else Reset();                                                                    // clic ailleurs : le curseur a pu bouger
            }
        }
        return CallNextHookEx(msHook, code, w, l);
    }

    IntPtr OnKey(int code, IntPtr w, IntPtr l) {
        int msg = w.ToInt32();
        if (code >= 0 && (msg == 0x100 || msg == 0x104)) {
            var k = (KBD)Marshal.PtrToStructure(l, typeof(KBD));
            if ((k.flags & 0x10) == 0) try { Key(k); } catch { Reset(); }   // nos frappes simulées sont ignorées
        }
        return CallNextHookEx(kbHook, code, w, l);
    }

    void Key(KBD k) {
        var f = GetForegroundWindow();
        if (f != fg) { fg = f; Reset(); RefreshCaps(); }
        if (marks.Count > 0 || Visible) ClearAll();
        gen++;
        lastKey = DateTime.Now;
        uint vk = k.vk;
        if (vk == 0x14) { capsOn = !capsOn; return; }
        if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x14 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C) return;
        bool ctrl = GetAsyncKeyState(0x11) < 0, alt = GetAsyncKeyState(0x12) < 0, win = GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        if (vk == 0x08) { if (ctrl || alt) Reset(); else if (buf.Length > 0) { buf.Length--; dirty = true; } return; }
        if (win || (ctrl != alt)) { Reset(); return; }
        if (vk == 0x0D || vk == 0x09 || vk == 0x1B || (vk >= 0x21 && vk <= 0x28) || vk == 0x2E || vk == 0x2D) { Reset(); return; }
        var st = new byte[256];
        if (GetAsyncKeyState(0x10) < 0) st[0x10] = 0x80;
        if (capsOn) st[0x14] = 1;
        if (ctrl && alt) { st[0x11] = 0x80; st[0x12] = 0x80; }
        uint pid; uint tid = GetWindowThreadProcessId(f, out pid);
        var sb = new StringBuilder(8);
        int n = ToUnicodeEx(vk, k.scan, st, sb, 8, 4, GetKeyboardLayout(tid));
        if (n < 0) { dead = sb.Length > 0 ? sb[0] : '\0'; return; }
        if (n == 0) return;
        string s = sb.ToString(0, Math.Min(n, sb.Length));
        if (dead != '\0') { s = Compose(dead, s); dead = '\0'; }
        if (s.Length == 0 || char.IsControl(s[0])) return;
        if (buf.Length > 3000) Reset();
        buf.Append(s); dirty = true;
    }

    static string Compose(char d, string s) {
        string comb = d == '^' ? "̂" : d == '¨' ? "̈" : d == '`' ? "̀" : d == '´' ? "́" : d == '~' ? "̃" : null;
        if (s == " ") return d.ToString();
        if (comb == null) return d + s;
        string r = (s + comb).Normalize(NormalizationForm.FormC);
        return r.Length == 1 ? r : d + s;
    }

    bool ExcludedProcess() {
        try {
            uint pid; GetWindowThreadProcessId(fg, out pid);
            if (pid == Program.Pid) return false;
            string name = Process.GetProcessById((int)pid).ProcessName;
            foreach (var x in NoSpell) if (string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) return true;
        } catch { }
        return false;
    }

    void Tick() {
        if (busy || typing || !dirty || buf.Length < 3 || (DateTime.Now - lastKey).TotalMilliseconds < 1300) return;
        dirty = false;
        if (ExcludedProcess()) { Reset(); return; }
        string text = buf.ToString(); int myGen = gen; IntPtr myFg = fg;
        Point caret; bool hasCaret = CaretPos(out caret);
        busy = true;
        jobs.Add(() => {
            bool password; Rectangle[] rects = null; List<Fix> fixes = null;
            password = IsPassword();
            if (!password) { fixes = Speller.Check(text); if (fixes.Count > 0) rects = Locate(text, fixes); }
            BeginInvoke((Action)(() => {
                busy = false;
                if (password) { Reset(); return; }
                if (myGen != gen || fg != myFg || fixes == null || fixes.Count == 0) return;   // l'utilisateur a recommencé à taper
                checkedText = text;
                ShowMarks(fixes, rects, hasCaret ? caret : new Point(owner.Left + owner.Width / 2, owner.Top - 16));
            }));
        });
    }

    bool CaretPos(out Point p) {
        p = Point.Empty;
        try {
            uint pid; var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (GetGUIThreadInfo(GetWindowThreadProcessId(fg, out pid), ref gi) && gi.caret != IntPtr.Zero && gi.rc.B > gi.rc.T) {
                p = new Point(gi.rc.R + 2, gi.rc.T - 12); return ClientToScreen(gi.caret, ref p);
            }
        } catch { }
        return false;
    }

    // ---- thread "speller" ----
    static bool IsPassword() {
        try { var el = System.Windows.Automation.AutomationElement.FocusedElement; return el != null && el.Current.IsPassword; } catch { return false; }
    }

    // Retrouve l'emplacement à l'écran de chaque faute : on part du curseur de texte et on recule
    static Rectangle[] Locate(string text, List<Fix> fixes) {
        var res = new Rectangle[fixes.Count];
        try {
            var el = System.Windows.Automation.AutomationElement.FocusedElement; if (el == null) return res;
            object o;
            if (!el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out o)) return res;
            var tp = (System.Windows.Automation.TextPattern)o;
            var sel = tp.GetSelection(); if (sel == null || sel.Length == 0) return res;
            var caret = sel[0];
            var S = System.Windows.Automation.Text.TextPatternRangeEndpoint.Start;
            var E = System.Windows.Automation.Text.TextPatternRangeEndpoint.End;
            var ch = System.Windows.Automation.Text.TextUnit.Character;
            for (int i = 0; i < fixes.Count; i++) {
                var f = fixes[i]; var r = caret.Clone();
                r.MoveEndpointByRange(S, caret, E);
                int back = text.Length - f.Start;
                r.MoveEndpointByUnit(S, ch, -back);
                r.MoveEndpointByUnit(E, ch, -(back - f.Len));
                if (r.GetText(200) != f.Word) continue;                         // pas sûr de l'endroit : pas de croix
                var rects = r.GetBoundingRectangles();
                if (rects == null || rects.Length == 0 || rects[0].Width <= 0) continue;
                var b = rects[0];
                res[i] = new Rectangle((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height));
            }
        } catch { }
        return res;
    }

    // ---- affichage ----
    void ShowMarks(List<Fix> fixes, Rectangle[] rects, Point fallback) {
        ClearAll();
        var loose = new List<Fix>();
        for (int i = 0; i < fixes.Count; i++) {
            if (rects != null && !rects[i].IsEmpty) AddMark(new SpellMark(rects[i], true, new List<Fix> { fixes[i] }, OnMarkClick));
            else loose.Add(fixes[i]);
        }
        // l'app ne dit pas où sont les mots : une seule croix près du curseur, qui liste les fautes
        if (loose.Count > 0) AddMark(new SpellMark(new Rectangle(fallback.X, fallback.Y, 15, 15), false, loose, OnMarkClick));
    }

    void AddMark(SpellMark m) { marks.Add(m); m.ShowNoActivate(); }

    void OnMarkClick(SpellMark m) {
        body.SuspendLayout(); body.Controls.Clear();
        if (m.Fixes.Count == 1) {
            var f = m.Fixes[0];
            body.Controls.Add(new Label { Text = "« " + f.Word + " »", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 0, 0, 4) });
            foreach (var s in f.Sugs) { var sug = s; body.Controls.Add(Chip(sug, Color.White, Accent, (a, b) => { f.Repl = sug; Apply(new List<Fix> { f }); })); }
            body.Controls.Add(Chip("Ignorer ce mot", Muted, Bg, (a, b) => { Speller.Ignore(f.Word); m.Hide(); marks.Remove(m); Hide(); }));
        } else {
            body.Controls.Add(new Label { Text = Settings.Name + " · orthographe", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, 4) });
            foreach (var fx in m.Fixes) { var one = fx; body.Controls.Add(Chip(one.Word + "  →  " + one.Repl, Color.White, Color.FromArgb(44, 44, 50), (a, b) => Apply(new List<Fix> { one }))); }
            body.Controls.Add(Chip("Tout corriger (" + m.Fixes.Count + ")", Color.White, Accent, (a, b) => Apply(new List<Fix>(m.Fixes))));
            body.Controls.Add(Chip("Ignorer", Muted, Bg, (a, b) => { foreach (var x in m.Fixes) Speller.Ignore(x.Word); ClearAll(); }));
        }
        body.ResumeLayout();
        var sz = GetPreferredSize(Size.Empty);
        var p = new Point(m.Right - 12, m.Top + 14);
        if (m.Fixes.Count == 1) p = new Point(m.Left, m.Bottom + 2);
        var wa = Screen.FromPoint(p).WorkingArea;
        if (p.Y + sz.Height > wa.Bottom) p.Y = m.Top - sz.Height - 2;
        p.X = Math.Max(wa.Left + 4, Math.Min(p.X, wa.Right - sz.Width - 4));
        Location = p;
        if (!Visible) ShowWindow(Handle, 4);
        Invalidate();
    }

    Label Chip(string text, Color fore, Color back, EventHandler click) {
        var l = new Label { Text = text, AutoSize = true, ForeColor = fore, BackColor = back, Font = new Font("Segoe UI", 9.5f), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 3, 0, 3), Cursor = Cursors.Hand };
        l.Click += click;
        return l;
    }

    void Apply(List<Fix> which) {
        ClearAll();
        if (buf.ToString() != checkedText || GetForegroundWindow() != fg) { Reset(); return; }
        which.Sort((a, b) => b.Start.CompareTo(a.Start));
        string t = checkedText;
        foreach (var f in which) t = t.Substring(0, f.Start) + f.Repl + t.Substring(f.Start + f.Len);
        int pre = 0;
        while (pre < t.Length && pre < checkedText.Length && t[pre] == checkedText[pre]) pre++;
        int del = checkedText.Length - pre; string add = t.Substring(pre);
        int size = Marshal.SizeOf(typeof(INPUT));
        typing = true;
        // Envoi touche par touche, avec un petit délai (frappe rapide « humaine ») : les apps modernes
        // comme le Bloc-notes perdent ou mélangent les touches envoyées en rafale.
        new Thread(() => {
            try {
                Thread.Sleep(120);                                         // laisse l app finir de traiter le clic
                for (int i = 0; i < del; i++) {
                    SendInput(2, new[] { new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x08 } }, new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x08, flags = 2 } } }, size);
                    Thread.Sleep(12);
                }
                Thread.Sleep(30);
                foreach (char ch in add) {
                    SendInput(2, new[] { new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 4 } }, new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 4 | 2 } } }, size);
                    Thread.Sleep(10);
                }
            } catch (Exception ex) { Program.Log("Correction", ex.ToString()); }
            try { BeginInvoke((Action)(() => { typing = false; dirty = true; lastKey = DateTime.Now.AddMilliseconds(-900); })); } catch { }
        }) { IsBackground = true, Name = "frappe" }.Start();
        buf.Length = 0; buf.Append(t); checkedText = t; gen++;
        owner.SetState("happy");
    }

    // Test : fait comme si "text" venait d'être tapé dans la fenêtre active
    public void Demo(string text) {
        fg = GetForegroundWindow(); RefreshCaps(); buf.Length = 0; buf.Append(text);
        dirty = true; lastKey = DateTime.Now.AddSeconds(-5); timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { Enable(false); base.OnFormClosing(e); }
}

// ---------- Calendriers (abonnements iCal / webcal) ----------
class CalEvent { public string Title = "", Loc = "", Uid = "", Cal = "", Color = ""; public DateTime Start, End; public bool AllDay, ForList, FromMail, Manual; public string Kind = ""; }

static class Calendars {
    static readonly object L = new object();
    static List<CalEvent> events = new List<CalEvent>();
    static string status = "";
    static bool running;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "calendriers");
    static readonly string[] Palette = { "#d97757", "#5fa8d3", "#8fbf6a", "#c38ad6", "#e0b84f", "#5ec4b6" };

    public static string TestIcs;   // tests uniquement
    public static ArrayList Config() {
        if (TestIcs != null) return new ArrayList { new Dictionary<string, object> { { "name", "Test" }, { "url", TestIcs }, { "list", true } } };
        try { var a = new JavaScriptSerializer().Deserialize<ArrayList>(Settings.Str("Calendars", "[]")); return a ?? new ArrayList(); }
        catch { return new ArrayList(); }
    }
    public static string ColorOf(int i) { return Palette[i % Palette.Length]; }

    public static void Refresh(Action done) {
        lock (L) { if (running) return; running = true; }
        new Thread(() => {
            var all = new List<CalEvent>(); var errs = new List<string>();
            DateTime from = DateTime.Today.AddDays(-45), to = DateTime.Today.AddDays(180);
            try {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                Directory.CreateDirectory(Dir);
                var cfg = Config();
                for (int i = 0; i < cfg.Count; i++) {
                    var c = cfg[i] as Dictionary<string, object>; if (c == null) continue;
                    string name = Convert.ToString(c.ContainsKey("name") ? c["name"] : "Calendrier");
                    string url = Convert.ToString(c.ContainsKey("url") ? c["url"] : "").Trim();
                    bool list = !c.ContainsKey("list") || Convert.ToBoolean(c["list"]);
                    if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.Substring(9);
                    if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) try { url = new Uri(url).LocalPath; } catch { }
                    bool local = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    if (local && !File.Exists(url)) { errs.Add(name + " : lien invalide"); continue; }
                    string cache = Path.Combine(Dir, (url.GetHashCode() & 0x7fffffff) + ".ics"), ics = null;
                    if (local) ics = File.ReadAllText(url, Encoding.UTF8);
                    else try {
                        using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; wc.Headers["User-Agent"] = "Bebene-Calendar/1.0"; ics = wc.DownloadString(url); }
                        File.WriteAllText(cache, ics, Encoding.UTF8);
                    } catch (Exception ex) {
                        if (File.Exists(cache)) { ics = File.ReadAllText(cache, Encoding.UTF8); errs.Add(name + " : hors ligne, copie locale utilisée"); }
                        else { errs.Add(name + " : " + ex.Message); continue; }
                    }
                    if (ics.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) < 0) { errs.Add(name + " : ce lien n'est pas un calendrier iCal"); continue; }
                    foreach (var e in Parse(ics, from, to)) { e.Cal = name; e.Color = ColorOf(i); e.ForList = list; e.Kind = Classify(e.Title); all.Add(e); }
                }
            } catch (Exception ex) { errs.Add(ex.Message); Program.Log("Calendriers", ex.ToString()); }
            all.Sort((a, b) => a.Start.CompareTo(b.Start));
            lock (L) { events = all; status = string.Join(" · ", errs.ToArray()); running = false; }
            if (done != null) try { done(); } catch { }
        }) { IsBackground = true, Name = "calendriers" }.Start();
    }

    public static List<CalEvent> Snapshot() {          // calendriers + ce qui a été trouvé dans les mails (sans doublon le même jour)
        List<CalEvent> r; lock (L) r = new List<CalEvent>(events);
        var keys = new HashSet<string>(); foreach (var e in r) if (e.Kind.Length > 0) keys.Add(e.Kind + e.Start.ToString("yyyyMMdd"));
        foreach (var e in Mailbox.Snapshot()) if (!keys.Contains(e.Kind + e.Start.ToString("yyyyMMdd"))) r.Add(e);
        foreach (var e in Manual.Events()) r.Add(e);                                   // ajoutés à la main
        var hid = Manual.Hidden(); if (hid.Count > 0) r.RemoveAll(e => hid.Contains(Reminders.Key(e)));   // retirés à la main
        r.Sort((x, y) => x.Start.CompareTo(y.Start)); return r;
    }
    public static string Json() {
        var list = new List<Dictionary<string, object>>();
        lock (L) {
            foreach (var e in events) list.Add(new Dictionary<string, object> {
                { "t", e.Title }, { "l", e.Loc }, { "c", e.Cal }, { "col", e.Color }, { "a", e.AllDay }, { "u", e.ForList }, { "k", e.Kind },
                { "s", e.Start.ToString("yyyy-MM-ddTHH:mm:ss") }, { "e", e.End.ToString("yyyy-MM-ddTHH:mm:ss") } });
            var d = new Dictionary<string, object> { { "events", list }, { "status", status }, { "filter", Settings.Str("LocFilter", "") }, { "count", Config().Count } };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d);
        }
    }

    public static string Norm(string s) {
        var sb = new StringBuilder();
        foreach (char ch in (s ?? "").Normalize(NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }
    static readonly string[] ExamWords = { "examen", "exam", "partiel", "controle", "evaluation", "epreuve", "soutenance", "quiz", "midterm", "final", "test ", "oral ", "te ", "ec " };
    static readonly string[] RdvWords = { "rendez-vous", "rendez vous", "rdv", "medecin", "docteur", "dr ", "dentiste", "entretien", "consultation", "rendezvous", "appointment", "kine", "physio", "coiffeur", "ophtalm", "gyneco", "osteo", "psy", "banque", "notaire", "garage" };
    public static string Classify(string title) {
        string t = " " + Norm(title) + " ";
        foreach (var w in ExamWords) if (t.Contains(w.EndsWith(" ") ? " " + w : w)) return "exam";
        foreach (var w in RdvWords) if (t.Contains(w.EndsWith(" ") ? " " + w : w)) return "rdv";
        return "";
    }

    // ---- lecture d'un fichier iCal (RFC 5545, l'essentiel : dates, fuseaux, récurrences simples, exceptions) ----
    static List<CalEvent> Parse(string ics, DateTime from, DateTime to) {
        var res = new List<CalEvent>();
        string text = ics.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        var masters = new List<Dictionary<string, List<string[]>>>();
        var overrides = new HashSet<string>();
        Dictionary<string, List<string[]>> cur = null;
        foreach (var raw in text.Split('\n')) {
            string line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { cur = new Dictionary<string, List<string[]>>(); continue; }
            if (line == "END:VEVENT") { if (cur != null) masters.Add(cur); cur = null; continue; }
            if (cur == null) continue;
            int colon = -1; bool q = false;
            for (int i = 0; i < line.Length; i++) { if (line[i] == '"') q = !q; else if (line[i] == ':' && !q) { colon = i; break; } }
            if (colon < 0) continue;
            string head = line.Substring(0, colon), val = line.Substring(colon + 1);
            int semi = head.IndexOf(';');
            string name = (semi < 0 ? head : head.Substring(0, semi)).ToUpperInvariant(), pars = semi < 0 ? "" : head.Substring(semi + 1);
            if (!cur.ContainsKey(name)) cur[name] = new List<string[]>();
            cur[name].Add(new[] { pars, val });
        }
        foreach (var ev in masters)
            if (ev.ContainsKey("RECURRENCE-ID") && ev.ContainsKey("UID")) {
                bool ad; var rid = ParseDate(ev["RECURRENCE-ID"][0], out ad);
                if (rid.HasValue) overrides.Add(ev["UID"][0][1] + "|" + rid.Value.ToString("yyyyMMddHHmm"));
            }
        foreach (var ev in masters) {
            if (!ev.ContainsKey("DTSTART")) continue;
            if (Get(ev, "STATUS").ToUpperInvariant() == "CANCELLED") continue;
            bool allDay; var st = ParseDate(ev["DTSTART"][0], out allDay); if (!st.HasValue) continue;
            DateTime start = st.Value, end;
            bool ad2; DateTime? en = ev.ContainsKey("DTEND") ? ParseDate(ev["DTEND"][0], out ad2) : null;
            if (en.HasValue) end = en.Value;
            else if (ev.ContainsKey("DURATION")) end = start + ParseDuration(ev["DURATION"][0][1]);
            else end = allDay ? start.AddDays(1) : start.AddHours(1);
            var len = end - start;
            string uid = Get(ev, "UID"), title = Unescape(Get(ev, "SUMMARY")), loc = Unescape(Get(ev, "LOCATION"));
            bool isOverride = ev.ContainsKey("RECURRENCE-ID");
            var ex = new HashSet<string>();
            if (ev.ContainsKey("EXDATE")) foreach (var x in ev["EXDATE"]) foreach (var v in x[1].Split(',')) {
                bool a; var d = ParseDate(new[] { x[0], v }, out a); if (d.HasValue) ex.Add(d.Value.ToString("yyyyMMddHHmm"));
            }
            Action<DateTime> add = s => {
                string key = s.ToString("yyyyMMddHHmm");
                if (ex.Contains(key)) return;
                if (!isOverride && overrides.Contains(uid + "|" + key) && s != start) return;
                if (s + len < from || s > to) return;
                res.Add(new CalEvent { Title = title, Loc = loc, Uid = uid, Start = s, End = s + len, AllDay = allDay });
            };
            string rr = isOverride ? "" : Get(ev, "RRULE");
            if (rr.Length == 0) { add(start); continue; }
            var r = new Dictionary<string, string>();
            foreach (var part in rr.Split(';')) { int k = part.IndexOf('='); if (k > 0) r[part.Substring(0, k).ToUpperInvariant()] = part.Substring(k + 1); }
            string freq = r.ContainsKey("FREQ") ? r["FREQ"] : "";
            int interval = 1, count = int.MaxValue; int.TryParse(r.ContainsKey("INTERVAL") ? r["INTERVAL"] : "1", out interval); if (interval < 1) interval = 1;
            if (r.ContainsKey("COUNT")) int.TryParse(r["COUNT"], out count);
            DateTime until = to;
            if (r.ContainsKey("UNTIL")) { bool a; var u = ParseDate(new[] { "", r["UNTIL"] }, out a); if (u.HasValue && u.Value < until) until = allDay ? u.Value.AddDays(1).AddTicks(-1) : u.Value; }
            var days = new List<DayOfWeek>();
            if (r.ContainsKey("BYDAY")) foreach (var d in r["BYDAY"].Split(',')) {
                string dd = d.Length >= 2 ? d.Substring(d.Length - 2) : d;
                int idx = Array.IndexOf(new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }, dd.ToUpperInvariant()); if (idx >= 0) days.Add((DayOfWeek)idx);
            }
            int n = 0, guard = 0;
            if (freq == "WEEKLY") {
                if (days.Count == 0) days.Add(start.DayOfWeek);
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var w = weekStart; w <= until && n < count && guard++ < 2000; w = w.AddDays(7 * interval))
                    for (int i = 0; i < 7 && n < count; i++) {
                        var day = w.AddDays(i); if (!days.Contains(day.DayOfWeek)) continue;
                        var s = day + start.TimeOfDay; if (s < start) continue; if (s > until) break;
                        n++; add(s);
                    }
            } else if (freq == "MONTHLY" && r.ContainsKey("BYDAY")) {        // ex. 2TU = 2e mardi du mois, -1FR = dernier vendredi
                string[] codes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
                for (var mo = new DateTime(start.Year, start.Month, 1); mo <= until && n < count && guard++ < 600; mo = mo.AddMonths(interval)) {
                    var hits = new List<DateTime>();
                    foreach (var bd in r["BYDAY"].Split(',')) {
                        var mm = Regex.Match(bd.Trim().ToUpperInvariant(), @"^([+-]?\d+)?(SU|MO|TU|WE|TH|FR|SA)$"); if (!mm.Success) continue;
                        var dow = (DayOfWeek)Array.IndexOf(codes, mm.Groups[2].Value);
                        var inMonth = new List<DateTime>(); for (var x = mo; x.Month == mo.Month; x = x.AddDays(1)) if (x.DayOfWeek == dow) inMonth.Add(x);
                        int ord = mm.Groups[1].Success ? int.Parse(mm.Groups[1].Value) : 0;
                        if (ord == 0) hits.AddRange(inMonth);
                        else { int ix = ord > 0 ? ord - 1 : inMonth.Count + ord; if (ix >= 0 && ix < inMonth.Count) hits.Add(inMonth[ix]); }
                    }
                    hits.Sort();
                    foreach (var hd in hits) { var hs = hd + start.TimeOfDay; if (hs < start) continue; if (hs > until || n >= count) break; n++; add(hs); }
                }
            } else {
                for (var s = start; s <= until && n < count && guard++ < 3000; n++) {
                    add(s);
                    s = freq == "DAILY" ? s.AddDays(interval) : freq == "MONTHLY" ? s.AddMonths(interval) : freq == "YEARLY" ? s.AddYears(interval) : DateTime.MaxValue.AddDays(-1);
                    if (s.Year > 9000) break;
                }
            }
        }
        return res;
    }

    static string Get(Dictionary<string, List<string[]>> ev, string k) { return ev.ContainsKey(k) ? ev[k][0][1] : ""; }
    static string Unescape(string s) { return s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim(); }

    static DateTime? ParseDate(string[] pv, out bool allDay) {
        string v = pv[1].Trim(); allDay = pv[0].ToUpperInvariant().Contains("VALUE=DATE") && !pv[0].ToUpperInvariant().Contains("DATE-TIME");
        try {
            if (v.Length == 8) { allDay = true; return DateTime.ParseExact(v, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture); }
            bool utc = v.EndsWith("Z"); if (utc) v = v.Substring(0, v.Length - 1);
            var d = DateTime.ParseExact(v.Substring(0, 15), "yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (utc) return DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
            var m = Regex.Match(pv[0], "TZID=\"?([^;\"]+)");            // fuseau nommé → heure locale du PC
            if (m.Success) {
                try { var tz = TimeZoneInfo.FindSystemTimeZoneById(WinZone(m.Groups[1].Value)); return TimeZoneInfo.ConvertTime(d, tz, TimeZoneInfo.Local); } catch { }
            }
            return d;
        } catch { return null; }
    }
    static string WinZone(string iana) {
        switch (iana) {
            case "Europe/Zurich": case "Europe/Paris": case "Europe/Berlin": case "Europe/Brussels": case "Europe/Rome": case "Europe/Madrid": case "Europe/Amsterdam": case "Europe/Vienna":
                return "Romance Standard Time";
            case "Europe/London": return "GMT Standard Time";
            case "America/New_York": return "Eastern Standard Time";
            case "UTC": case "Etc/UTC": return "UTC";
            default: return iana;
        }
    }
    static TimeSpan ParseDuration(string v) {
        var m = Regex.Match(v, @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        Func<int, int> g = i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return m.Success ? new TimeSpan(g(1) * 7 + g(2), g(3), g(4), g(5)) : TimeSpan.FromHours(1);
    }
}

// ---------- Volets « Calendrier » (gauche) et « Dates importantes » (droite) : fenêtres 100 % transparentes ----------
// Seuls le calendrier et les cartes de dates sont dessinés ; le reste laisse voir le bureau et passer les clics.
abstract class Layered : Form {
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point pos, ref Size sz, IntPtr src, ref Point srcPos, int key, ref BLEND b, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    protected static readonly Color CardBg = Color.FromArgb(238, 22, 22, 27), CardLine = Color.FromArgb(70, 235, 154, 120),
        Txt = Color.FromArgb(236, 236, 239), Muted = Color.FromArgb(150, 150, 160), Accent = Color.FromArgb(217, 119, 87),
        Accent2 = Color.FromArgb(235, 154, 120), Raised = Color.FromArgb(255, 44, 44, 52), ExamC = Color.FromArgb(232, 80, 74), RdvC = Color.FromArgb(95, 168, 211);
    protected static readonly Font FTitle = new Font("Segoe UI Semibold", 11.5f), FText = new Font("Segoe UI", 9.5f), FBold = new Font("Segoe UI Semibold", 9.5f),
        FSmall = new Font("Segoe UI", 8.5f), FBig = new Font("Segoe UI Semibold", 13f), FTag = new Font("Segoe UI Semibold", 7.5f);
    protected static readonly System.Globalization.CultureInfo FR = new System.Globalization.CultureInfo("fr-CH");

    readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
    protected Layered() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; return cp; } }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } base.WndProc(ref m); }

    protected abstract Size Measure();
    protected abstract void Render(Graphics g);
    protected void Hit(Rectangle r, Action a) { hits.Add(new KeyValuePair<Rectangle, Action>(r, a)); }

    public void ShowAt(Point p) { Place(p, true); }
    // redraw = false : simple déplacement (le contenu déjà dessiné suit la fenêtre), beaucoup plus léger
    public void Place(Point p, bool redraw) {
        if (redraw || !drawn) { Redraw(p); drawn = true; } else if (Location != p) Location = p;
        if (!Visible) ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
    }
    bool drawn;
    public void Redraw() { Redraw(Location); }
    public Bitmap Snapshot() {   // pour les tests : rendu dans une image
        var sz = Measure(); var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; Render(g); }
        return bmp;
    }
    void Redraw(Point p) {
        var sz = Measure();
        if (Size != sz) Size = sz;
        Location = p;
        hits.Clear();
        using (var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Render(g);
            }
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
            var size = sz; var src = Point.Empty; var pos = p;
            var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref bl, 2);
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }
    // glisser pour déplacer (Moved = l utilisateur l a placée lui-même)
    public bool Moved; protected bool Draggable = true; Point downAt; bool dragging, down;
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; dragging = false; downAt = e.Location; } }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (down && Draggable && (dragging || Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > 5)) {
            dragging = true; var c = Cursor.Position; Location = new Point(c.X - downAt.X, c.Y - downAt.Y); Cursor = Cursors.SizeAll; return;
        }
        bool over = false; foreach (var h in hits) if (h.Key.Contains(e.Location)) over = true;
        Cursor = over ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        down = false; if (dragging) { dragging = false; Moved = true; Cursor = Cursors.Default; return; }
        for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(e.Location)) { hits[i].Value(); return; }
    }

    // ---- petits outils de dessin ----
    protected static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }
    protected static void Card(Graphics g, RectangleF r, float rad) {
        using (var sh = Round(new RectangleF(r.X, r.Y + 3, r.Width, r.Height), rad)) using (var b = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillPath(b, sh);
        using (var p = Round(r, rad)) {
            using (var b = new SolidBrush(CardBg)) g.FillPath(b, p);
            using (var pen = new Pen(CardLine)) g.DrawPath(pen, p);
        }
    }
    protected static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near) {
        using (var b = new SolidBrush(c)) using (var sf = new StringFormat { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(s ?? "", f, b, r, sf);
    }
    protected static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1); }
}

// La flèche cliquable collée à la discussion
class ArrowTab : Layered {
    readonly bool leftSide; public bool Open; public Action Clicked;
    public ArrowTab(bool left) { leftSide = left; Draggable = false; }
    protected override Size Measure() { return new Size(26, 48); }
    // double chevron orange « ou » avec un léger rayonnement, sans fond
    protected override void Render(Graphics g) {
        using (var b = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(b, 0, 0, 26, 48);   // zone cliquable invisible
        bool pointLeft = leftSide ? !Open : Open;
        float cy = 24, h = 6.5f, w = 5f, d = pointLeft ? -1 : 1;
        var lines = new List<PointF[]>();
        foreach (float cx in new[] { 10f, 17f })
            lines.Add(new[] { new PointF(cx - d * w / 2, cy - h), new PointF(cx + d * w / 2, cy), new PointF(cx - d * w / 2, cy + h) });
        int a = Open ? 255 : 210;
        foreach (var gl in new[] { new { W = 9f, A = 18 }, new { W = 6f, A = 34 }, new { W = 4f, A = 60 } })   // halo
            using (var pen = new Pen(Color.FromArgb(gl.A * a / 255, 240, 140, 90), gl.W) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                foreach (var l in lines) g.DrawLines(pen, l);
        using (var pen = new Pen(Color.FromArgb(a, Open ? 255 : 240, Open ? 170 : 145, Open ? 120 : 95), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            foreach (var l in lines) g.DrawLines(pen, l);
        Hit(new Rectangle(0, 0, 26, 48), () => { if (Clicked != null) Clicked(); });
    }
}

// Volet gauche : le calendrier du mois + les événements du jour choisi
class CalWidget : Layered {
    public int MonthOffset; public DateTime Sel = DateTime.Today; public Action Changed;
    const int W = 300, Cell = 36;
    List<CalEvent> DayEvents(List<CalEvent> all, DateTime d) {
        var r = new List<CalEvent>(); DateTime a = d.Date, b = a.AddDays(1);
        foreach (var e in all) if (e.Start < b && (e.End > a || e.Start == a)) r.Add(e);
        return r;
    }
    protected override Size Measure() {
        int n = Math.Max(1, Math.Min(5, DayEvents(Calendars.Snapshot(), Sel).Count));
        return new Size(W, 16 + 40 + 24 + 6 * Cell + 14 + 22 + n * 40 + 14);
    }
    protected override void Render(Graphics g) {
        var all = Calendars.Snapshot(); var sz = Measure();
        Card(g, new RectangleF(4, 2, sz.Width - 8, sz.Height - 10), 16);
        var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(MonthOffset);
        DrawText(g, Cap(m.ToString("MMMM yyyy", FR)), FTitle, Txt, new RectangleF(20, 14, 180, 30));
        var prev = new Rectangle(W - 82, 16, 28, 26); var next = new Rectangle(W - 48, 16, 28, 26);
        DrawText(g, "‹", FBig, Muted, prev, StringAlignment.Center); DrawText(g, "›", FBig, Muted, next, StringAlignment.Center);
        Hit(prev, () => { MonthOffset--; Changed(); }); Hit(next, () => { MonthOffset++; Changed(); });
        string[] wd = { "lu", "ma", "me", "je", "ve", "sa", "di" };
        int x0 = 18, y0 = 56, cw = (W - 36) / 7;
        for (int i = 0; i < 7; i++) DrawText(g, wd[i], FSmall, Muted, new RectangleF(x0 + i * cw, y0, cw, 20), StringAlignment.Center);
        var first = m.AddDays(-(((int)m.DayOfWeek + 6) % 7));
        for (int i = 0; i < 42; i++) {
            var d = first.AddDays(i); var rc = new Rectangle(x0 + (i % 7) * cw + 2, y0 + 24 + (i / 7) * Cell + 1, cw - 4, Cell - 2);
            var de = DayEvents(all, d);
            if (d == DateTime.Today) using (var p = Round(rc, 9)) using (var b = new SolidBrush(Color.FromArgb(55, Accent))) g.FillPath(b, p);
            if (d == Sel.Date) using (var p = Round(rc, 9)) using (var pen = new Pen(Accent, 1.5f)) g.DrawPath(pen, p);
            Color c = d.Month != m.Month ? Color.FromArgb(90, 90, 98) : d == DateTime.Today ? Accent2 : Txt;
            DrawText(g, d.Day.ToString(), d == DateTime.Today ? FBold : FText, c, new RectangleF(rc.X, rc.Y - 3, rc.Width, rc.Height - 6), StringAlignment.Center);
            var cols = new List<string>(); bool exam = false;
            foreach (var e in de) { if (!cols.Contains(e.Color)) cols.Add(e.Color); if (e.Kind == "exam" && e.ForList) exam = true; }
            for (int k = 0; k < Math.Min(3, cols.Count); k++)
                using (var b = new SolidBrush(ColorTranslator.FromHtml(cols[k]))) g.FillEllipse(b, rc.X + rc.Width / 2f - Math.Min(3, cols.Count) * 3 + k * 6 + 1, rc.Bottom - 8, 4, 4);
            if (exam) using (var b = new SolidBrush(ExamC)) g.FillEllipse(b, rc.Right - 9, rc.Y + 4, 5, 5);
            var day = d; Hit(rc, () => { Sel = day; Changed(); });
        }
        int y = y0 + 24 + 6 * Cell + 10;
        DrawText(g, Cap(Sel.ToString("dddd d MMMM", FR)), FSmall, Muted, new RectangleF(20, y, W - 40, 20)); y += 24;
        var list = DayEvents(all, Sel);
        if (list.Count == 0) DrawText(g, "Rien de prévu.", FText, Muted, new RectangleF(20, y, W - 40, 30));
        for (int i = 0; i < Math.Min(5, list.Count); i++) {
            var e = list[i]; var r = new RectangleF(16, y + i * 40, W - 32, 34);
            using (var p = Round(r, 9)) using (var b = new SolidBrush(Color.FromArgb(255, 34, 34, 40))) g.FillPath(b, p);
            using (var b = new SolidBrush(ColorTranslator.FromHtml(e.Color))) g.FillRectangle(b, r.X + 6, r.Y + 7, 3, r.Height - 14);
            DrawText(g, e.Title, FBold, Txt, new RectangleF(r.X + 16, r.Y + 1, r.Width - 22, 18));
            string t = e.AllDay ? "Toute la journée" : e.Start.ToString("HH:mm") + "–" + e.End.ToString("HH:mm");
            DrawText(g, t + (e.Loc.Length > 0 ? " · " + e.Loc.Replace("\n", " ") : ""), FSmall, Muted, new RectangleF(r.X + 16, r.Y + 16, r.Width - 22, 17));
        }
    }
}

// Volet droit : deux listes séparées « Examens » et « Rendez-vous » (calendriers + mails), sans fond
class UpWidget : Layered {
    public Action OpenSettings;
    const int W = 330, H = 58, Gap = 8, Head = 26, PerKind = 5;
    List<CalEvent> Items(string kind) {
        var r = new List<CalEvent>(); string f = Calendars.Norm(Settings.Str("LocFilter", "")); var now = DateTime.Now;
        foreach (var e in Calendars.Snapshot())
            if (e.ForList && e.Kind == kind && e.End >= now && (f.Length == 0 || Calendars.Norm(e.Loc).Contains(f) || Calendars.Norm(e.Title).Contains(f))) { r.Add(e); if (r.Count == PerKind) break; }
        return r;
    }
    bool Nothing() { return Calendars.Config().Count == 0 && !Mailbox.GmailReady && !Mailbox.OutlookOn; }
    protected override Size Measure() {
        if (Nothing()) return new Size(W, Head + H + 6);
        return new Size(W, 2 * Head + (Math.Max(1, Items("exam").Count) + Math.Max(1, Items("rdv").Count)) * (H + Gap) + 58);
    }
    void Title(Graphics g, string t, int y) {
        DrawText(g, t, FTag, Color.FromArgb(170, 0, 0, 0), new RectangleF(7, y + 1, W, 18));   // ombre : lisible sur n'importe quel fond
        DrawText(g, t, FTag, Color.White, new RectangleF(6, y, W, 18));
    }
    protected override void Render(Graphics g) {
        int y = 6;
        if (Nothing()) {
            Title(g, "EXAMENS & RENDEZ-VOUS", y); y += Head - 6;
            var r = new RectangleF(4, y, W - 8, H); Card(g, r, 14);
            DrawText(g, "Ajoute tes calendriers ou ta boîte mail dans les réglages ›", FText, Accent2, new RectangleF(r.X + 14, r.Y, r.Width - 20, r.Height));
            Hit(Rectangle.Round(r), () => { if (OpenSettings != null) OpenSettings(); });
            return;
        }
        foreach (var kind in new[] { "exam", "rdv" }) {
            var items = Items(kind);
            Title(g, kind == "exam" ? "EXAMENS" : "RENDEZ-VOUS", y); y += Head - 6;
            if (items.Count == 0) {
                var r0 = new RectangleF(4, y, W - 8, H - 18); Card(g, r0, 12);
                DrawText(g, kind == "exam" ? "Aucun examen à venir." : "Aucun rendez-vous à venir.", FText, Muted, new RectangleF(r0.X + 14, r0.Y, r0.Width - 20, r0.Height));
                y += H - 18 + Gap + 10; continue;
            }
            foreach (var e in items) { DrawCard(g, e, y); y += H + Gap; }
            y += 10;
        }
        // « + Ajouter » : ouvre les réglages sur la section des examens et rendez-vous
        var ar = new RectangleF(4, y - 4, 210, 24);
        using (var p = Round(ar, 12)) using (var b = new SolidBrush(Color.FromArgb(200, 26, 26, 31))) g.FillPath(b, p);
        using (var p = Round(ar, 12)) using (var pen = new Pen(CardLine)) g.DrawPath(pen, p);
        DrawText(g, "＋  Ajouter un examen ou un RDV", FSmall, Accent2, ar, StringAlignment.Center);
        Hit(Rectangle.Round(ar), () => { if (OpenSettings != null) OpenSettings(); });
        y += 26;
        string st = Mailbox.Running ? "✉ analyse des mails…" : "";
        if (st.Length > 0) { DrawText(g, st, FSmall, Color.FromArgb(170, 0, 0, 0), new RectangleF(7, y - 3, W, 16)); DrawText(g, st, FSmall, Color.White, new RectangleF(6, y - 4, W, 16)); }
    }
    void DrawCard(Graphics g, CalEvent e, int y) {
        var r = new RectangleF(4, y, W - 8, H);
        Card(g, r, 14);
        var dr = new RectangleF(r.X + 9, r.Y + 8, 42, 42);
        using (var p = Round(dr, 10)) using (var b = new SolidBrush(Raised)) g.FillPath(b, p);
        DrawText(g, e.Start.Day.ToString(), FBig, Txt, new RectangleF(dr.X, dr.Y + 1, dr.Width, 24), StringAlignment.Center);
        DrawText(g, e.Start.ToString("MMM", FR).Replace(".", "").ToUpper(), FTag, Muted, new RectangleF(dr.X, dr.Y + 23, dr.Width, 16), StringAlignment.Center);
        bool exam = e.Kind == "exam";
        float tx = r.X + 60;
        if (e.FromMail) {                                       // trouvé dans un mail
            var mr = new RectangleF(tx, r.Y + 11, 22, 16);
            using (var p = Round(mr, 5)) using (var b = new SolidBrush(Color.FromArgb(60, 169, 155, 230))) g.FillPath(b, p);
            DrawText(g, "✉", FTag, Color.FromArgb(200, 190, 245), mr, StringAlignment.Center);
            tx = mr.Right + 6;
        } else {
            using (var b = new SolidBrush(exam ? ExamC : RdvC)) g.FillEllipse(b, tx, r.Y + 16, 6, 6);
            tx += 12;
        }
        int days = (int)(e.Start.Date - DateTime.Today).TotalDays;
        string soon = days <= 0 ? "aujourd'hui" : days == 1 ? "demain" : "dans " + days + " j";
        var ss = g.MeasureString(soon, FSmall);
        DrawText(g, soon, FSmall, Accent2, new RectangleF(r.Right - ss.Width - 10, r.Y + 10, ss.Width + 4, 18));
        DrawText(g, e.Title, FBold, Txt, new RectangleF(tx, r.Y + 9, r.Right - ss.Width - 16 - tx, 20));
        string t = e.AllDay ? "Toute la journée" : e.Start.ToString("HH:mm");
        string src = e.FromMail ? " · mail " + e.Cal : "";
        DrawText(g, t + (e.Loc.Length > 0 ? " · " + e.Loc.Replace("\n", " ") : "") + src, FSmall, Muted, new RectangleF(r.X + 60, r.Y + 31, r.Width - 92, 18));
        // × : retirer de la liste
        var xr = new RectangleF(r.Right - 26, r.Bottom - 25, 20, 20);
        DrawText(g, "×", FBig, Color.FromArgb(120, 150, 150, 160), xr, StringAlignment.Center);
        var ev = e; Hit(Rectangle.Round(xr), () => { Manual.Hide(ev); if (Changed != null) Changed(); });
    }
    public Action Changed;
}

// ---------- Boîtes mail : examens et rendez-vous trouvés dans les mails des 6 derniers mois (analyse locale, 0 token) ----------
class MailMsg { public string Subject = "", Text = "", Source = ""; public DateTime Received; }

static class Mailbox {
    static readonly object L = new object();
    static List<CalEvent> events = new List<CalEvent>();
    static string status = "";
    static bool running;
    static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "mails.json");
    public const string MailColor = "#a99be6";

    public static List<CalEvent> Snapshot() { lock (L) return new List<CalEvent>(events); }
    public static string Status { get { lock (L) return status; } }
    public static bool Running { get { lock (L) return running; } }

    // ---- réglages (mot de passe chiffré avec le compte Windows : DPAPI) ----
    public static string GmailUser { get { return Settings.Str("GmailUser", ""); } }
    public static bool GmailReady { get { return GmailUser.Length > 0 && Settings.Str("GmailPass", "").Length > 0; } }
    public static void SetGmailPass(string p) {
        var enc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(p), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Settings.Set("GmailPass", Convert.ToBase64String(enc));
    }
    static string GmailPass() {
        try { return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(Settings.Str("GmailPass", "")), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
    public static bool OutlookOn { get { return Settings.Bool("OutlookMail", false); } }

    public static void LoadCache() {
        try {
            if (!File.Exists(Cache)) return;
            var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Cache, Encoding.UTF8));
            var ev = new List<CalEvent>();
            foreach (var d in list) ev.Add(new CalEvent {
                Title = Convert.ToString(d["t"]), Loc = Convert.ToString(d["l"]), Cal = Convert.ToString(d["c"]), Color = MailColor, Kind = Convert.ToString(d["k"]),
                AllDay = Convert.ToBoolean(d["a"]), Start = DateTime.Parse(Convert.ToString(d["s"])), End = DateTime.Parse(Convert.ToString(d["e"])), ForList = true, FromMail = true });
            lock (L) { events = ev; if (status.Length == 0) status = "Résultats de la dernière analyse"; }
        } catch { }
    }
    static void SaveCache(List<CalEvent> ev) {
        try {
            var list = new List<Dictionary<string, object>>();
            foreach (var e in ev) list.Add(new Dictionary<string, object> { { "t", e.Title }, { "l", e.Loc }, { "c", e.Cal }, { "k", e.Kind }, { "a", e.AllDay },
                { "s", e.Start.ToString("s") }, { "e", e.End.ToString("s") } });
            Directory.CreateDirectory(Path.GetDirectoryName(Cache));
            File.WriteAllText(Cache, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(list), Encoding.UTF8);
        } catch { }
    }

    public static void Scan(Action done) {
        lock (L) { if (running) return; running = true; status = "Analyse des mails en cours…"; }
        new Thread(() => {
            var found = new List<CalEvent>(); var st = new List<string>(); DateTime since = DateTime.Today.AddMonths(-6);
            if (GmailReady) {
                try { var msgs = ReadGmail(GmailUser, GmailPass(), since); int n = found.Count; foreach (var m in msgs) found.AddRange(Extract(m)); st.Add("Gmail : " + msgs.Count + " mails pertinents, " + (found.Count - n) + " dates"); }
                catch (Exception ex) { st.Add("Gmail : " + ex.Message); Program.Log("Gmail", ex.ToString()); }
            }
            if (OutlookOn) {
                try { var msgs = ReadOutlook(since); int n = found.Count; foreach (var m in msgs) found.AddRange(Extract(m)); st.Add("Outlook : " + msgs.Count + " mails pertinents, " + (found.Count - n) + " dates"); }
                catch (Exception ex) { st.Add("Outlook : " + ex.Message); Program.Log("Outlook", ex.ToString()); }
            }
            // doublons (même mail transféré, rappels…)
            var seen = new HashSet<string>(); var uniq = new List<CalEvent>();
            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var e in found) {
                string k = e.Kind + "|" + e.Start.ToString("yyyyMMdd") + "|" + Calendars.Norm(e.Title).Replace("re: ", "").Replace("tr: ", "");
                if (k.Length > 40) k = k.Substring(0, 40);
                if (seen.Add(k)) uniq.Add(e);
            }
            if (st.Count == 0) st.Add("Aucune boîte mail configurée");
            SaveCache(uniq);
            lock (L) { events = uniq; status = string.Join(" · ", st.ToArray()) + " · " + DateTime.Now.ToString("HH:mm"); running = false; }
            if (done != null) try { done(); } catch { }
        }) { IsBackground = true, Name = "mails" }.Start();
    }

    // ---- mots qui rendent un mail intéressant ----
    static readonly string[] Hints = { "examen", "exam", "partiel", "controle", "evaluation", "epreuve", "soutenance", "convocation", "session",
        "rendez-vous", "rendez vous", "rdv", "consultation", "entretien", "appointment", "rappel" };
    static bool Interesting(string s) { s = Calendars.Norm(s); foreach (var h in Hints) if (s.Contains(h)) return true; return false; }
    static bool Newsletter(string s) { s = Calendars.Norm(s); return s.Contains("unsubscribe") || s.Contains("sabonner") || s.Contains("sinscri") || s.Contains("newsletter"); }

    // ---- Gmail (IMAP + mot de passe d'application) ----
    static List<MailMsg> ReadGmail(string user, string pass, DateTime since) {
        var res = new List<MailMsg>();
        if (pass.Length == 0) throw new Exception("mot de passe d'application manquant");
        using (var im = new Imap("imap.gmail.com")) {
            string st;
            im.Cmd("LOGIN " + Imap.Q(user) + " " + Imap.Q(pass), out st);
            if (!st.StartsWith("OK")) throw new Exception("connexion refusée (vérifie l'adresse et le mot de passe d'application)");
            string all = "INBOX";
            foreach (var line in im.Cmd("LIST \"\" \"*\"", out st)) if (line.Text.Contains("\\All")) { var m = Regex.Match(line.Text, "\"([^\"]+)\"\\s*$"); if (m.Success) all = m.Groups[1].Value; }
            im.Cmd("SELECT " + Imap.Q(all), out st);
            if (!st.StartsWith("OK")) throw new Exception("impossible d'ouvrir la boîte");
            string q = "after:" + since.ToString("yyyy'/'MM'/'dd") + " (examen OR exam OR partiel OR controle OR evaluation OR epreuve OR soutenance OR convocation OR \\\"rendez-vous\\\" OR rdv OR consultation OR entretien OR appointment)";
            var uids = new List<string>();
            foreach (var line in im.Cmd("UID SEARCH X-GM-RAW \"" + q + "\"", out st))
                if (line.Text.StartsWith("* SEARCH")) uids.AddRange(line.Text.Substring(8).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (uids.Count > 300) uids = uids.GetRange(uids.Count - 300, 300);   // les 300 plus récents
            foreach (var uid in uids) {
                foreach (var line in im.Cmd("UID FETCH " + uid + " (BODY.PEEK[]<0.120000>)", out st)) {
                    if (line.Literal == null) continue;
                    var m = Mime.Parse(line.Literal); m.Source = "Gmail";
                    if (m.Received >= since && !Newsletter(m.Text)) res.Add(m);
                }
            }
            im.Cmd("LOGOUT", out st);
        }
        return res;
    }

    // ---- Outlook classique (HES-SO) via COM ----
    static List<MailMsg> ReadOutlook(DateTime since) {
        var res = new List<MailMsg>();
        bool profile = false;
        foreach (var path in new[] { @"Software\Microsoft\Office\16.0\Outlook\Profiles", @"Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles" })
            using (var k = Registry.CurrentUser.OpenSubKey(path)) if (k != null && k.SubKeyCount > 0) profile = true;
        if (!profile) throw new Exception("aucun compte dans Outlook CLASSIQUE (le nouvel Outlook ne compte pas) : ouvre « Outlook (classic) » et ajoute ton compte HES-SO");
        var t = Type.GetTypeFromProgID("Outlook.Application");
        if (t == null) throw new Exception("Outlook classique n'est pas installé");
        bool wasOpen = Process.GetProcessesByName("OUTLOOK").Length > 0;   // si c'est nous qui le lançons, on le refermera
        dynamic app = Activator.CreateInstance(t);
        try { return ReadOutlookItems(app, since, res); }
        finally {
            if (!wasOpen) try { app.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(app); } catch { }
        }
    }
    static List<MailMsg> ReadOutlookItems(dynamic app, DateTime since, List<MailMsg> res) {
        dynamic ns = app.GetNamespace("MAPI");
        foreach (int fid in new[] { 6, 5 }) {   // boîte de réception, éléments envoyés
            dynamic items = ns.GetDefaultFolder(fid).Items;
            items.Sort("[ReceivedTime]", true);
            int n = 0;
            foreach (dynamic it in items) {
                if (++n > 4000) break;
                try {
                    if ((int)it.Class != 43) continue;                 // 43 = mail
                    DateTime rt = it.ReceivedTime; if (rt < since) break;
                    string subj = it.Subject ?? "";
                    string body = it.Body ?? ""; if (body.Length > 20000) body = body.Substring(0, 20000);
                    if (!Interesting(subj) && !Interesting(body.Length > 4000 ? body.Substring(0, 4000) : body)) continue;
                    if (Newsletter(body)) continue;
                    res.Add(new MailMsg { Subject = subj, Text = body, Received = rt, Source = "Outlook" });
                } catch { }
            }
        }
        return res;
    }

    // ---- extraction : type, date, heure, lieu ----
    static readonly string[] Months = { "janvier", "fevrier", "mars", "avril", "mai", "juin", "juillet", "aout", "septembre", "octobre", "novembre", "decembre",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
    static readonly Regex RxText = new Regex(@"\b(\d{1,2})(?:er|st|nd|rd|th)?\s+(" + string.Join("|", Months) + @")\.?(?:\s+(\d{4}))?", RegexOptions.Compiled);
    static readonly Regex RxNum = new Regex(@"\b(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?\b", RegexOptions.Compiled);
    static readonly Regex RxIso = new Regex(@"\b(20\d{2})-(\d{2})-(\d{2})\b", RegexOptions.Compiled);
    static readonly Regex RxTime = new Regex(@"^[^\n]{0,40}?\b([01]?\d|2[0-3])\s*(?:h|:)\s*([0-5]\d)?\b", RegexOptions.Compiled);
    static readonly Regex RxLoc = new Regex(@"(?im)^\s*(?:lieu|salle|adresse|endroit|location|where|o[uù])\s*[:\-–]\s*(.{3,90})$", RegexOptions.Compiled);

    public static List<CalEvent> Extract(MailMsg m) {
        var res = new List<CalEvent>();
        if (Newsletter(m.Text)) return res;                       // pubs et newsletters : ignorées
        string kind = Calendars.Classify(m.Subject);
        if (kind.Length == 0) kind = Calendars.Classify(m.Text.Length > 3000 ? m.Text.Substring(0, 3000) : m.Text);
        if (kind.Length == 0) return res;
        string subj = Regex.Replace(m.Subject, @"^\s*((re|tr|fw|fwd|aw|wg)\s*:\s*)+", "", RegexOptions.IgnoreCase).Trim();
        string norm = Calendars.Norm(subj + "\n" + m.Text);
        // positions des mots-clés : la bonne date est en général proche
        var keys = new List<int>();
        foreach (var h in Hints) { int i = norm.IndexOf(h); while (i >= 0 && keys.Count < 200) { keys.Add(i); i = norm.IndexOf(h, i + 1); } }
        DateTime? best = null; int bestDist = int.MaxValue; bool bestHasTime = false;
        Action<int, int, int, int, int> consider = (pos, len, d, mo, y) => {
            if (mo < 1 || mo > 12 || d < 1 || d > 31) return;
            if (y == 0) { y = m.Received.Year; if (new DateTime(y, mo, Math.Min(d, DateTime.DaysInMonth(y, mo))) < m.Received.Date.AddDays(-1)) y++; }
            if (y < 100) y += 2000;
            if (y < 2000 || y > 2100 || d > DateTime.DaysInMonth(y, mo)) return;
            var date = new DateTime(y, mo, d);
            if (date < m.Received.Date.AddDays(-1) || date > m.Received.Date.AddDays(400)) return;   // date future par rapport au mail
            int dist = pos < subj.Length + 1 ? 0 : int.MaxValue;
            foreach (var k in keys) dist = Math.Min(dist, Math.Abs(k - pos));
            if (dist > 600) return;
            var tm = RxTime.Match(norm.Substring(pos + len, Math.Min(60, norm.Length - pos - len)));
            bool hasTime = tm.Success; DateTime dt = date;
            if (hasTime) dt = date.AddHours(int.Parse(tm.Groups[1].Value)).AddMinutes(tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0);
            if (dist < bestDist) { best = dt; bestDist = dist; bestHasTime = hasTime; }
        };
        foreach (Match x in RxText.Matches(norm)) consider(x.Index, x.Length, int.Parse(x.Groups[1].Value), Array.IndexOf(Months, x.Groups[2].Value) % 12 + 1, x.Groups[3].Success ? int.Parse(x.Groups[3].Value) : 0);
        foreach (Match x in RxNum.Matches(norm)) {
            if (!x.Groups[3].Success && x.Value.Contains(":")) continue;
            consider(x.Index, x.Length, int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value), x.Groups[3].Success ? int.Parse(x.Groups[3].Value) : 0);
        }
        foreach (Match x in RxIso.Matches(norm)) consider(x.Index, x.Length, int.Parse(x.Groups[3].Value), int.Parse(x.Groups[2].Value), int.Parse(x.Groups[1].Value));
        if (!best.HasValue) return res;
        var loc = RxLoc.Match(m.Text);
        res.Add(new CalEvent {
            Title = subj.Length > 0 ? subj : (kind == "exam" ? "Examen" : "Rendez-vous"), Loc = loc.Success ? loc.Groups[1].Value.Trim() : "",
            Cal = m.Source, Color = MailColor, Kind = kind, AllDay = !bestHasTime, Start = best.Value, End = bestHasTime ? best.Value.AddHours(1) : best.Value.AddDays(1),
            ForList = true, FromMail = true });
        return res;
    }
}

// ---- client IMAP minimal (SSL) ----
class ImapLine { public string Text = ""; public byte[] Literal; }
class Imap : IDisposable {
    readonly System.Net.Sockets.TcpClient tcp; readonly System.Net.Security.SslStream ssl; int tag;
    public Imap(string host) {
        tcp = new System.Net.Sockets.TcpClient(host, 993) { ReceiveTimeout = 30000, SendTimeout = 30000 };
        ssl = new System.Net.Security.SslStream(tcp.GetStream());
        ssl.AuthenticateAsClient(host, null, System.Security.Authentication.SslProtocols.Tls12, true);
        ReadLine();
    }
    public static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    string ReadLine() {
        var ms = new MemoryStream(); int b, prev = -1;
        while ((b = ssl.ReadByte()) >= 0) { if (prev == '\r' && b == '\n') { ms.SetLength(ms.Length - 1); break; } ms.WriteByte((byte)b); prev = b; }
        if (b < 0) throw new IOException("connexion fermée par le serveur");
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    byte[] Read(int n) { var buf = new byte[n]; int off = 0; while (off < n) { int r = ssl.Read(buf, off, n - off); if (r <= 0) throw new IOException("connexion fermée"); off += r; } return buf; }
    public List<ImapLine> Cmd(string cmd, out string status) {
        string t = "A" + (++tag);
        var bytes = Encoding.UTF8.GetBytes(t + " " + cmd + "\r\n"); ssl.Write(bytes); ssl.Flush();
        var res = new List<ImapLine>();
        while (true) {
            string line = ReadLine();
            if (line.StartsWith(t + " ")) { status = line.Substring(t.Length + 1); return res; }
            var il = new ImapLine { Text = line };
            var m = Regex.Match(line, @"\{(\d+)\}$");
            if (m.Success) { il.Literal = Read(int.Parse(m.Groups[1].Value)); il.Text += " " + ReadLine(); }
            res.Add(il);
        }
    }
    public void Dispose() { try { ssl.Dispose(); tcp.Close(); } catch { } }
}

// ---- lecture d'un mail brut (MIME) ----
static class Mime {
    static readonly Encoding Latin = Encoding.GetEncoding(28591);
    public static MailMsg Parse(byte[] raw) {
        var m = new MailMsg(); string s = Latin.GetString(raw);
        Dictionary<string, string> h; string body; Split(s, out h, out body);
        m.Subject = h.ContainsKey("subject") ? Words(h["subject"]) : "";
        DateTimeOffset dto; m.Received = DateTime.Today;
        if (h.ContainsKey("date") && DateTimeOffset.TryParse(Regex.Replace(h["date"], @"\s*\(.*\)\s*$", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dto)) m.Received = dto.LocalDateTime;
        bool html; m.Text = Part(h, body, out html);
        if (html) m.Text = Strip(m.Text);
        if (m.Text.Length > 30000) m.Text = m.Text.Substring(0, 30000);
        return m;
    }
    static void Split(string s, out Dictionary<string, string> h, out string body) {
        int i = s.IndexOf("\r\n\r\n"); int sep = 4; if (i < 0) { i = s.IndexOf("\n\n"); sep = 2; }
        string head = i < 0 ? s : s.Substring(0, i); body = i < 0 ? "" : s.Substring(i + sep);
        h = new Dictionary<string, string>();
        foreach (var line in Regex.Replace(head, @"\r?\n[ \t]+", " ").Split('\n')) {
            int c = line.IndexOf(':'); if (c <= 0) continue;
            string k = line.Substring(0, c).Trim().ToLowerInvariant(); if (!h.ContainsKey(k)) h[k] = line.Substring(c + 1).Trim();
        }
    }
    static string Part(Dictionary<string, string> h, string body, out bool html) {
        string ct = h.ContainsKey("content-type") ? h["content-type"] : "text/plain"; html = ct.ToLowerInvariant().Contains("text/html");
        if (ct.ToLowerInvariant().StartsWith("multipart/")) {
            var b = Regex.Match(ct, "boundary=\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
            if (!b.Success) return "";
            string plain = null, htm = null;
            foreach (var p in body.Split(new[] { "--" + b.Groups[1].Value }, StringSplitOptions.None)) {
                if (p.StartsWith("--") || p.Trim().Length == 0) continue;
                Dictionary<string, string> ph; string pb; Split(p.TrimStart('\r', '\n'), out ph, out pb);
                if (ph.Count == 0) continue;
                bool ish; string txt = Part(ph, pb, out ish);
                if (ish) { if (htm == null) htm = txt; } else if (plain == null && txt.Length > 0) plain = txt;
            }
            if (plain != null) { html = false; return plain; }
            html = htm != null; return htm ?? "";
        }
        if (!ct.ToLowerInvariant().StartsWith("text/")) return "";
        string cte = h.ContainsKey("content-transfer-encoding") ? h["content-transfer-encoding"].ToLowerInvariant() : "";
        var cs = Regex.Match(ct, "charset=\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
        Encoding enc; try { enc = cs.Success ? Encoding.GetEncoding(cs.Groups[1].Value.Trim()) : Encoding.UTF8; } catch { enc = Encoding.UTF8; }
        byte[] data;
        try {
            if (cte.Contains("base64")) data = Convert.FromBase64String(Regex.Replace(body, @"[^A-Za-z0-9+/=]", ""));
            else if (cte.Contains("quoted-printable")) data = QP(body);
            else data = Latin.GetBytes(body);
        } catch { data = Latin.GetBytes(body); }
        return enc.GetString(data);
    }
    static byte[] QP(string s) {
        var ms = new MemoryStream(); s = Regex.Replace(s, @"=\r?\n", "");
        for (int i = 0; i < s.Length; i++) {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else ms.WriteByte((byte)s[i]);
        }
        return ms.ToArray();
    }
    static string Words(string s) {  // =?utf-8?B?...?= / =?iso-8859-1?Q?...?=
        s = Regex.Replace(s, @"\?=\s+=\?", "?==?");
        string r = Regex.Replace(s, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", mm => {
            Encoding enc; try { enc = Encoding.GetEncoding(mm.Groups[1].Value); } catch { enc = Encoding.UTF8; }
            try {
                byte[] d = mm.Groups[2].Value.ToUpper() == "B" ? Convert.FromBase64String(mm.Groups[3].Value) : QP(mm.Groups[3].Value.Replace('_', ' '));
                return enc.GetString(d);
            } catch { return mm.Value; }
        });
        if (r == s) try { r = Encoding.UTF8.GetString(Latin.GetBytes(s)); } catch { }   // en-tête brut en UTF-8
        return r;
    }
    static string Strip(string html) {
        string t = Regex.Replace(html, @"(?is)<(style|script|head)[^>]*>.*?</\1>", " ");
        t = Regex.Replace(t, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>|</h\d>", "\n");
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"[ \t ]+", " ");
        return Regex.Replace(t, @"\n\s*\n+", "\n");
    }
}

// ---------- Rappels d'examens et de rendez-vous (carte au-dessus du chat) ----------
static class Reminders {
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "rappels.txt");
    static HashSet<string> done;
    static HashSet<string> Done() {
        if (done != null) return done;
        done = new HashSet<string>();
        try { if (File.Exists(File_)) foreach (var l in File.ReadAllLines(File_, Encoding.UTF8)) if (l.Length > 0) done.Add(l); } catch { }
        return done;
    }
    static void Mark(string k) {
        if (!Done().Add(k)) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(File_)); File.AppendAllText(File_, k + "\r\n", Encoding.UTF8); } catch { }
    }
    public static string Key(CalEvent e) { return e.Kind + "|" + e.Start.ToString("yyyyMMddHHmm") + "|" + Calendars.Norm(e.Title); }
    public static void Never(CalEvent e) { Mark(Key(e) + "|jamais"); }

    // quand prévenir : la veille à 18 h, et 1 h avant (le matin à 7 h 30 pour un événement d'une journée)
    public static CalEvent Due(out string when) {
        when = null;
        string mode = Settings.Str("RemindWhen", "both");
        bool exam = Settings.Bool("RemindExam", true), rdv = Settings.Bool("RemindRdv", true);
        if (!exam && !rdv) return null;
        var now = DateTime.Now; var d = Done();
        foreach (var e in Calendars.Snapshot()) {
            if (!e.ForList || e.Start <= now || e.Start > now.AddDays(2)) continue;
            if ((e.Kind == "exam" && !exam) || (e.Kind == "rdv" && !rdv) || e.Kind.Length == 0) continue;
            string k = Key(e);
            if (d.Contains(k + "|jamais")) continue;
            if (mode != "heure") {
                var at = e.Start.Date.AddDays(-1).AddHours(18);
                if (now >= at && now < e.Start.Date && !d.Contains(k + "|veille")) { Mark(k + "|veille"); when = "demain"; return e; }
            }
            if (mode != "veille") {
                var at = e.AllDay ? e.Start.Date.AddHours(7.5) : e.Start.AddHours(-1);
                if (now >= at && !d.Contains(k + "|heure")) { Mark(k + "|heure"); Mark(k + "|veille"); when = e.AllDay ? "aujourd'hui" : "dans " + Math.Max(1, (int)Math.Round((e.Start - now).TotalMinutes)) + " min"; return e; }
            }
        }
        return null;
    }
}

class ReminderCard : Layered {
    public CalEvent Ev; public string When = ""; public Action OnDone;
    const int W = 320, H = 116;
    protected override Size Measure() { return new Size(W, H); }
    protected override void Render(Graphics g) {
        var r = new RectangleF(4, 2, W - 8, H - 10);
        Card(g, r, 16);
        bool exam = Ev.Kind == "exam";
        using (var p = Round(new RectangleF(r.X + 1, r.Y + 1, 4, r.Height - 2), 2)) using (var b = new SolidBrush(exam ? ExamC : RdvC)) g.FillPath(b, p);
        string tag = exam ? "EXAMEN" : "RENDEZ-VOUS";
        var ts = g.MeasureString(tag, FTag); var tr = new RectangleF(r.X + 16, r.Y + 12, ts.Width + 8, 17);
        using (var p = Round(tr, 5)) using (var b = new SolidBrush(Color.FromArgb(55, exam ? ExamC : RdvC))) g.FillPath(b, p);
        DrawText(g, tag, FTag, exam ? Color.FromArgb(240, 140, 135) : Color.FromArgb(140, 196, 230), tr, StringAlignment.Center);
        DrawText(g, Cap(When), FBold, Accent2, new RectangleF(tr.Right + 8, r.Y + 11, r.Right - tr.Right - 20, 19));
        DrawText(g, Ev.Title, FTitle, Txt, new RectangleF(r.X + 16, r.Y + 33, r.Width - 28, 22));
        string t = (Ev.AllDay ? "Toute la journée" : Ev.Start.ToString("dddd d MMMM 'à' HH:mm", FR)) + (Ev.Loc.Length > 0 ? " · " + Ev.Loc.Replace("\n", " ") : "");
        DrawText(g, t, FSmall, Muted, new RectangleF(r.X + 16, r.Y + 55, r.Width - 28, 17));
        var ok = new RectangleF(r.Right - 66, r.Bottom - 34, 54, 24);
        var never = new RectangleF(r.X + 12, r.Bottom - 34, 140, 24);
        using (var p = Round(ok, 12)) using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
        DrawText(g, "OK", FBold, Color.White, ok, StringAlignment.Center);
        DrawText(g, "Ne plus me rappeler", FSmall, Muted, never);
        Hit(Rectangle.Round(ok), () => { Hide(); if (OnDone != null) OnDone(); });
        Hit(Rectangle.Round(never), () => { Reminders.Never(Ev); Hide(); if (OnDone != null) OnDone(); });
    }
}

// ---------- Examens et rendez-vous ajoutés ou retirés à la main ----------
static class Manual {
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static List<Dictionary<string, object>> Raw() {
        try { return new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Settings.Str("ManualEvents", "[]")) ?? new List<Dictionary<string, object>>(); }
        catch { return new List<Dictionary<string, object>>(); }
    }
    static void Save(List<Dictionary<string, object>> l) { Settings.Set("ManualEvents", new JavaScriptSerializer().Serialize(l)); }
    static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, Inv) : ""; }

    public static List<CalEvent> Events() {
        var r = new List<CalEvent>();
        foreach (var d in Raw()) {
            DateTime s; if (!DateTime.TryParse(S(d, "s"), Inv, System.Globalization.DateTimeStyles.None, out s)) continue;
            bool a = S(d, "a") == "True";
            r.Add(new CalEvent { Title = S(d, "t"), Kind = S(d, "k") == "rdv" ? "rdv" : "exam", Loc = S(d, "l"), Start = s, End = a ? s.AddDays(1) : s.AddHours(1),
                AllDay = a, ForList = true, Manual = true, Uid = S(d, "id"), Cal = "ajouté à la main", Color = "#e0b84f" });
        }
        return r;
    }
    // d : { k: exam|rdv, t: titre, d: AAAA-MM-JJ, h: HH:mm (facultatif), l: lieu }
    public static void Add(Dictionary<string, object> d) {
        if (d == null) return;
        string t = S(d, "t").Trim(); if (t.Length == 0) return; if (t.Length > 120) t = t.Substring(0, 120);
        DateTime day; if (!DateTime.TryParseExact(S(d, "d"), "yyyy-MM-dd", Inv, System.Globalization.DateTimeStyles.None, out day)) return;
        TimeSpan tm; bool hasTime = TimeSpan.TryParseExact(S(d, "h"), "hh\\:mm", Inv, out tm);
        string l = S(d, "l").Trim(); if (l.Length > 120) l = l.Substring(0, 120);
        var list = Raw();
        list.Add(new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N").Substring(0, 10) }, { "k", S(d, "k") == "rdv" ? "rdv" : "exam" }, { "t", t },
            { "s", (hasTime ? day + tm : day).ToString("s") }, { "a", !hasTime }, { "l", l } });
        list.Sort((x, y) => string.CompareOrdinal(S(x, "s"), S(y, "s")));
        Save(list);
    }
    public static void Remove(string id) { var l = Raw(); l.RemoveAll(x => S(x, "id") == id); Save(l); }

    // retirés de la liste (venant d'un calendrier ou d'un mail) : on garde leur « clé »
    public static HashSet<string> Hidden() { return new HashSet<string>(Settings.Str("HiddenEvents", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)); }
    public static void Hide(CalEvent e) {
        if (e.Manual) { Remove(e.Uid); return; }
        var h = Hidden(); h.Add(Reminders.Key(e)); Settings.Set("HiddenEvents", string.Join("\n", new List<string>(h).ToArray()));
    }
    public static void Unhide(string key) { var h = Hidden(); h.Remove(key); Settings.Set("HiddenEvents", string.Join("\n", new List<string>(h).ToArray())); }
}

// ---------- Assistant d'installation : vérifie chaque étape, la fait à ta place, et prépare le paquet pour un autre PC ----------
static class Setup {
    static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
    public static string InstallDir { get { return Path.Combine(Home, "ClaudeCompanion"); } }
    public static string AppDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
    static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Bébène.lnk"); } }
    static string OneDrive { get { var od = Environment.GetEnvironmentVariable("OneDriveConsumer"); return !string.IsNullOrEmpty(od) && Directory.Exists(od) ? od : null; } }
    // ClaudeHub : sur le OneDrive personnel si possible (partagé entre PC), sinon dans le dossier utilisateur
    public static string Hub {
        get {
            if (OneDrive != null) { var h = Path.Combine(OneDrive, "ClaudeHub"); if (Directory.Exists(h)) return h; }
            var local = Path.Combine(Home, "ClaudeHub"); if (Directory.Exists(local)) return local;
            return OneDrive != null ? Path.Combine(OneDrive, "ClaudeHub") : local;
        }
    }
    public static bool InstalledHere { get { return string.Equals(Path.GetFullPath(AppDir).TrimEnd('\\'), Path.GetFullPath(InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } }
    static string Run(string file, string args, int ms) {
        Util.FreshPath();
        try {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi)) {
                var o = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (o) o.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return null; }
                p.WaitForExit();
                lock (o) return p.ExitCode == 0 ? o.ToString().Trim() : null;
            }
        } catch { return null; }
    }
    static bool OnPath(string exe) { return Run("where.exe", exe, 8000) != null; }

    static Dictionary<string, object> S(string id, string state, string detail) { return new Dictionary<string, object> { { "id", id }, { "state", state }, { "detail", detail } }; }

    // state : ok | no (à faire) | warn (facultatif mais conseillé) | opt (option)
    public static List<Dictionary<string, object>> Check() {
        var r = new List<Dictionary<string, object>>();
        string build = Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0"));   // vrai numéro (OSVersion ment aux anciens programmes)
        int bn; int.TryParse(build, out bn);
        r.Add(S("windows", "ok", "Windows " + (bn >= 22000 ? "11" : "10") + " (build " + build + ") · .NET Framework " + Environment.Version));
        string wv = null; try { wv = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
        r.Add(S("webview", wv != null ? "ok" : "no", wv != null ? "Version " + wv : "Composant d'affichage manquant"));
        string cv = Run("cmd.exe", "/c claude --version", 25000);
        r.Add(S("claude", cv != null ? "ok" : "no", cv != null ? cv.Split('\n')[0] : "Claude Code n'est pas installé"));
        bool login = File.Exists(Path.Combine(Home, @".claude\.credentials.json")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        r.Add(S("login", login ? "ok" : "no", login ? "Compte connecté" : "Pas encore connecté à ton compte Claude"));
        r.Add(S("onedrive", OneDrive != null ? "ok" : "warn", OneDrive != null ? OneDrive : "Pas de OneDrive personnel : la mémoire restera sur ce PC uniquement"));
        bool hub = File.Exists(Path.Combine(Hub, @"memoire\00-noyau.md"));
        r.Add(S("hub", hub ? "ok" : "no", hub ? Hub : "Mémoire ClaudeHub introuvable"));
        string md = Path.Combine(Home, @".claude\CLAUDE.md"), st = Path.Combine(Home, @".claude\settings.json");
        bool wired = File.Exists(md) && File.ReadAllText(md).Contains("memoire/00-noyau.md") && File.Exists(st) && File.ReadAllText(st).Contains("archiver-session.ps1");
        r.Add(S("wire", wired ? "ok" : "no", wired ? "Mémoire, archivage et skills branchés" : "Claude Code ne connaît pas encore ClaudeHub"));
        bool rtk = OnPath("rtk"), node = OnPath("node"), cave = Directory.Exists(Path.Combine(Home, @".claude\plugins\cache\caveman"));
        r.Add(S("tools", rtk && node && cave ? "ok" : "warn", (rtk ? "RTK ✓" : "RTK ✗") + " · " + (node ? "Node ✓" : "Node ✗") + " · " + (cave ? "Caveman ✓" : "Caveman ✗")));
        bool lnk = File.Exists(StartMenuLnk), run; using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run = k != null && k.GetValue("ClaudeCompanion") != null;
        r.Add(S("install", InstalledHere && lnk ? "ok" : "no", InstalledHere ? (lnk ? "Installé dans " + InstallDir + (run ? " · démarre avec Windows" : "") : "Raccourci du menu Démarrer manquant") : "Lancé depuis " + AppDir + " (pas encore installé)"));
        r.Add(S("calendars", Calendars.Config().Count > 0 ? "ok" : "opt", Calendars.Config().Count + " calendrier(s)"));
        r.Add(S("mail", Mailbox.GmailReady || Mailbox.OutlookOn ? "ok" : "opt", (Mailbox.GmailReady ? "Gmail ✓ " : "") + (Mailbox.OutlookOn ? "Outlook ✓" : "") + (!Mailbox.GmailReady && !Mailbox.OutlookOn ? "Aucune boîte mail" : "")));
        return r;
    }

    // Lance une action dans une fenêtre PowerShell visible (on voit ce qui se passe) ; done() quand elle se ferme
    static void Shell(string title, string script, Action done) {
        string full = "$Host.UI.RawUI.WindowTitle = '" + title.Replace("'", "''") + "'; " + script
            + "; Write-Host ''; Write-Host 'Terminé. Tu peux fermer cette fenêtre (Bébène vérifie automatiquement).' -ForegroundColor Green; Read-Host 'Appuie sur Entrée pour fermer'";
        var p = new Process { StartInfo = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + full.Replace("\"", "\\\"") + "\"") { UseShellExecute = true }, EnableRaisingEvents = true };
        p.Exited += (s, e) => { if (done != null) done(); };
        p.Start();
    }

    public static string Do(string id, Action recheck) {
        switch (id) {
            case "webview": Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); return "Téléchargement de WebView2 ouvert dans le navigateur : installe-le puis reviens.";
            case "claude":
                Shell("Installation de Claude Code", "winget install --id Anthropic.ClaudeCode -e --accept-source-agreements --accept-package-agreements", recheck);
                return "Installation de Claude Code en cours dans la fenêtre bleue…";
            case "login":
                Shell("Connexion à Claude", "Write-Host 'Connecte-toi à ton compte Claude (Pro ou Max) dans la page qui va s ouvrir, puis tape /exit.' -ForegroundColor Cyan; claude", recheck);
                return "Une fenêtre s'est ouverte : suis la connexion à ton compte Claude, puis tape /exit.";
            case "onedrive": {
                string od = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\OneDrive\OneDrive.exe");
                if (File.Exists(od)) Process.Start(od); else Process.Start("https://www.microsoft.com/microsoft-365/onedrive/download");
                return "Connecte ton compte Microsoft PERSONNEL dans OneDrive, puis attends que le dossier OneDrive apparaisse.";
            }
            case "hub": return CreateHub();
            case "wire": case "tools": {
                string inst = Path.Combine(Hub, @"outils\installer.ps1");
                if (!File.Exists(inst)) return "Installe d'abord la mémoire ClaudeHub (étape précédente).";
                Shell("Configuration de Claude Code", "& '" + inst.Replace("'", "''") + "'", recheck);
                return "Configuration en cours dans la fenêtre bleue (mémoire, archivage, skills, outils d'économie de tokens)…";
            }
            case "install": return InstallHere();
        }
        return "";
    }

    // Mémoire ClaudeHub vierge, à partir du modèle livré dans le paquet (hub-modele)
    static string CreateHub() {
        string model = Path.Combine(AppDir, "hub-modele");
        if (File.Exists(Path.Combine(Hub, @"memoire\00-noyau.md"))) return "ClaudeHub est déjà là.";
        if (!Directory.Exists(model)) return "Si tu utilises le même OneDrive que ton autre PC, attends simplement la synchronisation du dossier ClaudeHub.";
        string dest = OneDrive != null ? Path.Combine(OneDrive, "ClaudeHub") : Path.Combine(Home, "ClaudeHub");
        CopyDir(model, dest);
        return "Mémoire ClaudeHub créée dans " + dest + ".";
    }

    static void CopyDir(string from, string to) {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) { var d = Path.Combine(to, Path.GetFileName(f)); if (!File.Exists(d)) File.Copy(f, d); }
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // Copie Bébène dans %USERPROFILE%\ClaudeCompanion, raccourci du menu Démarrer, démarrage avec Windows, puis relance depuis là
    static string InstallHere() {
        try {
            if (!InstalledHere) {
                Directory.CreateDirectory(InstallDir);
                foreach (var f in Directory.GetFiles(AppDir)) File.Copy(f, Path.Combine(InstallDir, Path.GetFileName(f)), true);
                if (Directory.Exists(Path.Combine(AppDir, "hub-modele"))) CopyDir(Path.Combine(AppDir, "hub-modele"), Path.Combine(InstallDir, "hub-modele"));
            }
            string exe = Path.Combine(InstallDir, "ClaudeCompanion.exe");
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = sh.CreateShortcut(StartMenuLnk);
            l.TargetPath = exe; l.WorkingDirectory = InstallDir; l.IconLocation = Path.Combine(InstallDir, "bebene.ico") + ",0"; l.Description = "Ton compagnon Claude"; l.Save();
            Settings.AutoStart = true;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) k.SetValue("ClaudeCompanion", "\"" + exe + "\" --background");
            if (!InstalledHere) {   // relancer la copie installée, puis quitter celle-ci
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe + "\" --setup") { CreateNoWindow = true, UseShellExecute = false });
                Application.Exit();
                return "Installé ! Bébène redémarre depuis son dossier d'installation…";
            }
            return "Raccourci du menu Démarrer et démarrage avec Windows en place.";
        } catch (Exception ex) { return "Impossible d'installer : " + ex.Message; }
    }

}

// ---------- La fenêtre de discussion ----------
class ChatPanel : Form {
    public static bool NoActivate;   // tests : afficher sans prendre le clavier
    protected override bool ShowWithoutActivation { get { return NoActivate; } }
    readonly Companion owner;
    readonly WebView2 web;
    Process proc;
    public string WorkDir;

    public ChatPanel(Companion c) {
        owner = c;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(ChatW, 660);
        calTimer.Tick += (s, e) => RefreshCalendars();                 // rafraîchit les calendriers toutes les 30 min
        calTimer.Start();
        InitSide();
        VisibleChanged += (s, e) => { if (Visible) { ProbeQuota(false); if (owner.State == "ready") owner.SetState("happy"); } };    // quota à jour à chaque ouverture
        quotaTimer.Tick += (s, e) => { if (Visible) ProbeQuota(true); }; quotaTimer.Start();
        Mailbox.LoadCache();                                         // derniers résultats tout de suite, nouvelle analyse en fond
        mailTimer.Tick += (s, e) => ScanMail(); mailTimer.Start();
        var first = new System.Windows.Forms.Timer { Interval = 20000 };
        first.Tick += (s, e) => { first.Stop(); first.Dispose(); if (Mailbox.GmailReady || Mailbox.OutlookOn) ScanMail(); };
        first.Start();
        RefreshCalendars();
        BackColor = Color.Black;                                     // noir = zone où Windows dessine le verre dépoli
        WorkDir = Settings.Str("WorkDir", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Directory.Exists(WorkDir)) WorkDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(web);
        Util.InitWeb(web, "ui.html", OnMessage);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Util.DarkWindow(Handle, true); Util.Glass(Handle); }

    // ---- clic ailleurs = la discussion se ferme (sauf si elle est épinglée, ou si on clique sur Bébène / ses fenêtres) ----
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public bool Pinned = Settings.Bool("Pinned", false);
    protected override void OnDeactivate(EventArgs e) {
        base.OnDeactivate(e);
        if (Pinned || NoActivate) return;
        var t = new System.Windows.Forms.Timer { Interval = 120 };          // laisse Windows activer la nouvelle fenêtre
        t.Tick += (s, e2) => {
            t.Stop(); t.Dispose();
            if (!Visible || Pinned || ContainsFocus) return;
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == Program.Pid) return;      // le chat, les réglages, un dialogue de Bébène…
            Hide();
        };
        t.Start();
    }

    public void FocusInput() { web.Focus(); Post("focus"); }

    public void Post(string s) {
        // la fenêtre peut ne jamais avoir été affichée : on passe par le contrôle WebView, qui a toujours son handle
        Control ui = IsHandleCreated ? (Control)this : (web != null && web.IsHandleCreated ? web : null);
        if (ui == null) return;                                           // page pas prête : elle redemandera tout via "ready"
        if (ui.InvokeRequired) { try { ui.BeginInvoke((Action)(() => Post(s))); } catch { } return; }
        if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(s);  // la page n est pas encore prête : ignoré (elle redemande tout via "ready")
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
    string CurrentDir() {
        if (!Flash && Project.Length > 0) { string d = Path.Combine(ProjectsDir, Project); if (Directory.Exists(d)) return d; }
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
    DateTime lastProbe = DateTime.MinValue; volatile bool probing;
    readonly System.Windows.Forms.Timer quotaTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
    public void ProbeQuota(bool force) {
        if (probing || proc != null) return;                                   // une conversation en cours renvoie déjà ces infos
        if (!force && (DateTime.Now - lastProbe).TotalMinutes < 5) return;
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
                        if (line.Contains("\"rate_limit_event\"") && m.Success) Post("quota\n" + m.Groups[1].Value);
                    }
                    p.WaitForExit(30000);
                }
            } catch (Exception ex) { Program.Log("Quota", ex.Message); }
            probing = false;
        }) { IsBackground = true, Name = "quota" }.Start();
    }

    // ---- titre de conversation résumé par Claude (Haiku, quelques tokens, en arrière-plan) ----
    void MakeTitle(string id, string excerpt) {
        new Thread(() => {
            try {
                var psi = new ProcessStartInfo("claude", "-p --model haiku --setting-sources \"\" --strict-mcp-config --no-session-persistence --tools \"\" "
                    + "--system-prompt \"Tu donnes un titre a une conversation entre un utilisateur et son assistant. Reponds UNIQUEMENT par le titre : 3 a 7 mots en francais, "
                    + "precis, qui resume le sujet principal de toute la conversation. Pas de guillemets, pas de point final, pas d emoji.\" --output-format text") {
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
                    if (o.Length > 1 && o.Length <= 90 && p.ExitCode == 0) Post("title\n" + id + "\n" + o);
                }
            } catch (Exception ex) { Program.Log("Titre", ex.Message); }
        }) { IsBackground = true, Name = "titre" }.Start();
    }

    static void KillAfter(Process p, int ms) {
        new Thread(() => { try { if (!p.WaitForExit(ms)) p.Kill(); } catch { } }) { IsBackground = true, Name = "délai" }.Start();
    }

    public const int ChatW = 460;
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
    readonly System.Windows.Forms.Timer mailTimer = new System.Windows.Forms.Timer { Interval = 3 * 3600 * 1000 };

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
                Post("pinned\n" + (Pinned ? 1 : 0));
                Post("dir\n" + WorkDir); Post("name\n" + Settings.Name);
                PostCalendars(); ProbeQuota(true); break;               // la page est prête : on lit le quota tout de suite
            case "calrefresh": RefreshCalendars(); break;
            case "pin": Pinned = parts.Length > 1 && parts[1] == "1"; Settings.Set("Pinned", Pinned); break;
            case "sid": owner.SessionId = parts.Length > 1 ? parts[1] : ""; break;
            case "terminal": owner.OpenTerminal(true); break;
            case "settings": owner.ShowSettings(); break;
            case "hide": Hide(); break;
            case "title": {                                                    // title\n<id conversation>\n<extrait>
                int i1 = m.IndexOf('\n'), i2 = i1 < 0 ? -1 : m.IndexOf('\n', i1 + 1);
                if (i2 > 0) MakeTitle(m.Substring(i1 + 1, i2 - i1 - 1), m.Substring(i2 + 1));
                break; }
            case "copy": { string txt = m.Length > 5 ? m.Substring(5) : ""; if (txt.Length > 0) try { Clipboard.SetText(txt); } catch (Exception ex) { Program.Log("Copie", ex.Message); } break; }
            case "drag": DragWindow(); break;
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
            case "forget": Forget(parts.Length > 1 ? parts[1] : ""); break;
            case "stop": KillTree(); break;
            case "send": if (parts.Length == 5) Send(parts[4], parts[1] == "1", parts[2], parts[3]); break;
        }
    }

    // « Arrêter » : tue Claude ET les commandes qu'il a lancées (sinon elles continuent en fond)
    void KillTree() {
        if (proc == null) return;
        try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + proc.Id) { CreateNoWindow = true, UseShellExecute = false }); }
        catch { try { proc.Kill(); } catch { } }
    }

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

    void Send(string text, bool autonomous, string sessionId, string allowed) {
        if (proc != null) { Post("done\n-1\nClaude est encore en train de répondre : attends la fin ou clique sur Arrêter."); return; }
        // Le contexte qui change (fenêtre active) va dans le MESSAGE, pas dans le prompt système :
        // un prompt système stable garde l'historique en cache → beaucoup moins de tokens à chaque échange.
        if (Companion.LastWindow.Length > 0) text = "[Fenêtre active : " + Companion.LastWindow + "]\n" + text;
        string args = "-p --output-format stream-json --verbose --permission-mode " + (autonomous ? "bypassPermissions" : "acceptEdits");
        // actions autorisées : celles accordées dans la conversation + celles pour lesquelles l utilisateur ne veut plus d alerte (réglages)
        var allow = new List<string>();
        foreach (var a in (allowed + "," + Settings.Str("AutoAllow", "")).Split(',')) if (Regex.IsMatch(a, "^[A-Za-z]+$") && !allow.Contains(a)) allow.Add(a);
        if (allow.Count > 0) args += " --allowedTools " + string.Join(",", allow.ToArray());
        string model = Settings.Str("Model", "");
        if (Regex.IsMatch(model, "^[a-z0-9.-]+$")) args += " --model " + model;
        if (Regex.IsMatch(sessionId, "^[0-9a-fA-F-]{36}$")) args += " --resume " + sessionId;
        string persona = "Tu es " + Settings.Name + ", un petit chat noir qui vit sur le bureau Windows de l'utilisateur et l'aide au quotidien "
            + "(questions, petites modifications de fichiers, recommandations). Réponds en français, de façon claire et concise. "
            + "Tu partages la mémoire centrale ClaudeHub (~/OneDrive/ClaudeHub) avec Claude Code : tu peux y chercher (skill memoire), "
            + "retenir des choses, créer et gérer les projets (projets/<nom>/NOTES.md) et retrouver les conversations archivées, en suivant les règles du noyau de mémoire. "
            + "Quand l'utilisateur dit travailler sur un projet, écris la ligne « 📁 Projet : <nom> » pour que la conversation y soit sauvegardée. "
            + (Flash ? "CONVERSATION FLASH : l'utilisateur ne veut rien enregistrer. N'écris rien dans la mémoire ClaudeHub, ne crée pas de projet et n'écris jamais la ligne « 📁 Projet ». "
                     : Project.Length > 0 ? "Cette conversation fait partie du projet « " + Project + " » (dossier de travail actuel ; elle est archivée automatiquement dans le projet). Tiens NOTES.md à jour quand ça avance. " : "")
            + "Le message peut commencer par [Fenêtre active : …] : c'est la fenêtre que l'utilisateur avait devant lui (souvent « le fichier ouvert devant moi »). "
            + "Pour agir dans un programme (Excel, Word, PowerPoint, Outlook, Inventor ou toute autre appli, fichier ouvert compris), utilise la skill piloter-applis. "
            + "Tu ne peux PAS modifier ton propre programme (code, installation, réglages, données de Bébène) : c'est verrouillé. Si on te le demande, explique que l'évolution de Bébène se fait avec Claude Code dans le terminal. "
            + Settings.Str("Prompt", "");
        args += " --append-system-prompt " + Util.Quote(persona.Replace("\r", " ").Replace("\n", " "));
        args += " --settings " + Util.Quote(GuardSettings());                // gardien : pas de modification de Bébène par lui-même

        var psi = new ProcessStartInfo("claude", args) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = CurrentDir(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Caveman (réponses concises) : en mode non interactif, il faut l'activer explicitement
        psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = Settings.Bool("Caveman", true) ? "caveman" : "off";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var err = new StringBuilder();
        p.OutputDataReceived += (s, e) => { if (e.Data != null) Post("line\n" + e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.Exited += (s, e) => {
            p.WaitForExit();
            int code = p.ExitCode;
            string er; lock (err) er = err.ToString();
            try {
                BeginInvoke((Action)(() => {
                    Post("done\n" + code + "\n" + er);
                    p.Dispose(); proc = null;
                    owner.SetState(!Visible ? "ready" : code == 0 ? "happy" : "idle");   // fermée : il attend content qu on vienne lire
                }));
            } catch { } // l'app se ferme pendant que Claude répond
        };
        try { p.Start(); }
        catch (Exception ex) { Post("done\n-1\nImpossible de lancer Claude Code : " + ex.Message); return; }
        proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try {
            var bytes = Encoding.UTF8.GetBytes(text);
            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            p.StandardInput.Close();
        } catch (IOException) { }   // Claude s'est arrêté tout de suite : son message d'erreur arrivera par « done »
        owner.SetState("thinking");
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else KillTree();
    }
}

// ---------- Le centre de contrôle ----------
class SettingsWindow : Form {
    readonly Companion owner;
    readonly WebView2 web;

    public SettingsWindow(Companion c) {
        owner = c;
        Text = "Bébène";
        Icon = Companion.AppIcon(new Size(32, 32));
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(620, 760);
        MinimumSize = new Size(480, 520);
        BackColor = Color.FromArgb(24, 24, 27);
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
        Util.InitWeb(web, "settings.html", OnMessage);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Util.DarkWindow(Handle, false); }

    public void Refresh2() { Post("settings\n" + owner.SettingsJson()); }
    public void Goto(string section) { pendingGoto = section; Post("goto\n" + section); }   // la page peut ne pas être prête : redemandé à "get"
    string pendingGoto;

    void Post(string s) {
        if (InvokeRequired) { try { BeginInvoke((Action)(() => Post(s))); } catch { } return; }   // appels depuis les vérifications en arrière-plan
        if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(s);
    }
    // vérifications de l'assistant : en arrière-plan (certaines prennent quelques secondes)
    volatile bool checking;
    void SetupCheck() {
        if (checking) return; checking = true;
        Post("setupbusy");
        new Thread(() => {
            try { Post("setup\n" + new JavaScriptSerializer().Serialize(Setup.Check())); }
            catch (Exception ex) { Program.Log("Setup", ex.ToString()); }
            checking = false;
        }) { IsBackground = true, Name = "setup" }.Start();
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
        string m;
        try { m = e.TryGetWebMessageAsString(); } catch { return; }
        int i = m.IndexOf('\n');
        string k = i < 0 ? m : m.Substring(0, i), v = i < 0 ? "" : m.Substring(i + 1);
        switch (k) {
            case "get": Refresh2(); if (pendingGoto != null) Post("goto\n" + pendingGoto); pendingGoto = null; break;
            case "setupcheck": SetupCheck(); break;
            case "setupdo": {
                string msg = Setup.Do(v.Trim(), () => { try { BeginInvoke((Action)SetupCheck); } catch { } });
                Post("setupmsg\n" + msg);
                if (v.Trim() == "hub") SetupCheck();
                break;
            }
            case "set": owner.ApplySettings(v); Refresh2(); break;
            case "folder": if (owner.Panel.PickFolder(this)) Refresh2(); break;
            case "chat": owner.ShowPanel(); break;
            case "terminal": owner.OpenTerminal(false); break;
            case "resume": owner.OpenTerminal(true); break;
            case "quit": owner.Quit(); break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    }
}
