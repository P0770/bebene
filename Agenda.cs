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
                    foreach (var e in Parse(ics, from, to)) { e.Cal = name; e.Color = ColorOf(i); e.ForList = list; e.Kind = Classify(e.Title); if (e.Kind.Length == 0) e.Kind = Classify(name); all.Add(e); }   // calendrier « examen » / « rdv » : tout ce qu il contient compte
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
    public static bool DoneToday { get { return Settings.Str("MailScanDay", "") == DateTime.Today.ToString("yyyy-MM-dd"); } }

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
        Settings.Set("MailScanDay", DateTime.Today.ToString("yyyy-MM-dd"));   // noté dès le départ : un échec ne relance pas d'analyse auto le même jour
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

