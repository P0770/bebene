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

