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
    public static string Run(string file, string args, int ms) {
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

// ---------- Mises à jour : compare avec la dernière version publiée sur GitHub (Releases) ----------
// Dépôt public : API GitHub sans compte. Dépôt privé : passe par GitHub CLI (gh) connecté sur ce PC.
// L'installation remplace seulement le programme (exe, dll, pages) : réglages (registre), conversations
// (stockage WebView2), calendriers, mails et journal sont dans d'autres dossiers et ne sont pas touchés.
static class Updater {
    public const string Repo = "P0770/bebene";
    public static string Latest = "", Url = "", Notes = "", Asset = "";
    public static bool Available, ViaGh;
    public static int Cmp(string a, string b) {
        var x = (a ?? "").Split('.'); var y = (b ?? "").Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
            int p = 0, q = 0; if (i < x.Length) int.TryParse(x[i], out p); if (i < y.Length) int.TryParse(y[i], out q);
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }
    // au démarrage : si la version a augmenté depuis la dernière fois, on affichera « mise à jour réussie » (vert)
    public static void Init() {
        string lv = Settings.Str("LastVersion", "");
        if (lv.Length > 0 && Cmp(Program.Version, lv) > 0) Settings.Set("UpdatedShown", Program.Version);
        Settings.Set("LastVersion", Program.Version);
    }
    public static bool ShowOk { get { return Settings.Str("UpdatedShown", "") == Program.Version; } }
    static string Gh() { string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"GitHub CLI\gh.exe"); return File.Exists(p) ? p : "gh"; }

    public static void Check() {
        string json = null; ViaGh = false;
        try {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            using (var wc = new WebClient()) {
                wc.Encoding = Encoding.UTF8; wc.Headers["User-Agent"] = "Bebene/" + Program.Version; wc.Headers["Accept"] = "application/vnd.github+json";
                json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            }
        } catch {
            json = Setup.Run(Gh(), "api repos/" + Repo + "/releases/latest", 25000); ViaGh = json != null;   // dépôt privé
        }
        if (json == null) return;
        var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
        Latest = Convert.ToString(d.ContainsKey("tag_name") ? d["tag_name"] : "").TrimStart('v', 'V');
        Url = Convert.ToString(d.ContainsKey("html_url") ? d["html_url"] : "");
        Notes = Convert.ToString(d.ContainsKey("body") ? d["body"] : ""); if (Notes.Length > 3000) Notes = Notes.Substring(0, 3000);
        Asset = "";
        var assets = d.ContainsKey("assets") ? d["assets"] as ArrayList : null;
        if (assets != null) foreach (var o in assets) {
            var a = o as Dictionary<string, object>; if (a == null) continue;
            if (Convert.ToString(a["name"]).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { Asset = Convert.ToString(a["browser_download_url"]); break; }
        }
        Available = Latest.Length > 0 && Asset.Length > 0 && Cmp(Latest, Program.Version) > 0;
    }

    // télécharge le zip, le décompresse, puis un petit script remplace le programme une fois Bébène fermé et le relance
    public static void Install() { Install(false); }
    public static string Install(bool dry) {
        if (!Available && !dry) throw new Exception("aucune nouvelle version");
        string dir = Path.Combine(Path.GetTempPath(), "bebene-maj-" + Latest);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        if (!ViaGh) {
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "Bebene/" + Program.Version; wc.DownloadFile(Asset, Path.Combine(dir, "bebene.zip")); }
        } else if (Setup.Run(Gh(), "release download v" + Latest + " -R " + Repo + " -p \"*.zip\" -D \"" + dir + "\" --clobber", 300000) == null)
            throw new Exception("téléchargement impossible (GitHub CLI)");
        var zips = Directory.GetFiles(dir, "*.zip"); if (zips.Length == 0) throw new Exception("le zip de la version est introuvable");
        string x = Path.Combine(dir, "x");
        System.IO.Compression.ZipFile.ExtractToDirectory(zips[0], x);
        if (!File.Exists(Path.Combine(x, "ClaudeCompanion.exe"))) throw new Exception("le zip ne contient pas Bébène");
        if (dry) return x;                                         // test : téléchargé et décompressé, rien d installé
        string app = Setup.AppDir, ps1 = Path.Combine(dir, "maj.ps1");
        Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
        string script =
            "# Mise à jour de Bébène : attend sa fermeture, remplace le programme (rien d autre), puis le relance\r\n" +
            "try { Wait-Process -Id " + Program.Pid + " -Timeout 40 -ErrorAction Stop } catch { }\r\n" +
            "Start-Sleep -Milliseconds 600\r\n" +
            "$files = Get-ChildItem " + q(x) + " -File | Where-Object { $_.Extension -in '.exe', '.dll', '.html', '.json', '.ico' }\r\n" +
            "foreach ($f in $files) { for ($i = 0; $i -lt 10; $i++) { try { Copy-Item $f.FullName -Destination " + q(app) + " -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } } }\r\n" +
            "Start-Process " + q(Path.Combine(app, "ClaudeCompanion.exe")) + "\r\n";
        File.WriteAllText(ps1, script, new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps1 + "\"") { UseShellExecute = false, CreateNoWindow = true });
        return x;
    }
}

