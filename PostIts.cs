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

// ---------- Post-it : petites notes sur le bureau ----------
// Une note = un fichier ClaudeHub/postits/<id>.md (en-tête --- clé: valeur --- puis le texte) : suivie en mémoire,
// synchronisée par OneDrive, et Claude peut la lire, l'écrire ou en créer une (le dossier est surveillé).
// Visible si « visible: oui » ET (épinglée OU discussion ouverte). Fermer = masquer (gardée) ou supprimer (Corbeille).
class Note {
    public string Id, Text = "", Color = "jaune", Created = "";
    public int X = -1, Y = -1, W = 240, H = 220;
    public bool Pinned, Shown = true, Folded;     // Folded = réduit à sa barre de titre
    public string Title { get { foreach (var l in Text.Split('\n')) { string t = l.Trim().TrimStart('☐', '☑', '•', '-', ' '); if (t.Length > 0) return t.Length > 48 ? t.Substring(0, 48) + "…" : t; } return "(vide)"; } }
}

static class Notes {
    public static readonly string[] Colors = { "jaune", "rose", "vert", "bleu", "orange" };
    public static Color Back(string c) {
        switch (c) {
            case "rose": return Color.FromArgb(255, 199, 214); case "vert": return Color.FromArgb(200, 238, 186);
            case "bleu": return Color.FromArgb(189, 222, 252); case "orange": return Color.FromArgb(255, 205, 168);
            default: return Color.FromArgb(255, 238, 158);
        }
    }
    public static Color Head(string c) { var b = Back(c); return Color.FromArgb(b.R * 88 / 100, b.G * 88 / 100, b.B * 88 / 100); }
    public static string Dir { get { return Path.Combine(Setup.Hub, "postits"); } }
    public static bool Enabled { get { return Settings.Bool("PostIts", true); } }
    static readonly Dictionary<string, Note> all = new Dictionary<string, Note>();
    static readonly Dictionary<string, NoteWindow> wins = new Dictionary<string, NoteWindow>();
    static readonly Dictionary<string, string> written = new Dictionary<string, string>();   // ce que Bébène a écrit : le surveillant l'ignore
    static Companion owner;
    static FileSystemWatcher watcher;
    static System.Windows.Forms.Timer reloadT;
    public static event Action Changed;                                  // la liste a changé (menu de la discussion)

