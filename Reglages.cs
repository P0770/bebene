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

    // ---- au premier plan quand on l utilise (même devant la discussion), puis fenêtre normale : un clic sur une autre fenêtre la fait passer devant ----
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    const uint SWP_KEEP = 0x0001 | 0x0002 | 0x0010;                           // NOSIZE | NOMOVE | NOACTIVATE
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, SWP_KEEP); }   // HWND_TOPMOST
    protected override void OnDeactivate(EventArgs e) {
        base.OnDeactivate(e);
        var t = new System.Windows.Forms.Timer { Interval = 60 };             // laisse Windows activer la fenêtre cliquée
        t.Tick += (s, e2) => {
            t.Stop(); t.Dispose(); if (IsDisposed || ContainsFocus) return;
            SetWindowPos(Handle, (IntPtr)(-2), 0, 0, 0, 0, SWP_KEEP);           // HWND_NOTOPMOST
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != Handle && (GetWindowLong(fg, -20) & 0x8) == 0) SetWindowPos(Handle, fg, 0, 0, 0, 0, SWP_KEEP);   // juste derrière la fenêtre cliquée
        };
        t.Start();
    }
    public void BringFront() {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, SWP_KEEP);
        Activate();
    }

    public void Refresh2() { Post("settings\n" + owner.SettingsJson()); }
    public void Goto(string section) { pendingGoto = section; Post("goto\n" + section); }   // la page peut ne pas être prête : redemandé à "get"
    string pendingGoto;

    void Post(string s) {
        if (IsDisposed || Disposing) return;                                    // fenetre fermee : une analyse ou une verification finit apres
        if (InvokeRequired) { try { BeginInvoke((Action)(() => Post(s))); } catch { } return; }   // appels depuis les vérifications en arrière-plan
        try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(s); } catch (ObjectDisposedException) { }
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
            case "tpl": PostTemplates(); break;
            case "tpladd": {                                                  // tpladd\n<type>\nfiles|folder
                var p = v.Split('\n'); if (p.Length < 2 || !Templates.Valid(p[0])) break;
                string[] paths = null;
                if (p[1] == "folder") {
                    using (var d = new FolderBrowserDialog { Description = "Dossier du modèle (il sera copié dans ta mémoire ClaudeHub)" })
                        if (d.ShowDialog(this) == DialogResult.OK) paths = new[] { d.SelectedPath };
                } else {
                    using (var d = new OpenFileDialog { Multiselect = true, Title = "Fichiers du modèle (ils seront copiés dans ta mémoire ClaudeHub)" })
                        if (d.ShowDialog(this) == DialogResult.OK) paths = d.FileNames;
                }
                if (paths == null) break;
                string kind = p[0];
                Templates.Add(kind, paths, () => { PostTemplates(); Templates.Analyze(kind, PostTemplates); });   // copié → analysé tout de suite
                PostTemplates();
                break;
            }
            case "tplopen":
                if (Templates.Valid(v)) { Directory.CreateDirectory(Templates.KindDir(v)); try { Process.Start("explorer.exe", Util.Quote(Templates.KindDir(v))); } catch { } }
                break;
            case "tplanalyze": if (Templates.Valid(v)) Templates.Analyze(v, PostTemplates); break;
            case "tplclear": if (Templates.Valid(v)) { Templates.Clear(v); PostTemplates(); } break;
        }
    }
    void PostTemplates() {
        try { Post("tpl\n" + new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "dir", Templates.Dir }, { "kinds", Templates.Status() } })); }
        catch (Exception ex) { Program.Log("Modèles", ex.ToString()); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); owner.RegisterHotkeys(); try { web.Dispose(); } catch { } }   // raccourcis remis si on fermait pendant la saisie d une combinaison   // ferme = memoire rendue
}
