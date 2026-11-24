// Pont Mac : recrée l'API WebView2 (window.chrome.webview) par-dessus WKWebView.
// ui.html et settings.html restent identiques à ceux de Windows : seul ce fichier, injecté avant leurs scripts, change.
(function () {
  var listeners = [];
  var send = function (m) { window.webkit.messageHandlers.bebene.postMessage(String(m)); };
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: send,
    // WKWebView ne donne pas le chemin des fichiers : le natif lit le glisser-déposer et le presse-papiers lui-même
    postMessageWithAdditionalObjects: function (kind) { send(kind === 'files' ? 'pastefiles' : kind); },
    addEventListener: function (type, f) { if (type === 'message') listeners.push(f); },
    removeEventListener: function (type, f) { var i = listeners.indexOf(f); if (i >= 0) listeners.splice(i, 1); }
  };
  // appelé par le natif (evaluateJavaScript) pour chaque message
  window.__bebeneRecv = function (s) {
    var ev = { data: s };
    listeners.slice().forEach(function (f) { try { f(ev); } catch (e) { console.error(e); } });
  };
  window.BEBENE_OS = 'mac';
  document.documentElement.dataset.os = 'mac';
  // erreurs de la page → journal du natif
  window.addEventListener('error', function (e) { try { send('log\n' + e.message + ' @' + e.lineno); } catch (x) { } });

  // ⌘ fait ce que fait Ctrl sur Windows (⌘K palette, ⌘Entrée envoyer) : les pages ne testent que ctrlKey
  ['keydown', 'keyup'].forEach(function (t) {
    window.addEventListener(t, function (e) {
      if (e.metaKey && !e.ctrlKey) try { Object.defineProperty(e, 'ctrlKey', { value: true }); } catch (x) { }
    }, true);
  });

  // textes : vocabulaire Mac (sans toucher aux fichiers HTML partagés avec Windows)
  var WORDS = [
    [/Ctrl\s*\+\s*/g, '⌘'], [/\bCtrl K\b/g, '⌘K'], [/\bCtrl\b/g, '⌘'],
    [/Notifications Windows/g, 'Notifications'], [/notification Windows/g, 'notification'],
    [/l['’]Explorateur( Windows)?/g, 'le Finder'], [/\bExplorateur\b/g, 'Finder'],
    [/menu Démarrer/g, 'dossier Applications'], [/avec Windows/g, 'avec la session'],
    [/\bsur ton PC\b/g, 'sur ton Mac'], [/\bce PC\b/g, 'ce Mac'], [/\btous tes PC\b/g, 'tous tes appareils'],
    [/Alt\s*\+\s*(Espace|Space)/g, '⌥ Espace']
  ];
  function fixText(root) {
    // les messages (#log) ne sont jamais touchés ni parcourus : la discussion reste rapide même longue
    var w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, { acceptNode: function (x) {
      if (x.nodeType === 1) return x.id === "log" || /^(SCRIPT|STYLE|TEXTAREA|PRE|CODE)$/.test(x.nodeName) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_SKIP;
      return NodeFilter.FILTER_ACCEPT; } }), n;
    while ((n = w.nextNode())) {
      var s = n.nodeValue, o = s;
      if (!s || !/Ctrl|Windows|Explorateur|Démarrer|PC|Alt/.test(s)) continue;
      WORDS.forEach(function (r) { s = s.replace(r[0], r[1]); });
      if (s !== o) n.nodeValue = s;
    }
    if (root.querySelectorAll) root.querySelectorAll('[title]').forEach(function (el) {
      var t = el.getAttribute('title'), o = t; WORDS.forEach(function (r) { t = t.replace(r[0], r[1]); });
      if (t !== o) el.setAttribute('title', t);
    });
  }

  // réglages : ce qui n'existe pas (encore) sur Mac est masqué
  var HIDE = '#calendriers, #mails, #manual, [data-os-hide]';
  function hideRows() {
    ['remindExam', 'remindRdv', 'remindWhen', 'hk1'].forEach(function (id) {
      var el = document.getElementById(id), row = el && el.closest('.row');
      if (row && !row.hidden) {
        row.hidden = true;
        if (id === 'hk1' && row.nextElementSibling && row.nextElementSibling.classList.contains('row')) row.nextElementSibling.hidden = true;
      }
    });
    document.querySelectorAll(HIDE).forEach(function (el) { el.hidden = true; });
    var sys = document.querySelector('#systeme .row');
    if (sys && !document.getElementById('macHk')) {
      var r = document.createElement('div'); r.className = 'row'; r.id = 'macHk';
      r.innerHTML = '<div class="lbl"><b>Raccourci</b><span style="white-space:normal">⌥ Espace ouvre la discussion. Depuis le Finder, il joint les fichiers sélectionnés.</span></div>';
      sys.parentNode.insertBefore(r, sys.nextSibling);
    }
  }
  function macSteps() {
    if (typeof STEPS === 'undefined' || STEPS.__mac) return;              // assistant d'installation de settings.html
    STEPS.__mac = true;
    var T = {
      windows: ['macOS', 'Bébène fonctionne sur macOS 13 Ventura ou plus récent, sur les Mac Apple Silicon et Intel.'],
      webview: ["Composant d'affichage (WebKit)", 'Déjà intégré à macOS : rien à faire.'],
      claude: [null, "Le moteur de Bébène : c'est lui qui parle à Claude et agit sur ton Mac. L'installation se lance dans le Terminal."],
      login: [null, "Il faut un abonnement Claude Pro ou Max. Claude Code s'ouvre dans le Terminal : connecte-toi, puis tape /exit."],
      onedrive: ['OneDrive ou iCloud Drive', 'Conseillé : pour retrouver la même mémoire et les mêmes projets sur ton Mac et ton PC.'],
      hub: [null, "Le dossier où Bébène garde ta mémoire, ton journal et tes projets. Avec le même OneDrive qu'un PC, il arrive tout seul."],
      install: ['Ranger Bébène dans Applications', 'Glisse Bebene.app dans le dossier Applications, puis relance-le depuis là.']
    };
    for (var i = STEPS.length - 1; i >= 0; i--) {
      var s = STEPS[i], t = T[s.id];
      if (s.id === 'calendars' || s.id === 'mail') { STEPS.splice(i, 1); continue; }
      if (t) { if (t[0]) s.t = t[0]; s.d = t[1]; }
      if (s.id === 'webview' || s.id === 'onedrive') delete s.b;
      if (s.id === 'install') s.b = 'Montrer';
    }
  }
  function adapt() { macSteps(); hideRows(); fixText(document.body); }
  document.addEventListener('DOMContentLoaded', function () {
    adapt();
    var busy = false;
    new MutationObserver(function (ms) {
      if (busy || ms.every(function (m) { return m.target.closest && m.target.closest("#log"); })) return;
      busy = true;
      requestAnimationFrame(function () { try { hideRows(); fixText(document.body); } finally { busy = false; } });
    }).observe(document.body, { childList: true, subtree: true });
  });
})();