    public static void Init(Companion c) {
        owner = c;
        Load();
        reloadT = new System.Windows.Forms.Timer { Interval = 600 };
        reloadT.Tick += (s, e) => { reloadT.Stop(); Load(); Refresh(); };
        try {
            Directory.CreateDirectory(Dir);
            watcher = new FileSystemWatcher(Dir, "*.md") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, IncludeSubdirectories = false };
            FileSystemEventHandler h = (s, e) => { try { owner.BeginInvoke((Action)(() => { reloadT.Stop(); reloadT.Start(); })); } catch { } };
            watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
            watcher.Renamed += (s, e) => h(s, e);
            watcher.EnableRaisingEvents = true;
        } catch (Exception ex) { Program.Log("Post-it", ex.Message); }
        Refresh();
    }

    static string FileOf(string id) { return Path.Combine(Dir, id + ".md"); }
    static Note Parse(string id, string raw) {
        var n = new Note { Id = id };
        raw = raw.Replace("\r\n", "\n");
        if (raw.StartsWith("---\n")) {
            int end = raw.IndexOf("\n---", 4);
            if (end > 0) {
                foreach (var l in raw.Substring(4, end - 4).Split('\n')) {
                    int c = l.IndexOf(':'); if (c < 0) continue;
                    string k = l.Substring(0, c).Trim().ToLower(), v = l.Substring(c + 1).Trim();
                    int iv; bool yes = v == "oui" || v == "true" || v == "1";
                    switch (k) {
                        case "couleur": if (Array.IndexOf(Colors, v) >= 0) n.Color = v; break;
                        case "x": if (int.TryParse(v, out iv)) n.X = iv; break;
                        case "y": if (int.TryParse(v, out iv)) n.Y = iv; break;
                        case "w": if (int.TryParse(v, out iv)) n.W = Math.Max(160, Math.Min(900, iv)); break;
                        case "h": if (int.TryParse(v, out iv)) n.H = Math.Max(120, Math.Min(900, iv)); break;
                        case "epingle": case "épinglé": case "epinglé": n.Pinned = yes; break;
                        case "visible": n.Shown = yes; break;
                        case "reduit": case "réduit": n.Folded = yes; break;
                        case "cree": case "créé": n.Created = v; break;
                    }
                }
                int body = raw.IndexOf('\n', end + 1);
                raw = body < 0 ? "" : raw.Substring(body + 1);
            }
        }
        n.Text = raw.TrimEnd('\n');
        return n;
    }
    static string Serialize(Note n) {
        return "---\ncouleur: " + n.Color + "\nx: " + n.X + "\ny: " + n.Y + "\nw: " + n.W + "\nh: " + n.H
            + "\nepingle: " + (n.Pinned ? "oui" : "non") + "\nvisible: " + (n.Shown ? "oui" : "non") + (n.Folded ? "\nreduit: oui" : "") + "\ncree: " + n.Created + "\n---\n" + n.Text + "\n";
    }
    static void Load() {
        var seen = new HashSet<string>();
        try {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.md")) {
                    string id = Path.GetFileNameWithoutExtension(f);
                    if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,60}$")) continue;
                    string raw; try { raw = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }   // en cours d'écriture : relu au prochain signal
                    seen.Add(id);
                    string mine; lock (written) written.TryGetValue(id, out mine);
                    if (mine != null && mine == raw && all.ContainsKey(id)) continue;          // c'est notre propre écriture
                    var n = Parse(id, raw);
                    Note old;
                    if (all.TryGetValue(id, out old)) {                                       // modifiée ailleurs (Claude, autre PC)
                        NoteWindow w;
                        if (wins.TryGetValue(id, out w) && w.Editing) n.Text = old.Text;        // on ne coupe pas la frappe en cours
                    }
                    all[id] = n;
                    NoteWindow win; if (wins.TryGetValue(id, out win)) win.Apply(n);
                }
        } catch (Exception ex) { Program.Log("Post-it", ex.Message); }
        foreach (var id in new List<string>(all.Keys)) if (!seen.Contains(id)) { all.Remove(id); CloseWin(id); }
        if (Changed != null) Changed();
    }
    public static void Save(Note n) {
        if (!all.ContainsKey(n.Id)) return;                                      // supprime entre-temps
        try {
            Directory.CreateDirectory(Dir);
            string raw = Serialize(n);
            lock (written) written[n.Id] = raw;
            File.WriteAllText(FileOf(n.Id), raw, new UTF8Encoding(false));
        } catch (Exception ex) { Program.Log("Post-it", ex.Message); }
        if (Changed != null) Changed();
    }

    // afficher / cacher les fenêtres selon l'état de chaque note et de la discussion
    public static void Refresh() {
        if (owner == null) return;
        bool chat = owner.Panel != null && owner.Panel.Visible;
        foreach (var n in all.Values) {
            bool show = Enabled && n.Shown && (n.Pinned || chat);
            NoteWindow w; wins.TryGetValue(n.Id, out w);
            if (show) {
                if (w == null || w.IsDisposed) { w = new NoteWindow(n); wins[n.Id] = w; }
                if (!w.Visible) w.ShowQuiet();
            } else if (w != null && !w.IsDisposed && w.Visible) w.Hide();
        }
    }
    static void CloseWin(string id) { NoteWindow w; if (wins.TryGetValue(id, out w)) { wins.Remove(id); if (!w.IsDisposed) w.Close(); } }

    public static void New() {
        var n = new Note { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Color = Colors[all.Count % Colors.Length], Pinned = owner.Panel == null || !owner.Panel.Visible };   // créée sans la discussion : épinglée, sinon elle disparaîtrait
        all[n.Id] = n; Save(n);
        Settings.Set("PostIts", true);
        Refresh();
        NoteWindow w; if (wins.TryGetValue(n.Id, out w)) { w.Activate(); w.FocusText(); }
    }
    public static void Show(string id) {
        Note n; if (!all.TryGetValue(id, out n)) return;
        n.Shown = true;
        if (owner.Panel == null || !owner.Panel.Visible) n.Pinned = true;
        Save(n); Refresh();
        NoteWindow w; if (wins.TryGetValue(id, out w) && w.Visible) w.Activate();
    }
    public static void Hide(string id) { Note n; if (!all.TryGetValue(id, out n)) return; n.Shown = false; Save(n); Refresh(); }
    public static void Delete(string id) {
        Note n; if (!all.TryGetValue(id, out n)) return;
        all.Remove(id); CloseWin(id);
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(FileOf(id), Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { Program.Log("Post-it", ex.Message); }
        if (Changed != null) Changed();
    }
    public static string Json() {
        var list = new List<Dictionary<string, object>>();
        foreach (var n in all.Values)
            list.Add(new Dictionary<string, object> { { "id", n.Id }, { "title", n.Title }, { "color", n.Color }, { "shown", n.Shown }, { "pinned", n.Pinned }, { "created", n.Created } });
        list.Sort((a, b) => string.Compare((string)b["id"], (string)a["id"], StringComparison.Ordinal));
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "enabled", Enabled }, { "notes", list } });
    }
    public static string PersonaLine() {
        if (!Enabled) return "";
        return "POST-IT de l'utilisateur : fichiers " + Dir + "\\<id>.md (en-tête entre --- avec couleur: jaune|rose|vert|bleu|orange, epingle: oui|non, visible: oui|non, puis le texte). "
            + "Quand il demande de noter quelque chose sur un post-it (liste, rappel, info), crée un fichier <AAAAMMJJ-HHMMSS>.md avec l'en-tête « ---\\ncouleur: jaune\\nepingle: oui\\nvisible: oui\\n--- » puis le texte "
            + "(listes : « • » ou cases « ☐ » / « ☑ »). Pour en modifier un, change seulement le texte sous l'en-tête. Il s'affiche tout seul sur son écran. ";
    }
}

