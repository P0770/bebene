using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// ---------- Historique des conversations : un fichier par conversation ----------
// Avant : tout l historique dans le stockage local de la page (≈ 5 Mo) → 40 conversations au plus, vieilles conversations tronquées,
// et tout réécrit d un bloc à chaque sauvegarde. Maintenant : %LOCALAPPDATA%\ClaudeCompanion\conversations\<id>.json,
// seule la conversation modifiée est écrite. La page reçoit tout au démarrage (window.__CONVS, injecté avant ses scripts).
static class ConvStore {
    static readonly string Dir = Environment.GetEnvironmentVariable("BEBENE_CONVS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "conversations");   // variable : tests
    static readonly object L = new object();
    static bool Ok(string id) { return Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{1,40}$"); }
    public static void Save(string id, string json) {
        if (!Ok(id) || json == null || !json.StartsWith("{")) return;
        lock (L) try {
            Directory.CreateDirectory(Dir);
            string f = Path.Combine(Dir, id + ".json"), tmp = f + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));            // écrit à côté puis remplace : jamais de fichier à moitié écrit
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        } catch (Exception ex) { Program.Log("Conversations", ex.Message); }
    }
    public static void Delete(string id) {
        if (!Ok(id)) return;
        lock (L) try { File.Delete(Path.Combine(Dir, id + ".json")); } catch (Exception ex) { Program.Log("Conversations", ex.Message); }
    }
    // tableau JSON de toutes les conversations (chaque fichier contient déjà un objet JSON écrit par la page)
    public static string AllJson() {
        var sb = new StringBuilder("[");
        lock (L) try {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.json")) {
                    string s = File.ReadAllText(f, Encoding.UTF8).Trim();
                    if (!s.StartsWith("{") || !s.EndsWith("}")) continue;          // fichier abîmé : ignoré
                    if (sb.Length > 1) sb.Append(',');
                    sb.Append(s);
                }
        } catch (Exception ex) { Program.Log("Conversations", ex.Message); }
        return sb.Append(']').ToString();
    }
}

// ---------- Rappels demandés à Bébène (« rappelle-moi dans 20 min ») ----------
// Claude termine sa réponse par ```bebene-rappel {"quand":"AAAA-MM-JJTHH:MM","quoi":"…"}``` ; Bébène l affiche à l heure dite
// (carte au-dessus du chat + notification), même si la discussion est fermée.
static class MyReminders {
    static readonly string File_ = Environment.GetEnvironmentVariable("BEBENE_RAPPELS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCompanion", "rappels-perso.json");   // variable : tests
    static readonly object L = new object();
    static readonly Regex Block = new Regex("```bebene-rappel\\s*(\\{[\\s\\S]*?\\})\\s*```");
    public class Item { public string Quand = "", Quoi = ""; public bool Fait; [ScriptIgnore] public string Id { get { return Quand + "|" + Quoi; } } }
    static List<Item> items;
    static List<Item> All() {
        if (items != null) return items;
        items = new List<Item>();
        try { if (File.Exists(File_)) items = new JavaScriptSerializer().Deserialize<List<Item>>(File.ReadAllText(File_, Encoding.UTF8)) ?? new List<Item>(); } catch { }
        return items;
    }
    static void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            All().RemoveAll(i => i.Fait && When(i) < DateTime.Now.AddDays(-7));   // ménage : vieux rappels déjà montrés
            File.WriteAllText(File_, new JavaScriptSerializer().Serialize(All()), new UTF8Encoding(false));
        } catch (Exception ex) { Program.Log("Rappels", ex.Message); }
    }
    public static DateTime When(Item i) {
        DateTime d; return DateTime.TryParseExact(i.Quand, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : DateTime.MinValue;
    }
    // texte d une réponse de Claude : ajoute les rappels qu il contient (sans doublon)
    public static int AddFrom(string text) {
        int n = 0;
        foreach (Match m in Block.Matches(text ?? "")) {
            try {
                var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(m.Groups[1].Value);
                var it = new Item { Quand = Convert.ToString(o["quand"]).Trim(), Quoi = Convert.ToString(o["quoi"]).Trim() };
                if (it.Quoi.Length == 0 || When(it) == DateTime.MinValue) continue;
                if (it.Quoi.Length > 200) it.Quoi = it.Quoi.Substring(0, 200);
                lock (L) { if (All().Exists(x => x.Id == it.Id)) continue; All().Add(it); Save(); }
                n++;
            } catch { }
        }
        return n;
    }
    public static void Cancel(string id) { lock (L) { if (All().RemoveAll(x => x.Id == id) > 0) Save(); } }
    public static void Snooze(string id, int minutes) {
        lock (L) {
            var it = All().Find(x => x.Id == id); if (it == null) return;
            All().Remove(it);
            All().Add(new Item { Quand = DateTime.Now.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm"), Quoi = it.Quoi });
            Save();
        }
    }
    // rappel arrivé à l heure (le plus ancien d abord) : marqué comme montré
    public static Item Due() {
        lock (L) {
            Item best = null;
            foreach (var i in All()) if (!i.Fait && When(i) <= DateTime.Now && (best == null || When(i) < When(best))) best = i;
            if (best != null) { best.Fait = true; Save(); }
            return best;
        }
    }
    public static List<Item> Today() {
        lock (L) return All().FindAll(i => !i.Fait && When(i).Date == DateTime.Today);
    }
}

// ---------- Point du jour : à la première ouverture de la journée (réglage « Point du jour ») ----------
// Le C# envoie l agenda (calendriers + mails + ajouts à la main) et les rappels du jour ; la page ajoute les post-it épinglés et les tâches en pause.
static class Brief {
    static readonly CultureInfo FR = new CultureInfo("fr-CH");
    public static string Json() {
        var today = new List<object>(); var soon = new List<object>(); var rappels = new List<object>();
        DateTime t0 = DateTime.Today, t1 = t0.AddDays(1), week = t0.AddDays(8);
        foreach (var e in Calendars.Snapshot()) {
            bool isToday = e.Start < t1 && (e.End > t0 || e.Start >= t0);
            if (isToday)
                today.Add(new Dictionary<string, object> { { "t", e.Title }, { "h", e.AllDay ? "Toute la journée" : e.Start.ToString("HH:mm") }, { "l", e.Loc.Replace("\n", " ") }, { "k", e.Kind }, { "m", e.FromMail } });
            else if (e.Kind.Length > 0 && e.ForList && e.Start >= t1 && e.Start < week)
                soon.Add(new Dictionary<string, object> { { "t", e.Title }, { "h", e.Start.ToString(e.AllDay ? "ddd d MMM" : "ddd d MMM, HH:mm", FR) }, { "k", e.Kind }, { "m", e.FromMail } });
        }
        foreach (var r in MyReminders.Today()) rappels.Add(new Dictionary<string, object> { { "t", r.Quoi }, { "h", MyReminders.When(r).ToString("HH:mm") } });
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
            { "date", DateTime.Now.ToString("dddd d MMMM", FR) }, { "today", today }, { "soon", soon }, { "rappels", rappels } });
    }
}
