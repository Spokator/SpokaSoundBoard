namespace SpokaSoundBoard.Core.Remote;

/// <summary>
/// The phone remote: one self-contained page (no external file, works on the local network without Internet).
/// Tabs: « Mon deck » (buttons chosen by the user, saved on the PC), Sons, Voix, Musique and Blind test (during a game).
/// </summary>
public static class RemotePage
{
    public const string Manifest = """
{
  "name": "SpokaSoundBoard – Télécommande",
  "short_name": "Spoka",
  "lang": "fr",
  "start_url": ".",
  "display": "standalone",
  "orientation": "any",
  "background_color": "#0f1117",
  "theme_color": "#0f1117",
  "icons": [ { "src": "icon.png", "sizes": "any", "type": "image/png", "purpose": "any" } ]
}
""";

    public const string Html = """"
<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f1117">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Spoka">
<link rel="manifest" href="manifest.webmanifest">
<link rel="apple-touch-icon" href="icon.png">
<link rel="icon" href="icon.png">
<title>Télécommande SpokaSoundBoard</title>
<style>
:root{--bg:#0f1117;--card:#1a1e2b;--card2:#222738;--border:#2a3042;--text:#f1f3f9;--sub:#9aa3b8;--accent:#7c5cff;--on-accent:#fff;
  --ok:#22c55e;--danger:#f43f5e;--warn:#f59e0b;--tile:112px;--safe-b:env(safe-area-inset-bottom,0px);--safe-t:env(safe-area-inset-top,0px)}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;overscroll-behavior:none}
button,input,textarea{font:inherit;color:inherit}
button{background:var(--card);border:1px solid var(--border);border-radius:12px;padding:10px 12px;cursor:pointer;touch-action:manipulation;
  -webkit-user-select:none;user-select:none;-webkit-touch-callout:none}
button:active{transform:scale(.96)}
button:disabled{opacity:.4}
.topbar{position:sticky;top:0;z-index:5;display:flex;align-items:center;gap:8px;padding:calc(8px + var(--safe-t)) 12px 8px;
  background:rgba(15,17,23,.92);backdrop-filter:blur(10px);border-bottom:1px solid var(--border)}
.brand{font-weight:700;font-size:16px;flex:1;display:flex;align-items:center;gap:8px;min-width:0}
.brand img{width:26px;height:26px;border-radius:7px}
.dot{width:9px;height:9px;border-radius:50%;background:var(--warn);flex:none}
.dot.on{background:var(--ok)}
.chip{border-radius:999px;padding:8px 12px;font-size:14px;white-space:nowrap}
.chip.on{border-color:var(--ok);color:var(--ok)}
.chip.off{border-color:var(--danger);color:var(--danger)}
.stopbtn{background:var(--danger);border-color:var(--danger);color:#fff;font-weight:700;border-radius:12px;padding:9px 14px}
main{padding:12px 12px calc(84px + var(--safe-b))}
.view{display:none}.view.active{display:block}
h2{font-size:17px;margin:4px 0 10px;display:flex;align-items:center;gap:8px}
h2 .grow{flex:1}
h3{font-size:14px;color:var(--sub);font-weight:600;margin:18px 0 8px;text-transform:uppercase;letter-spacing:.04em}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(var(--tile),1fr));gap:10px}
.tile{position:relative;min-height:calc(var(--tile) * .78);display:flex;flex-direction:column;justify-content:space-between;text-align:left;
  border-width:2px;padding:10px;border-radius:14px;overflow:hidden}
.tile .ico{font-size:24px;line-height:1}
.tile .lbl{font-weight:600;font-size:14px;margin-top:6px;overflow-wrap:break-word;-webkit-hyphens:auto;hyphens:auto}
.tile .sub{font-size:11px;color:var(--sub);margin-top:2px}
.tile.playing{box-shadow:0 0 0 3px var(--text) inset}
.tile.playing::after{content:"";position:absolute;inset:auto 0 0 0;height:4px;background:var(--text);animation:pulse 1s ease-in-out infinite}
@keyframes pulse{50%{opacity:.25}}
.tile.on{border-color:var(--ok)}.tile.on .sub{color:var(--ok)}
.tile.off{border-color:var(--danger)}.tile.off .sub{color:var(--danger)}
.tile.current{border-color:var(--accent);background:color-mix(in srgb,var(--accent) 22%,var(--card))}
.tile.held{background:var(--ok);color:#06210f;border-color:var(--ok)}
.tile .edit{position:absolute;inset:0;display:none;align-items:flex-end;justify-content:space-between;padding:5px;background:rgba(0,0,0,.12)}
.editing .tile .edit{display:flex}
.edit span{background:rgba(34,39,56,.92);border:1px solid var(--border);border-radius:9px;min-width:30px;height:30px;display:flex;align-items:center;justify-content:center;font-size:13px}
.edit .x{background:var(--danger);border-color:var(--danger);color:#fff;position:absolute;top:5px;right:5px;min-width:26px;height:26px;border-radius:50%}
.editing .tile .sub{visibility:hidden}
.editing .tile{animation:wiggle .35s ease-in-out infinite alternate}
@keyframes wiggle{from{transform:rotate(-.6deg)}to{transform:rotate(.6deg)}}
@media (prefers-reduced-motion:reduce){.editing .tile,.tile.playing::after{animation:none}}
.addtile{border-style:dashed;align-items:center;justify-content:center;text-align:center;color:var(--sub)}
.tabs{display:flex;gap:6px;overflow-x:auto;padding-bottom:6px;margin-bottom:6px;scrollbar-width:none}
.tabs::-webkit-scrollbar{display:none}
.tabs button{border-radius:999px;padding:7px 13px;font-size:13px;white-space:nowrap;flex:none}
.tabs .sel{border-color:var(--accent);background:color-mix(in srgb,var(--accent) 25%,var(--card))}
.search{width:100%;background:var(--card);border:1px solid var(--border);border-radius:12px;padding:11px 12px;margin-bottom:10px;outline:none}
.search:focus,textarea:focus{border-color:var(--accent)}
nav.bottom{position:fixed;left:0;right:0;bottom:0;z-index:5;display:flex;background:rgba(20,23,34,.96);backdrop-filter:blur(10px);
  border-top:1px solid var(--border);padding:6px 6px calc(6px + var(--safe-b))}
nav.bottom button{flex:1;background:none;border:none;border-radius:10px;padding:6px 2px;display:flex;flex-direction:column;align-items:center;gap:2px;font-size:11px;color:var(--sub)}
nav.bottom button .i{font-size:21px}
nav.bottom button.sel{color:var(--text);background:color-mix(in srgb,var(--accent) 22%,transparent)}
nav.bottom button.hidden{display:none}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.card{background:var(--card);border:1px solid var(--border);border-radius:16px;padding:14px;margin-bottom:12px}
.ptt{width:100%;min-height:150px;border-radius:24px;font-size:20px;font-weight:700;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:6px;border-width:2px}
.ptt .ico{font-size:40px}
.ptt.held{background:var(--ok);border-color:var(--ok);color:#06210f}
textarea{width:100%;min-height:84px;resize:vertical;background:var(--bg);border:1px solid var(--border);border-radius:12px;padding:10px;outline:none}
.primary{background:var(--accent);border-color:var(--accent);color:var(--on-accent);font-weight:700}
.phr{font-size:13px;border-radius:999px;padding:8px 12px}
.np{display:flex;gap:14px;align-items:center}
.cover{width:88px;height:88px;border-radius:12px;background:var(--card2);display:flex;align-items:center;justify-content:center;font-size:34px;overflow:hidden;flex:none}
.cover img{width:100%;height:100%;object-fit:cover}
.np .t{font-weight:700;font-size:17px;overflow-wrap:anywhere}.np .a{color:var(--sub);margin-top:2px}
.bar{height:5px;background:var(--card2);border-radius:5px;overflow:hidden;margin:12px 0 4px}.bar div{height:100%;background:var(--accent);width:0}
.times{display:flex;justify-content:space-between;font-size:12px;color:var(--sub)}
.transport{display:flex;justify-content:center;gap:16px;margin:12px 0 4px}
.transport button{width:60px;height:60px;border-radius:50%;font-size:22px;padding:0}
.transport .big{width:74px;height:74px;font-size:28px;background:var(--accent);border-color:var(--accent);color:var(--on-accent)}
.slider{margin:12px 0}.slider label{display:flex;justify-content:space-between;font-size:14px;margin-bottom:6px}.slider label span{color:var(--sub)}
input[type=range]{width:100%;accent-color:var(--accent);height:30px}
.bt-count{font-size:56px;font-weight:800;line-height:1}
.answer{font-size:20px;font-weight:700}.answer small{display:block;font-size:15px;color:var(--accent);font-weight:600;margin-top:2px}
.hint{font-family:ui-monospace,Menlo,Consolas,monospace;color:var(--accent);margin-top:8px;white-space:pre-wrap}
.hidden-answer .answer{filter:blur(9px)}
.player{display:flex;align-items:center;gap:8px;padding:10px 0;border-top:1px solid var(--border)}
.player .n{flex:1;min-width:0}.player .n b{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.player .n span{font-size:12px;color:var(--sub)}
.player button{padding:9px 10px;font-size:13px}
.player button.on{border-color:var(--ok);background:color-mix(in srgb,var(--ok) 25%,var(--card))}
.sheet-bg{position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:20;display:none}
.sheet{position:fixed;left:0;right:0;bottom:0;z-index:21;background:var(--card);border-radius:18px 18px 0 0;max-height:82vh;overflow:auto;
  padding:14px 14px calc(14px + var(--safe-b));transform:translateY(105%);transition:transform .2s}
.sheet.open{transform:none}.sheet-bg.open{display:block}
.sheet h2{position:sticky;top:-14px;background:var(--card);padding:6px 0;margin-top:-6px;z-index:1}
.pick{width:100%;display:flex;align-items:center;gap:10px;text-align:left;margin-bottom:6px}
.pick .c{width:14px;height:14px;border-radius:4px;flex:none}
.pick .l{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pick.in{border-color:var(--ok)}.pick.in::after{content:"✓";color:var(--ok);font-weight:700}
.toast{position:fixed;left:12px;right:12px;bottom:calc(80px + var(--safe-b));z-index:30;background:var(--card2);border:1px solid var(--border);border-radius:12px;
  padding:12px 14px;opacity:0;transform:translateY(10px);transition:all .2s;pointer-events:none}
.toast.show{opacity:1;transform:none}
.empty{color:var(--sub);text-align:center;padding:24px 10px}
.note{color:var(--sub);font-size:13px}
.seg{display:flex;gap:6px}.seg button{flex:1}.seg .sel{border-color:var(--accent);background:color-mix(in srgb,var(--accent) 25%,var(--card))}
@media (orientation:landscape) and (max-height:500px){
  main{padding:8px 12px calc(52px + var(--safe-b))}.ptt{min-height:96px}.bt-count{font-size:40px}
  .topbar{padding-top:calc(4px + var(--safe-t));padding-bottom:4px}.topbar button{padding-top:6px;padding-bottom:6px}
  nav.bottom{padding-top:3px;padding-bottom:calc(3px + var(--safe-b))}
  nav.bottom button{flex-direction:row;justify-content:center;gap:6px;font-size:12px;padding:6px 2px}nav.bottom button .i{font-size:17px}}
</style>
</head>
<body>
<div class="topbar">
  <div class="brand"><img src="icon.png" alt="" onerror="this.remove()"><span>Spoka</span><span class="dot" id="dot" title="Connexion"></span></div>
  <button class="chip" id="micChip" aria-label="Couper ou activer le micro">🎙</button>
  <button class="chip" id="gear" aria-label="Réglages de la télécommande">⚙</button>
  <button class="stopbtn" id="stopAll" aria-label="Arrêter tous les sons">■ Stop</button>
</div>

<main>
  <section class="view active" id="v-deck">
    <h2><span class="grow">Mon deck</span><button class="chip" id="editDeck">Modifier</button></h2>
    <div class="grid" id="deckGrid"></div>
    <div class="empty" id="deckEmpty" hidden>
      Votre deck est vide.<br>Ajoutez-y vos sons, effets de voix, phrases et actions favoris.<br><br>
      <button class="primary" id="autoDeck">Remplir automatiquement</button>
      <button id="addDeck2">Choisir…</button>
    </div>
  </section>

  <section class="view" id="v-pads">
    <input class="search" id="padSearch" type="search" placeholder="Rechercher un son…" aria-label="Rechercher un son">
    <div class="tabs" id="padTabs"></div>
    <div class="grid" id="padGrid"></div>
    <p class="note" id="padNote">Appui long sur un son pour l'ajouter à Mon deck.</p>
  </section>

  <section class="view" id="v-voice">
    <div class="row" style="margin-bottom:10px">
      <button class="chip" id="vMic">Micro</button>
      <button class="chip" id="vFx">Effets</button>
      <button class="chip" id="vRadio">Mode radio</button>
    </div>
    <button class="ptt" id="ptt"><span class="ico">🗣</span><span id="pttLbl">Maintenir pour parler</span><span class="note" id="pttNote"></span></button>
    <div class="card" style="margin-top:12px">
      <h2 style="margin-top:0">Dire dans le micro</h2>
      <textarea id="sayText" maxlength="300" placeholder="Tapez une phrase : la synthèse vocale la dit dans votre micro."></textarea>
      <div class="row" style="margin-top:8px"><button class="primary" id="sayBtn">Dire</button><span class="note" id="sayNote"></span></div>
      <div class="row" id="phrases" style="margin-top:10px"></div>
    </div>
    <h2><span class="grow">Effets de voix</span><button class="chip" id="pPrev" aria-label="Effet précédent">◀</button><button class="chip" id="pNext" aria-label="Effet suivant">▶</button></h2>
    <div class="grid" id="presetGrid"></div>
  </section>

  <section class="view" id="v-music">
    <div class="card">
      <div class="np"><div class="cover" id="cover">🎵</div><div style="min-width:0"><div class="t" id="mTitle">Aucune musique</div><div class="a" id="mArtist"></div></div></div>
      <div class="bar"><div id="mBar"></div></div>
      <div class="times"><span id="mPos">0:00</span><span id="mDur">0:00</span></div>
      <div class="transport">
        <button id="mPrev" aria-label="Morceau précédent">⏮</button>
        <button class="big" id="mPlay" aria-label="Lecture ou pause">▶</button>
        <button id="mNext" aria-label="Morceau suivant">⏭</button>
      </div>
    </div>
    <div class="card">
      <button class="chip" id="mToMic" style="margin-bottom:6px">Musique dans le micro</button>
      <div class="slider"><label>Musique <span id="s-music-v"></span></label><input type="range" id="s-music" min="0" max="150"></div>
      <div class="slider"><label>Sons de la soundboard <span id="s-pads-v"></span></label><input type="range" id="s-pads" min="0" max="150"></div>
      <div class="slider"><label>Envoi dans le micro <span id="s-tomic-v"></span></label><input type="range" id="s-tomic" min="0" max="150"></div>
      <div class="slider"><label>Casque <span id="s-monitor-v"></span></label><input type="range" id="s-monitor" min="0" max="150"></div>
    </div>
  </section>

  <section class="view" id="v-bt">
    <div id="btBox"></div>
  </section>
</main>

<nav class="bottom" id="nav">
  <button data-v="deck" class="sel"><span class="i">⭐</span>Mon deck</button>
  <button data-v="pads"><span class="i">🔊</span>Sons</button>
  <button data-v="voice"><span class="i">🎙️</span>Voix</button>
  <button data-v="music"><span class="i">🎵</span>Musique</button>
  <button data-v="bt" class="hidden" id="navBt"><span class="i">🏆</span>Blind test</button>
</nav>

<div class="sheet-bg" id="sheetBg"></div>
<div class="sheet" id="sheet"></div>
<div class="toast" id="toast"></div>

<script>
"use strict";
const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
let code = params.get('code') || '';
try { if (code) localStorage.setItem('spoka-code', code); else code = localStorage.getItem('spoka-code') || ''; } catch (e) {}
const prefs = (() => { try { return JSON.parse(localStorage.getItem('spoka-prefs') || '{}'); } catch (e) { return {}; } })();
prefs.size ??= 'm'; prefs.vibrate ??= true; prefs.showAnswer ??= true;
function savePrefs() { try { localStorage.setItem('spoka-prefs', JSON.stringify(prefs)); } catch (e) {} }

let catalog = null, state = null, version = '', view = 'deck', padTab = '', editing = false, deckDraft = null;

const ACTIONS = {
  'stop': ['⏹', 'Tout arrêter'], 'mic': ['🎙', 'Micro'], 'effects': ['✨', 'Effets de voix'], 'radio': ['📻', 'Mode radio'],
  'ptt': ['🗣', 'Parler (maintenir)'], 'preset-next': ['⏭', 'Effet suivant'], 'preset-previous': ['⏮', 'Effet précédent'],
  'music-playpause': ['⏯', 'Musique'], 'music-next': ['⏭', 'Morceau suivant'], 'music-previous': ['⏮', 'Morceau précédent'],
  'media-to-mic': ['📢', 'Musique dans le micro'], 'bt-reveal': ['👁', 'Révéler (blind test)'], 'bt-next': ['➡', 'Manche suivante'],
  'bt-replay': ['🔁', 'Rejouer l’extrait'], 'bt-pause': ['⏸', 'Pause blind test'], 'bt-hint': ['💡', 'Indice'], 'bt-skip': ['⏩', 'Passer le morceau'],
};

// ---- Network ---------------------------------------------------------------------------------------------
function url(path, extra) {
  const q = new URLSearchParams(extra || {}); q.set('code', code);
  return path + '?' + q.toString();
}
async function call(path, extra, quiet) {
  try {
    const r = await fetch(url(path, extra), { method: 'POST', cache: 'no-store' });
    const data = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(data.error || ('erreur ' + r.status));
    return data;
  } catch (e) { if (!quiet) toast(e.message === 'Failed to fetch' ? 'PC injoignable : SpokaSoundBoard est-il ouvert ?' : e.message); throw e; }
}
async function loadCatalog() {
  const r = await fetch(url('/api/catalog'), { cache: 'no-store' });
  if (r.status === 401) { fatal('Code d’accès refusé. Scannez à nouveau le QR code affiché dans SpokaSoundBoard (Configuration → Télécommande).'); return; }
  catalog = await r.json();
  document.documentElement.style.setProperty('--accent', catalog.accent);
  document.documentElement.style.setProperty('--on-accent', catalog.onAccent);
  renderAll();
}
let es = null, pollTimer = null, failures = 0;
function connect() {
  if (!window.EventSource) { startPolling(); return; }
  es = new EventSource(url('/api/events'));
  es.onmessage = e => { failures = 0; setConnected(true); onState(JSON.parse(e.data)); };
  es.onerror = () => {
    setConnected(false); es.close(); es = null;
    if (++failures >= 3) startPolling(); else setTimeout(connect, 1000 * failures);
  };
}
function startPolling() {
  if (pollTimer) return;
  const tick = async () => {
    try { const r = await fetch(url('/api/state'), { cache: 'no-store' }); if (r.ok) { setConnected(true); onState(await r.json()); } else setConnected(false); }
    catch (e) { setConnected(false); }
  };
  tick(); pollTimer = setInterval(tick, 1000);
  setTimeout(() => { clearInterval(pollTimer); pollTimer = null; failures = 0; connect(); }, 30000);
}
function setConnected(on) { $('dot').classList.toggle('on', on); }
document.addEventListener('visibilitychange', () => { if (!document.hidden && !es && !pollTimer) connect(); });

function onState(s) {
  state = s;
  if (s.version !== version) { version = s.version; loadCatalog(); return; }
  updateLive();
}

// ---- Helpers -----------------------------------------------------------------------------------------------
let toastTimer;
function toast(text) { const t = $('toast'); t.textContent = text; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 2600); }
function buzz(ms) { if (prefs.vibrate && navigator.vibrate) navigator.vibrate(ms || 12); }
function fatal(text) { document.querySelector('main').innerHTML = '<div class="empty">' + text + '</div>'; }
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function fmt(s) { s = Math.max(0, Math.floor(s || 0)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
function applySize() { const px = { s: 92, m: 112, l: 150 }[prefs.size] || 112; document.documentElement.style.setProperty('--tile', px + 'px'); }

// Long press (pin to the deck) without blocking the normal tap.
function onPress(btn, tap, long) {
  let timer = null, longDone = false;
  btn.addEventListener('pointerdown', () => { longDone = false; if (long) timer = setTimeout(() => { longDone = true; buzz(30); long(); }, 550); });
  const cancel = () => clearTimeout(timer);
  btn.addEventListener('pointerup', cancel); btn.addEventListener('pointerleave', cancel); btn.addEventListener('pointercancel', cancel);
  btn.addEventListener('contextmenu', e => e.preventDefault());
  btn.addEventListener('click', e => { if (longDone) { e.preventDefault(); return; } buzz(); tap(); });
}

// ---- Deck items ------------------------------------------------------------------------------------------
function describe(key) {
  const i = key.indexOf(':'); const kind = key.slice(0, i), value = key.slice(i + 1);
  if (kind === 'pad') { const p = catalog.pads.find(x => x.id === value); return p && { kind, value, label: p.name, color: p.color, sub: p.hotkey || p.category || 'Son' }; }
  if (kind === 'preset') { const p = catalog.presets.find(x => x.name === value); return p && { kind, value, label: p.name, ico: '🎭', sub: 'Effet de voix' }; }
  if (kind === 'say') return { kind, value, label: value, ico: '💬', sub: 'Dire dans le micro' };
  if (kind === 'act') { const a = ACTIONS[value]; return a && { kind, value, label: a[1], ico: a[0], sub: '' }; }
  return null;
}
function tileFor(key, opts) {
  const d = describe(key); if (!d) return null;
  const b = el('button', 'tile'); b.dataset.key = key;
  if (d.color) { b.style.background = d.color + '33'; b.style.borderColor = d.color; }
  if (d.ico) b.appendChild(el('span', 'ico', d.ico));
  b.appendChild(el('span', 'lbl', d.label));
  const sub = el('span', 'sub', d.sub); b.appendChild(sub);
  b.setAttribute('aria-label', d.label);
  if (key === 'act:ptt' && !(opts && opts.editable)) bindHold(b);
  else if (key === 'act:ptt') onPress(b, () => {});
  else onPress(b, () => { if (!(opts && opts.editable)) runKey(key); }, opts && opts.long ? () => pinSheet(key) : null);
  if (opts && opts.editable) {
    const ed = el('div', 'edit');
    const l = el('span', null, '◀'), r = el('span', null, '▶'), x = el('span', 'x', '✕');
    l.onclick = e => { e.stopPropagation(); moveDeck(key, -1); }; r.onclick = e => { e.stopPropagation(); moveDeck(key, 1); };
    x.onclick = e => { e.stopPropagation(); deckDraft = deckDraft.filter(k => k !== key); renderDeck(); };
    ed.append(l, r, x); b.appendChild(ed);
  }
  return b;
}
function runKey(key) {
  const i = key.indexOf(':'); const kind = key.slice(0, i), value = key.slice(i + 1);
  if (kind === 'pad') return call('/api/pads/' + encodeURIComponent(value) + '/play').catch(() => {});
  if (kind === 'preset') return call('/api/presets/' + encodeURIComponent(value) + '/apply').then(() => toast('Effet « ' + value + ' »')).catch(() => {});
  if (kind === 'say') return say(value);
  if (kind === 'act') return call('/api/action/' + value).catch(() => {});
}

// Push-to-talk: « down » repeated while held, so the PC lets go by itself if the phone loses the connection.
function bindHold(btn) {
  let timer = null;
  const start = e => {
    e.preventDefault(); if (timer) return;
    if (state && !state.radioMode) { toast('Activez d’abord le mode radio (onglet Voix).'); return; }
    try { btn.setPointerCapture(e.pointerId); } catch (x) {}
    buzz(20); btn.classList.add('held');
    const send = () => fetch(url('/api/ptt', { state: 'down' }), { method: 'POST', cache: 'no-store' }).catch(() => {});
    send(); timer = setInterval(send, 300);
  };
  const stop = () => {
    if (!timer) return; clearInterval(timer); timer = null; btn.classList.remove('held'); buzz(8);
    fetch(url('/api/ptt', { state: 'up' }), { method: 'POST', cache: 'no-store' }).catch(() => {});
  };
  btn.addEventListener('pointerdown', start);
  ['pointerup', 'pointercancel', 'lostpointercapture'].forEach(n => btn.addEventListener(n, stop));
  btn.addEventListener('contextmenu', e => e.preventDefault());
  window.addEventListener('blur', stop); document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
}

// ---- Rendering -------------------------------------------------------------------------------------------
function renderAll() {
  if (!catalog) return;
  renderDeck(); renderPads(); renderVoice(); updateLive();
}
function deckItems() { return (editing ? deckDraft : catalog.deck).filter(k => describe(k)); }
function renderDeck() {
  const grid = $('deckGrid'); grid.innerHTML = '';
  const items = deckItems();
  $('deckEmpty').hidden = items.length > 0 || editing;
  grid.classList.toggle('editing', editing);
  for (const k of items) { const t = tileFor(k, { editable: editing }); if (t) grid.appendChild(t); }
  if (editing) {
    const add = el('button', 'tile addtile'); add.append(el('span', 'ico', '＋'), el('span', 'lbl', 'Ajouter'));
    add.onclick = () => pickerSheet(); grid.appendChild(add);
  }
  $('editDeck').textContent = editing ? 'Terminé' : 'Modifier';
  $('editDeck').classList.toggle('primary', editing);
  updateLive();
}
function moveDeck(key, dir) {
  const i = deckDraft.indexOf(key), j = i + dir;
  if (i < 0 || j < 0 || j >= deckDraft.length) return;
  [deckDraft[i], deckDraft[j]] = [deckDraft[j], deckDraft[i]]; renderDeck();
}
async function saveDeck(items) {
  await call('/api/deck', { items: items.join('|') });
  catalog.deck = items.slice();
}
$('editDeck').onclick = async () => {
  if (!editing) { editing = true; deckDraft = catalog.deck.filter(k => describe(k)); renderDeck(); return; }
  try { await saveDeck(deckDraft); editing = false; deckDraft = null; renderDeck(); toast('Deck enregistré sur le PC.'); } catch (e) {}
};
$('autoDeck').onclick = async () => {
  const items = ['act:stop', 'act:mic', 'act:preset-next', 'act:music-playpause'];
  if (state && state.radioMode) items.splice(2, 0, 'act:ptt');
  for (const p of catalog.pads.slice(0, 12)) items.push('pad:' + p.id);
  try { await saveDeck(items); renderDeck(); } catch (e) {}
};
$('addDeck2').onclick = () => { editing = true; deckDraft = []; renderDeck(); pickerSheet(); };

function renderPads() {
  const cats = [...new Set(catalog.pads.map(p => p.category).filter(c => c))];
  const tabs = $('padTabs'); tabs.innerHTML = ''; tabs.style.display = cats.length ? 'flex' : 'none';
  if (padTab && !cats.includes(padTab)) padTab = '';
  for (const c of ['', ...cats]) {
    const b = el('button', c === padTab ? 'sel' : '', c || 'Tous');
    b.onclick = () => { padTab = c; renderPads(); }; tabs.appendChild(b);
  }
  const q = $('padSearch').value.trim().toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  const grid = $('padGrid'); grid.innerHTML = '';
  const list = catalog.pads.filter(p => (!padTab || p.category === padTab) &&
    (!q || p.name.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').includes(q)));
  for (const p of list) grid.appendChild(tileFor('pad:' + p.id, { long: true }));
  $('padNote').textContent = catalog.pads.length === 0 ? 'Aucun son dans la soundboard pour l’instant.' : 'Appui long sur un son pour l’ajouter à Mon deck.';
  updateLive();
}
$('padSearch').addEventListener('input', () => renderPads());

function renderVoice() {
  const grid = $('presetGrid'); grid.innerHTML = '';
  for (const p of catalog.presets) grid.appendChild(tileFor('preset:' + p.name, { long: true }));
  const box = $('phrases'); box.innerHTML = '';
  const seen = new Set();
  for (const t of [...catalog.history, ...catalog.quickPhrases]) {
    if (seen.has(t)) continue; seen.add(t);
    const b = el('button', 'phr', t); onPress(b, () => say(t), () => pinSheet('say:' + t)); box.appendChild(b);
  }
}
async function say(text) {
  if (state && !state.ttsAvailable) { toast('Aucune voix de synthèse sur le PC.'); return; }
  try { await call('/api/say', { text }); toast('« ' + text + ' »'); } catch (e) {}
}
$('sayBtn').onclick = () => { const t = $('sayText').value.trim(); if (t) { buzz(); say(t); $('sayText').value = ''; } };
bindHold($('ptt'));

function setToggle(btn, on, labelOn, labelOff) {
  btn.classList.toggle('on', on); btn.classList.toggle('off', !on); btn.setAttribute('aria-pressed', on ? 'true' : 'false');
  if (labelOn) btn.textContent = on ? labelOn : labelOff;
}
function updateLive() {
  if (!state || !catalog) return;
  const playing = new Set(state.playing);
  document.querySelectorAll('.tile[data-key]').forEach(t => {
    const k = t.dataset.key, sub = t.querySelector('.sub');
    t.classList.remove('playing', 'on', 'off', 'current');
    if (k.startsWith('pad:')) t.classList.toggle('playing', playing.has(k.slice(4)));
    else if (k.startsWith('preset:')) t.classList.toggle('current', k.slice(7) === state.preset);
    else if (k === 'act:mic') { t.classList.add(state.micEnabled ? 'on' : 'off'); sub.textContent = state.micEnabled ? 'Actif' : 'Coupé'; }
    else if (k === 'act:effects') { t.classList.add(state.effectsEnabled ? 'on' : 'off'); sub.textContent = state.effectsEnabled ? state.preset : 'Coupés'; }
    else if (k === 'act:radio') { t.classList.add(state.radioMode ? 'on' : 'off'); sub.textContent = state.radioMode ? 'Activé' : 'Désactivé'; }
    else if (k === 'act:media-to-mic') { t.classList.add(state.mix.mediaToMic ? 'on' : 'off'); sub.textContent = state.mix.mediaToMic ? 'Partagée' : 'Pour vous seul'; }
    else if (k === 'act:music-playpause') sub.textContent = state.music.hasTrack ? (state.music.playing ? '⏸ ' : '▶ ') + (state.music.title || '') : 'Aucune musique';
    else if (k === 'act:ptt') sub.textContent = state.radioMode ? (state.talking ? 'En cours…' : 'Maintenir') : 'Mode radio désactivé';
    else if (k.startsWith('act:preset-')) sub.textContent = state.preset;
  });
  setToggle($('micChip'), state.micEnabled, '🎙 Actif', '🔇 Coupé');
  setToggle($('vMic'), state.micEnabled, '🎙 Micro actif', '🔇 Micro coupé');
  setToggle($('vFx'), state.effectsEnabled, '✨ Effets : ' + state.preset, 'Effets coupés');
  setToggle($('vRadio'), state.radioMode, '📻 Mode radio', 'Mode radio désactivé');
  $('ptt').disabled = !state.radioMode;
  $('pttNote').textContent = state.radioMode ? (state.talking ? 'Votre voix passe' : 'Votre voix ne passe que pendant l’appui') : 'Activez le mode radio pour l’utiliser';
  $('sayBtn').disabled = !state.ttsAvailable;
  $('sayNote').textContent = state.ttsAvailable ? '' : 'Aucune voix sur le PC (voir Synthèse vocale).';
  updateMusic();
  updateBlindTest();
}
$('micChip').onclick = () => { buzz(); call('/api/action/mic').catch(() => {}); };
$('vMic').onclick = $('micChip').onclick;
$('vFx').onclick = () => { buzz(); call('/api/action/effects').catch(() => {}); };
$('vRadio').onclick = () => { buzz(); call('/api/action/radio').catch(() => {}); };
$('pPrev').onclick = () => { buzz(); call('/api/action/preset-previous').catch(() => {}); };
$('pNext').onclick = () => { buzz(); call('/api/action/preset-next').catch(() => {}); };
$('stopAll').onclick = () => { buzz(25); call('/api/action/stop').catch(() => {}); };

// ---- Music -----------------------------------------------------------------------------------------------
let musicAt = 0, coverKey = '';
const sliders = { music: 'music', pads: 'pads', tomic: 'toMic', monitor: 'monitor' };
const dragging = {};
for (const [key, field] of Object.entries(sliders)) {
  const input = $('s-' + key); let last = 0, pending = null;
  const send = () => { const v = (input.value / 100).toFixed(2); last = Date.now(); call('/api/mix', { key, value: v }, true).catch(() => {}); };
  input.addEventListener('input', () => {
    dragging[key] = true; $('s-' + key + '-v').textContent = input.value + ' %';
    clearTimeout(pending); if (Date.now() - last > 120) send(); else pending = setTimeout(send, 120);
  });
  input.addEventListener('change', () => { send(); setTimeout(() => dragging[key] = false, 800); });
}
function updateMusic() {
  const m = state.music;
  $('mTitle').textContent = m.hasTrack ? (m.title || 'Sans titre') : 'Aucune musique';
  $('mArtist').textContent = m.hasTrack ? (m.artist || '') : 'Lancez une musique sur le PC ou depuis ce téléphone.';
  $('mPlay').textContent = m.playing ? '⏸' : '▶';
  musicAt = Date.now();
  const ck = m.cover || '';
  if (ck !== coverKey) {
    coverKey = ck; const c = $('cover'); c.innerHTML = '';
    if (ck) { const img = el('img'); img.alt = ''; img.src = ck.startsWith('/') ? url(ck, { t: m.title || '' }) : ck; img.onerror = () => { c.textContent = '🎵'; }; c.appendChild(img); }
    else c.textContent = '🎵';
  }
  setToggle($('mToMic'), state.mix.mediaToMic, '📢 Musique dans le micro', 'Musique pour vous seul');
  for (const [key, field] of Object.entries(sliders)) {
    if (dragging[key]) continue;
    const v = Math.round(state.mix[field] * 100); $('s-' + key).value = v; $('s-' + key + '-v').textContent = v + ' %';
  }
  drawProgress();
}
function drawProgress() {
  if (!state) return; const m = state.music;
  const pos = Math.min(m.duration, m.position + (m.playing ? (Date.now() - musicAt) / 1000 : 0));
  $('mBar').style.width = (m.duration > 0 ? pos / m.duration * 100 : 0) + '%';
  $('mPos').textContent = fmt(pos); $('mDur').textContent = fmt(m.duration);
}
setInterval(drawProgress, 500);
$('mPlay').onclick = () => { buzz(); call('/api/action/music-playpause').catch(() => {}); };
$('mNext').onclick = () => { buzz(); call('/api/action/music-next').catch(() => {}); };
$('mPrev').onclick = () => { buzz(); call('/api/action/music-previous').catch(() => {}); };
$('mToMic').onclick = () => { buzz(); call('/api/action/media-to-mic').catch(() => {}); };

// ---- Blind test --------------------------------------------------------------------------------------------
let btSig = '';
function updateBlindTest() {
  const bt = state.blindTest;
  const active = !!bt && bt.phase !== 'setup';
  $('navBt').classList.toggle('hidden', !active);
  if (!active) { if (view === 'bt') show('deck'); btSig = ''; return; }
  const cd = $('btCount'); if (cd) cd.textContent = String(bt.countdown);
  const sig = JSON.stringify({ ...bt, countdown: 0 }) + prefs.showAnswer;
  if (sig === btSig) return; btSig = sig;
  const box = $('btBox'); box.innerHTML = '';
  if (bt.phase === 'podium') {
    const c = el('div', 'card'); c.appendChild(el('h2', null, '🏆 Podium'));
    for (const p of bt.players) { const r = el('div', 'player'); const n = el('div', 'n'); n.append(el('b', null, p.rank + '. ' + p.name)); r.append(n, el('b', null, p.score + ' pts')); c.appendChild(r); }
    box.appendChild(c); return;
  }
  if (bt.suspended) {
    const c = el('div', 'card'); c.append(el('p', null, 'La partie est en pause : la page Blind test a été quittée sur le PC.'));
    const b = el('button', 'primary', '▶ Reprendre la partie'); b.onclick = () => call('/api/action/bt-resume').catch(() => {}); c.appendChild(b); box.appendChild(c); return;
  }
  const stateLabel = { loading: 'Chargement de l’extrait…', listening: bt.paused ? 'En pause' : 'À l’écoute', timeup: 'Temps écoulé !', revealed: 'Réponse révélée' }[bt.state] || '';
  const head = el('div', 'card');
  const top = el('div', 'row'); top.style.justifyContent = 'space-between';
  top.append(el('b', null, 'Manche ' + bt.round + ' / ' + bt.total), el('span', 'note', stateLabel));
  head.append(top);
  if (bt.state !== 'revealed') { const cd = el('div', 'bt-count', String(bt.countdown)); cd.id = 'btCount'; cd.style.margin = '8px 0'; head.appendChild(cd); }
  const ans = el('div', prefs.showAnswer ? '' : 'hidden-answer');
  const lab = el('div', 'row'); lab.style.justifyContent = 'space-between';
  lab.append(el('span', 'note', 'Réponse (visible seulement ici)'));
  const eye = el('button', 'chip', prefs.showAnswer ? '🙈 Cacher' : '👁 Voir'); eye.onclick = () => { prefs.showAnswer = !prefs.showAnswer; savePrefs(); btSig = ''; updateBlindTest(); };
  lab.appendChild(eye); ans.appendChild(lab);
  const a = el('div', 'answer', bt.answerTitle || '…'); a.appendChild(el('small', null, bt.answerArtist || '')); ans.appendChild(a);
  if (bt.hint) ans.appendChild(el('div', 'hint', bt.hint));
  head.appendChild(ans);
  const acts = el('div', 'grid'); acts.style.marginTop = '12px';
  const add = (ico, label, action, primary) => { const b = el('button', 'tile' + (primary ? ' primary' : '')); b.append(el('span', 'ico', ico), el('span', 'lbl', label)); onPress(b, () => call('/api/action/' + action).catch(() => {})); acts.appendChild(b); };
  if (bt.state !== 'revealed') add('👁', 'Révéler', 'bt-reveal', true);
  else add('➡', bt.round >= bt.total ? 'Voir le podium' : 'Manche suivante', 'bt-next', true);
  if (bt.state === 'listening') add(bt.paused ? '▶' : '⏸', bt.paused ? 'Reprendre' : 'Pause', 'bt-pause');
  add('🔁', 'Rejouer', 'bt-replay');
  if (bt.canHint && bt.state !== 'revealed') add('💡', 'Indice', 'bt-hint');
  if (bt.state !== 'revealed') add('⏩', 'Passer', 'bt-skip');
  head.appendChild(acts); box.appendChild(head);
  const pl = el('div', 'card'); pl.appendChild(el('h2', null, 'Qui a trouvé ?'));
  for (const p of bt.players) {
    const r = el('div', 'player'); const n = el('div', 'n');
    n.append(el('b', null, p.rank + '. ' + p.name), el('span', null, p.score + ' pts' + (p.fast ? ' · ⚡ rapide' : '')));
    const t = el('button', p.title ? 'on' : '', 'Titre +2'), ar = el('button', p.artist ? 'on' : '', 'Artiste +1');
    t.onclick = () => { buzz(); call('/api/blindtest/player', { name: p.name, field: 'title' }).catch(() => {}); };
    ar.onclick = () => { buzz(); call('/api/blindtest/player', { name: p.name, field: 'artist' }).catch(() => {}); };
    r.append(n, t, ar); pl.appendChild(r);
  }
  box.appendChild(pl);
}

// ---- Sheets ----------------------------------------------------------------------------------------------
function openSheet(build) {
  const s = $('sheet'); s.innerHTML = ''; build(s);
  $('sheet').classList.add('open'); $('sheetBg').classList.add('open');
}
function closeSheet() { $('sheet').classList.remove('open'); $('sheetBg').classList.remove('open'); }
$('sheetBg').onclick = closeSheet;

function pinSheet(key) {
  const d = describe(key); if (!d) return;
  const inDeck = catalog.deck.includes(key);
  openSheet(s => {
    s.appendChild(el('h2', null, d.label));
    const b = el('button', 'pick', inDeck ? '✕ Retirer de Mon deck' : '⭐ Ajouter à Mon deck'); b.style.justifyContent = 'center';
    b.onclick = async () => {
      const items = inDeck ? catalog.deck.filter(k => k !== key) : [...catalog.deck, key];
      try { await saveDeck(items); renderDeck(); toast(inDeck ? 'Retiré de Mon deck' : 'Ajouté à Mon deck'); } catch (e) {}
      closeSheet();
    };
    const c = el('button', 'pick', 'Annuler'); c.style.justifyContent = 'center'; c.onclick = closeSheet;
    s.append(b, c);
  });
}

function pickerSheet() {
  openSheet(s => {
    const h = el('h2'); h.append(el('span', 'grow', 'Ajouter au deck'));
    const done = el('button', 'chip primary', 'OK'); done.onclick = closeSheet; h.appendChild(done); s.appendChild(h);
    const search = el('input', 'search'); search.type = 'search'; search.placeholder = 'Rechercher…'; s.appendChild(search);
    const list = el('div'); s.appendChild(list);
    const groups = [
      ['Actions', ['act:ptt', ...Object.keys(ACTIONS).map(a => 'act:' + a)]],
      ...groupPads(),
      ['Effets de voix', catalog.presets.map(p => 'preset:' + p.name)],
      ['Phrases', [...new Set([...catalog.history, ...catalog.quickPhrases])].map(t => 'say:' + t)],
    ];
    const draw = () => {
      list.innerHTML = ''; const q = search.value.trim().toLowerCase();
      for (const [title, keys] of groups) {
        const items = keys.filter((k, i, all) => all.indexOf(k) === i).map(k => [k, describe(k)]).filter(([k, d]) => d && (!q || d.label.toLowerCase().includes(q)));
        if (!items.length) continue;
        list.appendChild(el('h3', null, title));
        for (const [k, d] of items) {
          const b = el('button', 'pick' + (deckDraft.includes(k) ? ' in' : ''));
          const sw = el('span', 'c'); sw.style.background = d.color || 'var(--accent)'; b.append(sw, el('span', 'l', (d.ico ? d.ico + ' ' : '') + d.label));
          b.onclick = () => { buzz(); if (deckDraft.includes(k)) deckDraft = deckDraft.filter(x => x !== k); else deckDraft.push(k); b.classList.toggle('in'); renderDeck(); };
          list.appendChild(b);
        }
      }
    };
    search.addEventListener('input', draw); draw();
  });
}
function groupPads() {
  const cats = [...new Set(catalog.pads.map(p => p.category))];
  return cats.map(c => ['Sons' + (c ? ' · ' + c : ''), catalog.pads.filter(p => p.category === c).map(p => 'pad:' + p.id)]);
}

$('gear').onclick = () => openSheet(s => {
  s.appendChild(el('h2', null, 'Réglages de la télécommande'));
  s.appendChild(el('h3', null, 'Taille des boutons'));
  const seg = el('div', 'seg');
  for (const [k, l] of [['s', 'Petits'], ['m', 'Moyens'], ['l', 'Grands']]) {
    const b = el('button', prefs.size === k ? 'sel' : '', l);
    b.onclick = () => { prefs.size = k; savePrefs(); applySize(); seg.querySelectorAll('button').forEach(x => x.classList.remove('sel')); b.classList.add('sel'); };
    seg.appendChild(b);
  }
  s.appendChild(seg);
  s.appendChild(el('h3', null, 'Retour au toucher'));
  const vib = el('button', 'pick', prefs.vibrate ? '📳 Vibration activée' : 'Vibration désactivée');
  vib.onclick = () => { prefs.vibrate = !prefs.vibrate; savePrefs(); vib.textContent = prefs.vibrate ? '📳 Vibration activée' : 'Vibration désactivée'; buzz(30); };
  s.appendChild(vib);
  if (!navigator.vibrate) s.appendChild(el('p', 'note', 'Ce téléphone ne permet pas la vibration depuis une page web (iPhone).'));
  s.appendChild(el('h3', null, 'Comme une application'));
  s.appendChild(el('p', 'note', 'iPhone : bouton Partager → « Sur l’écran d’accueil ». Android : menu ⋮ → « Ajouter à l’écran d’accueil ». La télécommande s’ouvre alors en plein écran, d’un seul geste.'));
  s.appendChild(el('p', 'note', 'Astuce : pendant vos parties, réglez la mise en veille de l’écran du téléphone sur une durée longue.'));
  const close = el('button', 'pick', 'Fermer'); close.style.justifyContent = 'center'; close.onclick = closeSheet; s.appendChild(close);
});

// ---- Navigation --------------------------------------------------------------------------------------------
function show(v) {
  view = v;
  document.querySelectorAll('.view').forEach(x => x.classList.toggle('active', x.id === 'v-' + v));
  document.querySelectorAll('nav.bottom button').forEach(b => b.classList.toggle('sel', b.dataset.v === v));
  if (editing && v !== 'deck') { editing = false; deckDraft = null; renderDeck(); }
  window.scrollTo(0, 0);
}
document.querySelectorAll('nav.bottom button').forEach(b => b.onclick = () => { buzz(8); show(b.dataset.v); });

applySize();
if (!code) fatal('Code d’accès manquant : scannez le QR code affiché dans SpokaSoundBoard (Configuration → Télécommande).');
else { loadCatalog().catch(() => toast('PC injoignable : SpokaSoundBoard est-il ouvert ?')); connect(); }
</script>
</body>
</html>
"""";
}
