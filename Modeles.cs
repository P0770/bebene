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

// ---------- Modèles de référence : rapport LaTeX, présentation, Word, code ----------
// Rangés dans ClaudeHub/modeles/<type>/ (synchronisés par OneDrive). Une analyse unique (Claude) écrit PROFIL.md :
// le patron résumé (format, structure, style), pour ne pas relire tout le modèle à chaque demande.
static class Templates {
    public static readonly string[] Kinds = { "rapport", "presentation", "word", "excel", "code" };
    public const string Profile = "PROFIL.md";
    public static string Dir { get { return Path.Combine(Setup.Hub, "modeles"); } }
    public static string KindDir(string k) { return Path.Combine(Dir, k); }
    public static bool Valid(string k) { return Array.IndexOf(Kinds, k) >= 0; }
    static readonly HashSet<string> analyzing = new HashSet<string>(), copying = new HashSet<string>();
    public static readonly Dictionary<string, string> Errors = new Dictionary<string, string>();

    static List<string> Files(string k) {
        var r = new List<string>(); string d = KindDir(k);
        if (!Directory.Exists(d)) return r;
        try {
            foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) {
                string n = Path.GetFileName(f);
                if (n.StartsWith("~$") || string.Equals(Path.Combine(d, Profile), f, StringComparison.OrdinalIgnoreCase)) continue;
                r.Add(f);
            }
        } catch (Exception ex) { Program.Log("Modèles", ex.Message); }
        return r;
    }
    public static bool Has(string k) {                                 // rapide : appelé à chaque message
        string d = KindDir(k); if (!Directory.Exists(d)) return false;
        foreach (var e in Directory.EnumerateFileSystemEntries(d)) { string n = Path.GetFileName(e); if (n != Profile && !n.StartsWith("~$")) return true; }
        return false;
    }

    public static List<Dictionary<string, object>> Status() {
        var list = new List<Dictionary<string, object>>();
        foreach (var k in Kinds) {
            var files = Files(k); long size = 0; DateTime newest = DateTime.MinValue;
            var top = new List<string>();
            foreach (var f in files) {
                try { var fi = new FileInfo(f); size += fi.Length; if (fi.LastWriteTime > newest) newest = fi.LastWriteTime; } catch { }
            }
            string d = KindDir(k);
            if (Directory.Exists(d))
                foreach (var e in Directory.GetFileSystemEntries(d)) {
                    string n = Path.GetFileName(e);
                    if (n == Profile || n.StartsWith("~$")) continue;
                    if (top.Count < 6) top.Add(Directory.Exists(e) ? n + "/" : n);
                }
            string prof = Path.Combine(d, Profile);
            bool hasProf = File.Exists(prof);
            DateTime pt = hasProf ? File.GetLastWriteTime(prof) : DateTime.MinValue;
            string err; lock (Errors) Errors.TryGetValue(k, out err);
            bool busyA, busyC; lock (analyzing) { busyA = analyzing.Contains(k); busyC = copying.Contains(k); }
            list.Add(new Dictionary<string, object> {
                { "id", k }, { "files", files.Count }, { "mb", Math.Round(size / 1048576.0, 1) }, { "top", top },
                { "profile", hasProf }, { "profiled", hasProf ? pt.ToString("dd.MM.yyyy HH:mm") : "" },
                { "stale", hasProf && newest > pt.AddSeconds(5) }, { "analyzing", busyA }, { "copying", busyC }, { "error", err ?? "" }
            });
        }
        return list;
    }

    // copie des fichiers ou dossiers choisis dans modeles/<type>/ (en arrière-plan : un rapport peut être gros)
    public static void Add(string k, string[] paths, Action done) {
        if (!Valid(k) || paths == null || paths.Length == 0) return;
        lock (analyzing) copying.Add(k);
        new Thread(() => {
            try {
                string d = KindDir(k); Directory.CreateDirectory(d);
                foreach (var p in paths) {
                    if (Directory.Exists(p)) CopyDir(p, Path.Combine(d, Path.GetFileName(p.TrimEnd('\\'))));
                    else if (File.Exists(p)) File.Copy(p, Path.Combine(d, Path.GetFileName(p)), true);
                }
                lock (Errors) Errors.Remove(k);
            } catch (Exception ex) { lock (Errors) Errors[k] = "Copie : " + ex.Message; Program.Log("Modèles", ex.ToString()); }
            lock (analyzing) copying.Remove(k);
            done();
        }) { IsBackground = true, Name = "modèles" }.Start();
    }
    static void CopyDir(string src, string dst) {
        if (Path.GetFullPath(dst).StartsWith(Path.GetFullPath(src).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;   // pas de copie dans lui-même
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) {
            string n = Path.GetFileName(f);
            if (n.StartsWith("~$")) continue;
            File.Copy(f, Path.Combine(dst, n), true);
        }
        foreach (var sd in Directory.GetDirectories(src)) {
            string n = Path.GetFileName(sd);
            if (n == ".git" || n == "node_modules" || n == "__pycache__" || n == ".vs" || n == "bin" || n == "obj") continue;   // inutile pour un patron
            CopyDir(sd, Path.Combine(dst, n));
        }
    }

    // vider : vers la Corbeille (récupérable)
    public static void Clear(string k) {
        if (!Valid(k)) return;
        string d = KindDir(k);
        if (!Directory.Exists(d)) return;
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(d, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { lock (Errors) Errors[k] = "Vider : " + ex.Message; }
    }

    static string Brief(string k) {
        switch (k) {
            case "rapport": return "un RAPPORT (souvent LaTeX). Décris : classe et préambule (packages, macros, mise en page) ; arborescence des fichiers (fichier principal, chapitres, images, bibliographie) ; "
                + "page de titre ; plan type et rôle de chaque section ; style des figures, tableaux, légendes, équations, références ; bibliographie ; ton, personne grammaticale et longueur typique. "
                + "Sépare ce qu'il faut reprendre tel quel (préambule, page de titre, mise en page) de ce qu'il faut adapter au nouveau sujet.";
            case "presentation": return "une PRÉSENTATION (PowerPoint, Beamer ou PDF). Décris : thème et masque (couleurs, polices, logo), dispositions utilisées, plan type, nombre de diapositives, "
                + "densité de texte par diapo, place des images et schémas, numérotation, diapo de titre et de fin. Indique quel fichier copier pour partir du même masque.";
            case "word": return "un DOCUMENT WORD. Décris : styles (titres, corps, légendes, citations), polices et tailles, marges, en-tête et pied de page, page de garde, table des matières, "
                + "numérotation, plan type, ton. Indique quel fichier copier pour garder les styles.";
            case "excel": return "un CLASSEUR EXCEL. Décris : feuilles et leur rôle ; disposition des tableaux (en-têtes, zones de saisie / de calcul / de résultats) ; "
                + "formules typiques et fonctions utilisées ; tableaux structurés, noms définis, validations ; mise en forme (couleurs, polices, formats de nombres, mise en forme conditionnelle) ; "
                + "graphiques (type, style) ; macros éventuelles. Pour lire les formules, regarde xl/worksheets/*.xml (balises <f>) et xl/sharedStrings.xml. Indique quel fichier copier pour garder la mise en forme.";
            default: return "du CODE écrit par l'utilisateur. Le but : que le code produit lui ressemble et qu'il le comprenne. Décris : langages ; NIVEAU de l'auteur "
                + "(constructions qu'il utilise, et celles qu'il n'utilise jamais : à éviter) ; nommage ; langue et densité des commentaires ; découpage en fichiers et fonctions ; "
                + "gestion des erreurs ; bibliothèques habituelles ; formatage. Termine par « Règles pour écrire comme l'auteur » : liste courte et concrète, avec un petit exemple représentatif.";
        }
    }

    // analyse : Claude lit le modèle et écrit PROFIL.md (une fois ; refaite quand le modèle change)
    public static void Analyze(string k, Action done) {
        if (!Valid(k) || !Has(k)) return;
        lock (analyzing) { if (analyzing.Contains(k)) return; analyzing.Add(k); }
        lock (Errors) Errors.Remove(k);
        done();
        new Thread(() => {
            try {
                string prompt = "Le dossier courant est le MODÈLE DE RÉFÉRENCE de l'utilisateur pour " + Brief(k) + " "
                    + "Explore-le (Glob, Read ; seulement ce qui est utile, pas besoin de tout lire), puis écris le fichier " + Profile + " dans le dossier courant, en français, 60 à 160 lignes. "
                    + "Ce profil servira de PATRON pour produire de NOUVEAUX documents du même type sur d'AUTRES sujets : décris le format et le style, pas le contenu du sujet. "
                    + "Cite les chemins relatifs des fichiers clés à réutiliser ou copier. "
                    + "Fichiers .docx/.pptx/.xlsx : ce sont des archives zip ; copie-les dans $env:TEMP puis décompresse la copie avec PowerShell (Expand-Archive) pour lire le XML (document.xml, styles.xml, slides, slideLayouts). "
                    + "Ne modifie, ne déplace et ne supprime AUCUN fichier du dossier : écris seulement " + Profile + ". Réponds juste « fini » à la fin.";
                string args = "-p --setting-sources \"\" --strict-mcp-config --no-session-persistence --permission-mode acceptEdits --output-format json"
                    + " --allowedTools \"Read,Glob,Grep,Write,Edit,Bash(powershell*),PowerShell\""
                    + " --settings " + Util.Quote(ChatPanel.GuardSettings());
                string model = Settings.Str("Model", "");
                if (Regex.IsMatch(model, "^[a-z0-9.-]+$")) args += " --model " + model;
                var psi = new ProcessStartInfo("claude", args) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = KindDir(k),
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                psi.EnvironmentVariables["CAVEMAN_DEFAULT_MODE"] = "off";     // profil écrit en prose normale
                Util.FreshPath();
                using (var p = Process.Start(psi)) {
                    new Thread(() => { try { if (!p.WaitForExit(15 * 60000)) p.Kill(); } catch { } }) { IsBackground = true }.Start();
                    var bytes = Encoding.UTF8.GetBytes(prompt);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.Close();
                    var errTask = p.StandardError.ReadToEndAsync();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (!File.Exists(Path.Combine(KindDir(k), Profile)) || DateTime.Now - File.GetLastWriteTime(Path.Combine(KindDir(k), Profile)) > TimeSpan.FromMinutes(20)) {
                        var m = Regex.Match(outp, "\"result\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                        string why = m.Success ? Regex.Unescape(m.Groups[1].Value) : (errTask.Result ?? "").Trim();
                        lock (Errors) Errors[k] = "Analyse non terminée" + (why.Length > 0 ? " : " + (why.Length > 200 ? why.Substring(0, 200) + "…" : why) : ".");
                    }
                }
            } catch (Exception ex) { lock (Errors) Errors[k] = "Analyse : " + ex.Message; Program.Log("Modèles", ex.ToString()); }
            lock (analyzing) analyzing.Remove(k);
            done();
        }) { IsBackground = true, Name = "analyse modèle" }.Start();
    }

    // ligne du prompt système : seulement les types fournis (change rarement → le cache reste valable)
    public static string PersonaLine() {
        var have = new List<string>();
        foreach (var k in Kinds) if (Has(k)) have.Add(k);
        if (have.Count == 0) return "";
        return "MODÈLES DE RÉFÉRENCE de l'utilisateur dans " + Dir + " (sous-dossiers : " + string.Join(", ", have.ToArray()) + "). "
            + "Avant de produire un rapport, une présentation, un document Word, un classeur Excel ou du code, lis <type>/" + Profile + " (s'il manque, parcours le dossier), puis seulement les fichiers utiles du modèle. "
            + "Reprends son FORMAT (structure, mise en page, préambule, styles, ton, longueur), pas son contenu, et n'en prends que ce qui sert au nouveau sujet. "
            + "Word / PowerPoint / Excel : pars d'une COPIE du fichier modèle et remplaces-en le contenu (styles, masque et mise en forme gardés). LaTeX : reprends le préambule, la page de titre et l'organisation des fichiers. "
            + "Code : écris dans le style et au niveau de l'utilisateur décrits dans code/" + Profile + " (pas de constructions qu'il n'utilise pas), pour qu'il le reconnaisse et le comprenne. "
            + "Le nouveau travail va dans le dossier de travail, jamais dans modeles/ (ne modifie jamais les modèles). "
            + "L'utilisateur peut te demander de ranger un fichier comme modèle : copie-le dans le bon sous-dossier et dis-lui de relancer l'analyse dans Réglages > Mes modèles. ";
    }
}