// la fenêtre d'un post-it : en-tête glissable (+ nouveau, couleur, épingle, fermer), texte éditable, coin pour redimensionner
class NoteWindow : Form {
    Note n;
    readonly RichTextBox box;
    readonly System.Windows.Forms.Timer saveT = new System.Windows.Forms.Timer { Interval = 700 };
    const int HeadH = 28, Pad = 10;
    bool applying;
    public bool Editing { get { return box.Focused || saveT.Enabled; } }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    public NoteWindow(Note note) {
        n = note;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        DoubleBuffered = true; MinimumSize = new Size(160, 120); KeyPreview = true; MaximizeBox = false; MinimizeBox = false;
        Text = "Post-it";
        box = new RichTextBox { BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5f), ForeColor = Color.FromArgb(48, 40, 30),
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = true, ShortcutsEnabled = true, AcceptsTab = false };
        Controls.Add(box);
        box.TextChanged += (s, e) => { Invalidate(new Rectangle(0, 0, ClientSize.Width, HeadH)); if (applying) return; saveT.Stop(); saveT.Start(); };
        box.KeyDown += OnKey;
        box.MouseUp += (s, e) => ToggleBox(e.Location);
        box.LinkClicked += (s, e) => {                                       // seulement les liens web et mail (un post-it peut venir de Claude ou de OneDrive)
            if (Regex.IsMatch(e.LinkText ?? "", "^(https?://|mailto:)", RegexOptions.IgnoreCase)) try { Process.Start(e.LinkText); } catch { }
        };
        saveT.Tick += (s, e) => { saveT.Stop(); n.Text = box.Text.Replace("\r", ""); Notes.Save(n); };
        VisibleChanged += (s, e) => { if (!Visible) CloseConfirm(); };
        Apply(n);
        Place();
    }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; cp.ExStyle |= 0x80; return cp; } }   // ombre ; pas dans Alt+Tab
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { }   // coins arrondis Windows 11
    }
    public void ShowQuiet() { Place(); Show(); }
    public void FocusText() { box.Focus(); box.SelectionStart = box.TextLength; }

    public void Apply(Note note) {
        n = note;
        BackColor = Notes.Back(n.Color); box.BackColor = BackColor;
        if (box.Text.Replace("\r", "") != n.Text) { applying = true; int sel = box.SelectionStart; box.Text = n.Text; box.SelectionStart = Math.Min(sel, box.TextLength); applying = false; }
        FoldLayout();
        Invalidate();
    }
    // ---- réduire à la barre de titre (bouton –, double-clic sur la barre, ou clic droit) ----
    void ToggleFold() { n.Folded = !n.Folded; Notes.Save(n); FoldLayout(); }
    void FoldLayout() {
        if (n.Folded) { box.Visible = false; MinimumSize = new Size(160, HeadH); Height = HeadH; }
        else { MinimumSize = new Size(160, 120); if (Height < 120) Height = n.H; box.Visible = true; }
        Invalidate();
    }
    void Place() {
        var wa = Screen.PrimaryScreen.WorkingArea;
        MinimumSize = new Size(160, n.Folded ? HeadH : 120);
        Size = new Size(n.W, n.Folded ? HeadH : n.H);
        int x = n.X, y = n.Y;
        var vs = SystemInformation.VirtualScreen;
        if (x < vs.Left || y < vs.Top || x > vs.Right - 60 || y > vs.Bottom - 40) {                 // jamais placée, ou écran d un autre PC
            int k = Math.Abs(n.Id.GetHashCode()) % 6;
            x = wa.Right - n.W - 520 - k * 26; y = wa.Top + 80 + k * 26;
        }
        Location = new Point(x, y);
        Layout2();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); Invalidate(); }
    void Layout2() {
        if (box == null) return;
        box.SetBounds(Pad, HeadH + 4, Math.Max(10, ClientSize.Width - 2 * Pad), Math.Max(10, ClientSize.Height - HeadH - 4 - Pad - 4));
    }

    // ---- en-tête dessiné, sobre : + à gauche ; couleur, épingle, corbeille, –, × à droite ----
    Rectangle BtnNew { get { return new Rectangle(6, 3, 22, 22); } }
    Rectangle BtnClose { get { return new Rectangle(ClientSize.Width - 28, 3, 22, 22); } }
    Rectangle BtnFold { get { return new Rectangle(ClientSize.Width - 52, 3, 22, 22); } }
    Rectangle BtnDel { get { return new Rectangle(ClientSize.Width - 76, 3, 22, 22); } }
    Rectangle BtnPin { get { return new Rectangle(ClientSize.Width - 100, 3, 22, 22); } }
    Rectangle BtnColor { get { return new Rectangle(ClientSize.Width - 124, 3, 22, 22); } }
    Rectangle[] Btns { get { return new[] { BtnNew, BtnClose, BtnFold, BtnDel, BtnPin, BtnColor }; } }
    bool OnBtn(Point p) { foreach (var r in Btns) if (r.Contains(p)) return true; return false; }
    Point hover = new Point(-1, -1);
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var hb = new SolidBrush(Notes.Head(n.Color))) g.FillRectangle(hb, 0, 0, ClientSize.Width, HeadH);
        var ink = Color.FromArgb(120, 60, 45, 30);
        foreach (var r in Btns)
            if (r.Contains(hover)) using (var b = new SolidBrush(r == BtnDel ? Color.FromArgb(45, 190, 50, 40) : Color.FromArgb(28, 0, 0, 0))) g.FillEllipse(b, r);
        using (var p = new Pen(ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
            var r = BtnNew; int cx = r.X + 11, cy = r.Y + 11;
            g.DrawLine(p, cx - 4, cy, cx + 4, cy); g.DrawLine(p, cx, cy - 4, cx, cy + 4);
            r = BtnClose; cx = r.X + 11; cy = r.Y + 11;                              // × = masquer
            g.DrawLine(p, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f); g.DrawLine(p, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f);
            r = BtnFold; cx = r.X + 11; cy = r.Y + 11;
            if (n.Folded) g.DrawRectangle(p, cx - 4, cy - 3, 8, 7);                  // ▢ = rouvrir
            else g.DrawLine(p, cx - 4, cy + 1, cx + 4, cy + 1);                        // – = réduire
            r = BtnPin; cx = r.X + 11; cy = r.Y + 11;                                // punaise : tête ronde + pointe
            if (n.Pinned) using (var b = new SolidBrush(Color.FromArgb(217, 119, 87))) g.FillEllipse(b, cx - 4, cy - 6, 8, 8);
            else g.DrawEllipse(p, cx - 3.5f, cy - 5.5f, 7, 7);
            g.DrawLine(p, cx, cy + 2, cx, cy + 6);
            r = BtnDel; cx = r.X + 11; cy = r.Y + 11;                                // corbeille = supprimer
            g.DrawLine(p, cx - 5, cy - 3, cx + 5, cy - 3); g.DrawLine(p, cx - 1.5f, cy - 5, cx + 1.5f, cy - 5);
            g.DrawLine(p, cx - 3.5f, cy - 3, cx - 2.8f, cy + 5); g.DrawLine(p, cx + 3.5f, cy - 3, cx + 2.8f, cy + 5); g.DrawLine(p, cx - 2.8f, cy + 5, cx + 2.8f, cy + 5);
        }
        string next = Notes.Colors[(Array.IndexOf(Notes.Colors, n.Color) + 1) % Notes.Colors.Length];
        var rc = BtnColor;
        using (var b = new SolidBrush(Notes.Back(next))) g.FillEllipse(b, rc.X + 6, rc.Y + 6, 10, 10);
        using (var p = new Pen(Color.FromArgb(90, 60, 45, 30), 1f)) g.DrawEllipse(p, rc.X + 6, rc.Y + 6, 10, 10);
        if (n.Folded) {                                                      // réduit : le titre (1re ligne) dans la barre
            var tr = new RectangleF(32, 6, Math.Max(10, BtnColor.X - 36), 18);
            using (var b = new SolidBrush(Color.FromArgb(200, 50, 38, 26))) using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(n.Title, f, b, tr, sf);
        }
        else if (box.TextLength == 0 && ClientSize.Width > 260)              // aide dans l en-tête tant que la note est vide
            using (var b = new SolidBrush(Color.FromArgb(120, 60, 45, 30))) using (var f = new Font("Segoe UI", 8.5f))
                g.DrawString("« - » liste · « [] » case", f, b, 32, 7);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hover = e.Location; Invalidate(new Rectangle(0, 0, ClientSize.Width, HeadH)); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = new Point(-1, -1); Invalidate(new Rectangle(0, 0, ClientSize.Width, HeadH)); }
    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (BtnNew.Contains(e.Location)) Notes.New();
        else if (BtnClose.Contains(e.Location)) Notes.Hide(n.Id);
        else if (BtnFold.Contains(e.Location)) ToggleFold();
        else if (BtnDel.Contains(e.Location)) ShowConfirm();
        else if (BtnPin.Contains(e.Location)) { n.Pinned = !n.Pinned; Notes.Save(n); Invalidate(); Notes.Refresh(); }
        else if (BtnColor.Contains(e.Location)) { n.Color = Notes.Colors[(Array.IndexOf(Notes.Colors, n.Color) + 1) % Notes.Colors.Length]; Notes.Save(n); Apply(n); }
    }

    // glisser par l'en-tête, redimensionner par les bords ; position enregistrée à la fin
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x84) {                                                 // WM_NCHITTEST
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            int w = ClientSize.Width, h = ClientSize.Height, b = 7;
            bool l = p.X < b, r = p.X >= w - b, t = p.Y < 4 && !n.Folded, bo = p.Y >= h - b && !n.Folded;   // réduit : largeur seulement
            if (bo && r) { m.Result = (IntPtr)17; return; } if (bo && l) { m.Result = (IntPtr)16; return; }
            if (t && r) { m.Result = (IntPtr)14; return; } if (t && l) { m.Result = (IntPtr)13; return; }
            if (r) { m.Result = (IntPtr)11; return; } if (l) { m.Result = (IntPtr)10; return; }
            if (bo) { m.Result = (IntPtr)15; return; } if (t) { m.Result = (IntPtr)12; return; }
            if (p.Y < HeadH && !OnBtn(p)) { m.Result = (IntPtr)2; return; }   // HTCAPTION
        }
        if (m.Msg == 0xA3) { ToggleFold(); return; }                         // double-clic sur la barre : réduire / rouvrir
        if (m.Msg == 0x0232) { n.X = Left; n.Y = Top; n.W = Width; if (!n.Folded) n.H = Height; Notes.Save(n); }   // WM_EXITSIZEMOVE
        base.WndProc(ref m);
    }

    // ---- supprimer : petite bulle juste au-dessus de la corbeille, « Supprimer ? Oui / Non » ----
    Form confirm;
    void CloseConfirm() { if (confirm != null && !confirm.IsDisposed) confirm.Close(); confirm = null; }
    void ShowConfirm() {
        CloseConfirm();
        var pop = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, TopMost = true,
            BackColor = Color.FromArgb(255, 252, 247), Size = new Size(196, 38), KeyPreview = true };
        var lbl = new Label { Text = "Supprimer ?", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(60, 45, 30), AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(10, 0, 84, 38) };
        var yes = new Button { Text = "Oui", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(200, 64, 52), ForeColor = Color.White, Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand, Bounds = new Rectangle(96, 7, 44, 24) };
        var no = new Button { Text = "Non", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 233, 224), ForeColor = Color.FromArgb(70, 60, 50), Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand, Bounds = new Rectangle(144, 7, 44, 24) };
        yes.FlatAppearance.BorderSize = 0; no.FlatAppearance.BorderSize = 0;
        yes.Click += (s, e) => { CloseConfirm(); Notes.Delete(n.Id); };
        no.Click += (s, e) => CloseConfirm();
        pop.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) CloseConfirm(); else if (e.KeyCode == Keys.Enter) { CloseConfirm(); Notes.Delete(n.Id); } };
        pop.Deactivate += (s, e) => { if (confirm == pop) pop.BeginInvoke((Action)CloseConfirm); };   // clic ailleurs = annulé
        pop.HandleCreated += (s, e) => { try { int v = 2; DwmSetWindowAttribute(pop.Handle, 33, ref v, 4); } catch { } };   // coins arrondis + ombre Windows 11
        pop.Controls.AddRange(new Control[] { lbl, yes, no });
        var tr = RectangleToScreen(BtnDel); var wa = Screen.FromControl(this).WorkingArea;
        int x = Math.Max(wa.Left + 4, Math.Min(wa.Right - pop.Width - 4, tr.Left + tr.Width / 2 - pop.Width / 2));
        int y = tr.Top - pop.Height - 6; if (y < wa.Top) y = tr.Bottom + 6;  // pas de place au-dessus : juste en dessous
        pop.Location = new Point(x, y);
        confirm = pop;
        pop.Show(this); pop.Activate(); no.Focus();
    }

    // ---- listes : « - » / « • » et cases « ☐ » continuent à la ligne suivante ; « [] » devient une case ; clic sur une case = cochée ----
    void OnKey(object s, KeyEventArgs e) {
        if (e.KeyCode == Keys.V && e.Control) {                              // coller sans mise en forme
            e.SuppressKeyPress = true;
            if (Clipboard.ContainsText()) box.SelectedText = Clipboard.GetText().Replace("\r\n", "\n");
            return;
        }
        if (e.KeyCode == Keys.Escape) { ActiveControl = null; return; }
        int line = box.GetLineFromCharIndex(box.SelectionStart), start = box.GetFirstCharIndexFromLine(line);
        string cur = line < box.Lines.Length ? box.Lines[line] : "";
        if (e.KeyCode == Keys.Space && box.SelectionStart - start == 2 && cur.StartsWith("[]")) {
            e.SuppressKeyPress = true; box.Select(start, 2); box.SelectedText = "☐ "; return;
        }
        if (e.KeyCode == Keys.Enter && !e.Shift) {
            string pre = cur.StartsWith("☐ ") || cur.StartsWith("☑ ") ? "☐ " : cur.StartsWith("• ") ? "• " : cur.StartsWith("- ") ? "- " : null;
            if (pre == null) return;
            e.SuppressKeyPress = true;
            if (cur.Trim().Length <= 1) { box.Select(start, cur.Length); box.SelectedText = ""; return; }   // élément vide : fin de la liste
            box.SelectedText = "\n" + pre;
        }
    }
    void ToggleBox(Point p) {
        int i = box.GetCharIndexFromPosition(p);
        if (i < 0 || i >= box.TextLength) return;
        char c = box.Text[i];
        if (c != '☐' && c != '☑') return;
        int ss = box.SelectionStart;
        box.Select(i, 1); box.SelectedText = c == '☐' ? "☑" : "☐"; box.SelectionStart = ss;
    }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (saveT.Enabled) { saveT.Stop(); n.Text = box.Text.Replace("\r", ""); Notes.Save(n); } Invalidate(); }
}

