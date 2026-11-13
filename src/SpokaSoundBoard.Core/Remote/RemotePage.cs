namespace SpokaSoundBoard.Core.Remote;

/// <summary>
/// The phone remote: one self-contained page (icons embedded, works on the local network without Internet).
/// Tabs: « Mon deck » (buttons chosen by the user, saved on the PC), Sons, Voix, Musique and Blind test (during a game).
/// Three themes chosen on the phone: Nuit (gradient keys under glass), Coucher de soleil and Océan (glass on a gradient).
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
  "background_color": "#14121c",
  "theme_color": "#14121c",
  "icons": [ { "src": "icon.png", "sizes": "any", "type": "image/png", "purpose": "any" } ]
}
""";

    /// <summary>Theme keys offered on the phone (the first one is the default).</summary>
    public static readonly IReadOnlyList<string> Themes = ["nuit", "soleil", "ocean"];

    public const string Html = Head + RemoteIcons.Sprite + Body;

    private const string Head = """"
<!doctype html>
<html lang="fr" data-theme="nuit">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#14121c" id="themeColor">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Spoka">
<link rel="manifest" href="manifest.webmanifest">
<link rel="apple-touch-icon" href="icon.png">
<link rel="icon" href="icon.png">
<title>Télécommande SpokaSoundBoard</title>
<style>
:root{--tile:112px;--safe-b:env(safe-area-inset-bottom,0px);--safe-t:env(safe-area-inset-top,0px);--danger1:#ff4d6d;--danger2:#c9184a;--ok:#34d399}
[data-theme=nuit]{--page:#14121c;--solid:#1b1826;--fg:#fff;--sub:#9a94b5;--glass:rgba(255,255,255,.06);--glass-b:rgba(255,255,255,.11);--nav:rgba(22,20,31,.92);--a1:#c084fc;--a2:#7c3aed;--on-a:#fff}
[data-theme=soleil]{--page:linear-gradient(170deg,#4c1d95 0%,#be185d 52%,#f97316 100%);--solid:#3a1238;--fg:#fff;--sub:#fde2e4;--glass:rgba(255,255,255,.16);--glass-b:rgba(255,255,255,.32);--nav:rgba(44,10,38,.45);--a1:#fff;--a2:#fde2e4;--on-a:#7a1240}
[data-theme=ocean]{--page:radial-gradient(circle at 85% 0%,rgba(34,211,238,.45),transparent 45%),radial-gradient(circle at 0% 75%,rgba(37,99,235,.5),transparent 48%),#06132b;--solid:#0b1d3d;--fg:#e0f7ff;--sub:#7dd3fc;--glass:rgba(255,255,255,.07);--glass-b:rgba(125,211,252,.25);--nav:rgba(6,19,43,.88);--a1:#22d3ee;--a2:#2563eb;--on-a:#03203a}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html{background:#0b0a12}
body{margin:0;color:var(--fg);font:15px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;overscroll-behavior:none;background:transparent}
body::before{content:"";position:fixed;inset:0;z-index:-1;background:var(--page)}
button,input,textarea{font:inherit;color:inherit}
button{background:var(--glass);border:1px solid var(--glass-b);border-radius:14px;padding:10px 12px;cursor:pointer;touch-action:manipulation;
  -webkit-user-select:none;user-select:none;-webkit-touch-callout:none;display:inline-flex;align-items:center;gap:6px}
button:active{transform:scale(.96)}
button:disabled{opacity:.45}
.ic{width:1.25em;height:1.25em;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round;flex:none}
.topbar{position:sticky;top:0;z-index:5;display:flex;align-items:center;gap:8px;padding:calc(10px + var(--safe-t)) 14px 8px;
  background:linear-gradient(var(--nav),transparent);backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px)}
.ttl{flex:1;min-width:0}.ttl b{display:block;font-size:19px;font-weight:700;letter-spacing:-.01em}
.ttl small{display:flex;align-items:center;gap:6px;color:var(--sub);font-size:12px;white-space:nowrap;overflow:hidden}
.dot{width:7px;height:7px;border-radius:50%;background:#f59e0b;flex:none}.dot.on{background:var(--ok)}
.iconbtn{width:40px;height:40px;justify-content:center;padding:0;border-radius:50%}
.stopbtn{background:linear-gradient(135deg,var(--danger1),var(--danger2));border:none;color:#fff;font-weight:700;border-radius:999px;padding:9px 14px}
main{padding:6px 14px calc(150px + var(--safe-b))}
.view{display:none}.view.active{display:block;animation:fade .18s ease-out}
@keyframes fade{from{opacity:0;transform:translateY(4px)}}
#v-deck h2{margin:0 0 8px}
h2{font-size:16px;margin:16px 0 10px;display:flex;align-items:center;gap:8px;font-weight:700}h2 .grow{flex:1}
h3{font-size:12px;color:var(--sub);font-weight:600;margin:18px 0 8px;text-transform:uppercase;letter-spacing:.06em}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(var(--tile),1fr));gap:10px}
.tile{position:relative;min-height:calc(var(--tile) * .8);flex-direction:column;align-items:stretch;justify-content:space-between;text-align:left;
  padding:11px;border-radius:20px;overflow:hidden;gap:8px;color:#fff}
.tile .ico{display:flex;align-items:center;justify-content:center;width:34px;height:34px;border-radius:11px;font-size:20px;position:relative}
.tile .lbl{font-weight:600;font-size:14px;line-height:1.2;overflow-wrap:break-word;-webkit-hyphens:auto;hyphens:auto;position:relative;display:block}
.tile .sub{font-size:11px;opacity:.85;margin-top:2px;display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;position:relative}
[data-theme=nuit] .tile{background:linear-gradient(135deg,var(--c1),var(--c2));border:none}
[data-theme=nuit] .tile::before{content:"";position:absolute;left:0;right:0;top:0;height:62%;background:linear-gradient(rgba(255,255,255,.28),rgba(255,255,255,0));pointer-events:none}
[data-theme=nuit] .tile .ico{background:rgba(255,255,255,.18)}
[data-theme=soleil] .tile{background:var(--glass);border:1px solid var(--glass-b);backdrop-filter:blur(10px);-webkit-backdrop-filter:blur(10px)}
[data-theme=soleil] .tile .ico{width:auto;height:auto;font-size:24px;justify-content:flex-start}
[data-theme=ocean] .tile{background:var(--glass);border:1px solid var(--glass-b);color:var(--fg)}
[data-theme=ocean] .tile .ico{border-radius:50%;background:linear-gradient(135deg,var(--c1),var(--c2));color:#fff;font-size:18px}
.tile.playing{box-shadow:0 0 0 3px #fff inset}
.tile.playing::after{content:"";position:absolute;left:0;right:0;bottom:0;height:4px;background:#fff;animation:pulse 1s ease-in-out infinite}
@keyframes pulse{50%{opacity:.25}}
.tile.current{box-shadow:0 0 0 3px var(--a1) inset}
.tile.held{filter:brightness(1.25);transform:scale(.97)}
.tile .edit{position:absolute;inset:0;display:none;align-items:flex-end;justify-content:space-between;padding:6px;background:rgba(0,0,0,.1)}
.editing .tile .edit{display:flex}
.edit span{background:rgba(15,15,25,.82);border:1px solid rgba(255,255,255,.2);border-radius:10px;width:32px;height:30px;display:flex;align-items:center;justify-content:center;color:#fff}
.edit .x{background:var(--danger1);border:none;position:absolute;top:6px;right:6px;width:26px;height:26px;border-radius:50%}
.editing .tile .sub{visibility:hidden}
.editing .tile{animation:wiggle .35s ease-in-out infinite alternate}
@keyframes wiggle{from{transform:rotate(-.6deg)}to{transform:rotate(.6deg)}}
@media (prefers-reduced-motion:reduce){.editing .tile,.tile.playing::after,.view.active{animation:none}}
.addtile{border:2px dashed var(--glass-b)!important;background:transparent!important;align-items:center;justify-content:center;text-align:center;color:var(--sub)!important}
.addtile::before{display:none}.addtile .ic{width:26px;height:26px}
.tabs{display:flex;gap:6px;overflow-x:auto;padding-bottom:6px;margin-bottom:6px;scrollbar-width:none}.tabs::-webkit-scrollbar{display:none}
.tabs button{border-radius:999px;padding:7px 14px;font-size:13px;white-space:nowrap;flex:none}
.sel{background:linear-gradient(135deg,var(--a1),var(--a2))!important;color:var(--on-a)!important;border-color:transparent!important}
.search,textarea{width:100%;background:var(--glass);border:1px solid var(--glass-b);border-radius:14px;padding:12px;outline:none;color:var(--fg)}
.search{margin-bottom:10px}.search::placeholder,textarea::placeholder{color:var(--sub)}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.card{background:var(--glass);border:1px solid var(--glass-b);border-radius:22px;padding:14px;margin-bottom:12px;backdrop-filter:blur(10px);-webkit-backdrop-filter:blur(10px)}
.chip{border-radius:999px;padding:8px 13px;font-size:13px;white-space:nowrap}
.chip.on{border-color:transparent;background:rgba(52,211,153,.22);color:#a7f3d0}
.chip.off{border-color:transparent;background:rgba(255,77,109,.22);color:#ffc2cd}
[data-theme=soleil] .chip.on,[data-theme=soleil] .chip.off{color:#fff}
.primary{background:linear-gradient(135deg,var(--a1),var(--a2));border:none;color:var(--on-a);font-weight:700;justify-content:center}
.ptt{width:100%;min-height:150px;border-radius:28px;font-size:20px;font-weight:700;flex-direction:column;justify-content:center;gap:6px;border:none;color:#fff;
  background:linear-gradient(135deg,#22d3ee,#0891b2)}
.ptt .ic{width:44px;height:44px}
.ptt .note{color:rgba(255,255,255,.85)}
.ptt.held{background:linear-gradient(135deg,#34d399,#059669);transform:scale(.98)}
.ptt:disabled{background:var(--glass);border:1px solid var(--glass-b);opacity:1;color:var(--sub)}
.ptt:disabled .note{color:var(--sub)}
textarea{min-height:84px;resize:vertical}
.phr{font-size:13px;border-radius:999px;padding:8px 12px}
.np{display:flex;gap:14px;align-items:center}
.cover{width:96px;height:96px;border-radius:18px;background:linear-gradient(135deg,var(--a1),var(--a2));display:flex;align-items:center;justify-content:center;font-size:40px;color:var(--on-a);overflow:hidden;flex:none}
.cover img{width:100%;height:100%;object-fit:cover}
.np .t{font-weight:700;font-size:18px;overflow-wrap:anywhere}.np .a{color:var(--sub);margin-top:2px}
.bar{height:5px;background:rgba(255,255,255,.15);border-radius:5px;overflow:hidden;margin:14px 0 4px}.bar div{height:100%;background:linear-gradient(90deg,var(--a1),var(--a2));width:0}
.times{display:flex;justify-content:space-between;font-size:12px;color:var(--sub)}
.transport{display:flex;justify-content:center;align-items:center;gap:18px;margin:12px 0 4px}
.transport button{width:58px;height:58px;border-radius:50%;justify-content:center;padding:0;font-size:22px}
.transport .big{width:76px;height:76px;font-size:30px;background:linear-gradient(135deg,var(--a1),var(--a2));border:none;color:var(--on-a)}
.slider{margin:14px 0}.slider label{display:flex;justify-content:space-between;font-size:14px;margin-bottom:6px}.slider label span{color:var(--sub)}
input[type=range]{width:100%;accent-color:var(--a1);height:30px}
.bt-count{font-size:60px;font-weight:800;line-height:1}
.answer{font-size:22px;font-weight:800}.answer small{display:block;font-size:15px;color:var(--a1);font-weight:600;margin-top:2px}
.hint{font-family:ui-monospace,Menlo,Consolas,monospace;color:var(--a1);margin-top:8px;white-space:pre-wrap}
.hidden-answer .answer{filter:blur(10px)}
.player{display:flex;align-items:center;gap:8px;padding:10px 0;border-top:1px solid var(--glass-b)}
.player .n{flex:1;min-width:0}.player .n b{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.player .n span{font-size:12px;color:var(--sub)}
.player button{padding:9px 10px;font-size:13px}
.player button.on{background:rgba(52,211,153,.3);border-color:transparent}
.mini{position:fixed;left:10px;right:10px;bottom:calc(68px + var(--safe-b));z-index:6;display:none;align-items:center;gap:10px;padding:7px 8px 7px 7px;
  border-radius:18px;background:var(--nav);border:1px solid var(--glass-b);backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px)}
.mini.show{display:flex}
.mini .mc{width:38px;height:38px;border-radius:11px;background:linear-gradient(135deg,var(--a1),var(--a2));overflow:hidden;flex:none;display:flex;align-items:center;justify-content:center;color:var(--on-a)}
.mini .mc img{width:100%;height:100%;object-fit:cover}
.mini .mt{flex:1;min-width:0;font-size:13px;cursor:pointer}.mini .mt b,.mini .mt span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.mini .mt span{color:var(--sub);font-size:12px}
.mini button{width:38px;height:38px;border-radius:50%;justify-content:center;padding:0;background:transparent;border:none;font-size:20px}
nav.bottom{position:fixed;left:0;right:0;bottom:0;z-index:6;display:flex;background:var(--nav);backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px);
  border-top:1px solid var(--glass-b);padding:6px 6px calc(6px + var(--safe-b))}
nav.bottom button{flex:1;background:none;border:none;border-radius:14px;padding:6px 2px;flex-direction:column;gap:2px;font-size:11px;color:var(--sub)}
nav.bottom button .ic{width:22px;height:22px}
nav.bottom button.on{color:var(--fg)}
nav.bottom button.on .ic{color:var(--a1)}
nav.bottom button.hidden{display:none}
.sheet-bg{position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:20;display:none}
.sheet{position:fixed;left:0;right:0;bottom:0;z-index:21;background:var(--solid);border-radius:24px 24px 0 0;max-height:84vh;overflow:auto;
  padding:16px 14px calc(16px + var(--safe-b));transform:translateY(105%);transition:transform .22s ease-out;border-top:1px solid var(--glass-b)}
.sheet.open{transform:none}.sheet-bg.open{display:block}
.sheet h2{position:sticky;top:-16px;background:var(--solid);padding:8px 0;margin-top:-8px;z-index:1}
.pick{width:100%;display:flex;align-items:center;gap:10px;text-align:left;margin-bottom:6px;padding:11px 12px}
.pick .c{width:28px;height:28px;border-radius:9px;flex:none;display:flex;align-items:center;justify-content:center;color:#fff;font-size:16px}
.pick .l{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pick.in{border-color:var(--ok)}.pick.in::after{content:"✓";color:var(--ok);font-weight:700}
.themes{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}
.themes button{flex-direction:column;padding:8px;border-radius:16px;gap:8px;font-size:12px}
.themes i{display:block;width:100%;height:58px;border-radius:11px}
.themes button.cur{border:2px solid var(--a1)}
.toast{position:fixed;left:14px;right:14px;top:calc(70px + var(--safe-t));z-index:30;background:var(--solid);border:1px solid var(--glass-b);border-radius:16px;
  padding:12px 14px;opacity:0;transform:translateY(-8px);transition:all .2s;pointer-events:none}
.toast.show{opacity:1;transform:none}
.empty{color:var(--sub);text-align:center;padding:24px 10px}
.empty .ic{width:46px;height:46px;color:var(--a1);margin-bottom:8px}
.note{color:var(--sub);font-size:13px}
.seg{display:flex;gap:6px}.seg button{flex:1;justify-content:center}
@media (orientation:landscape) and (max-height:500px){
  main{padding:4px 14px calc(110px + var(--safe-b))}.ptt{min-height:96px}.bt-count{font-size:42px}
  .topbar{padding-top:calc(5px + var(--safe-t));padding-bottom:4px}.ttl b{font-size:16px}
  nav.bottom{padding-top:3px;padding-bottom:calc(3px + var(--safe-b))}
  nav.bottom button{flex-direction:row;justify-content:center;gap:6px;font-size:12px;padding:6px 2px}nav.bottom button .ic{width:19px;height:19px}
  .mini{display:none!important}}
</style>
</head>
<body>
"""";

    private const string Body = """"
<div class="topbar">
  <div class="ttl"><b id="ttl">Mon deck</b><small><span class="dot" id="dot"></span><span id="status">Connexion…</span></small></div>
  <button class="iconbtn" id="gear" aria-label="Réglages de la télécommande"><svg class="ic"><use href="#i-settings"/></svg></button>
  <button class="stopbtn" id="stopAll" aria-label="Arrêter tous les sons"><svg class="ic"><use href="#i-player-stop"/></svg>Stop</button>
</div>

<main>
  <section class="view active" id="v-deck">
    <h2><span class="grow"></span><button class="chip" id="editDeck"></button></h2>
    <div class="grid" id="deckGrid"></div>
    <div class="empty" id="deckEmpty" hidden>
      <svg class="ic"><use href="#i-star"/></svg><br>
      Votre deck est vide.<br>Ajoutez-y vos sons, effets de voix, phrases et actions favoris.<br><br>
      <button class="primary" id="autoDeck">Remplir automatiquement</button>
      <button id="addDeck2">Choisir…</button>
    </div>
  </section>

  <section class="view" id="v-pads">
    <input class="search" id="padSearch" type="search" placeholder="Rechercher un son…" aria-label="Rechercher un son">
    <div class="tabs" id="padTabs"></div>
    <div class="grid" id="padGrid"></div>
    <p class="note" id="padNote"></p>
  </section>

  <section class="view" id="v-voice">
    <div class="row" style="margin:6px 0 12px">
      <button class="chip" id="vMic"></button>
      <button class="chip" id="vFx"></button>
      <button class="chip" id="vRadio"></button>
      <button class="chip" id="vNoise"></button>
    </div>
    <button class="ptt" id="ptt"><svg class="ic"><use href="#i-broadcast"/></svg><span>Maintenir pour parler</span><span class="note" id="pttNote"></span></button>
    <div class="card" style="margin-top:12px">
      <h2 style="margin-top:0"><svg class="ic"><use href="#i-message-circle"/></svg>Dire dans le micro</h2>
      <textarea id="sayText" maxlength="300" placeholder="Tapez une phrase : la synthèse vocale la dit dans votre micro."></textarea>
      <div class="row" style="margin-top:8px"><button class="primary" id="sayBtn">Dire</button><span class="note" id="sayNote"></span></div>
      <div class="row" id="phrases" style="margin-top:10px"></div>
    </div>
    <h2><span class="grow">Effets de voix</span>
      <button class="iconbtn" id="pPrev" aria-label="Effet précédent"><svg class="ic"><use href="#i-chevron-left"/></svg></button>
      <button class="iconbtn" id="pNext" aria-label="Effet suivant"><svg class="ic"><use href="#i-chevron-right"/></svg></button></h2>
    <div class="grid" id="presetGrid"></div>
  </section>

  <section class="view" id="v-music">
    <div class="card">
      <div class="np"><div class="cover" id="cover"></div><div style="min-width:0"><div class="t" id="mTitle">Aucune musique</div><div class="a" id="mArtist"></div></div></div>
      <div class="bar"><div id="mBar"></div></div>
      <div class="times"><span id="mPos">0:00</span><span id="mDur">0:00</span></div>
      <div class="transport">
        <button id="mPrev" aria-label="Morceau précédent"><svg class="ic"><use href="#i-player-skip-back"/></svg></button>
        <button class="big" id="mPlay" aria-label="Lecture ou pause"><svg class="ic"><use href="#i-player-play"/></svg></button>
        <button id="mNext" aria-label="Morceau suivant"><svg class="ic"><use href="#i-player-skip-forward"/></svg></button>
      </div>
    </div>
    <div class="card">
      <button class="chip" id="mToMic" style="margin-bottom:4px"></button>
      <div class="slider"><label>Musique <span id="s-music-v"></span></label><input type="range" id="s-music" min="0" max="150"></div>
      <div class="slider"><label>Sons de la soundboard <span id="s-pads-v"></span></label><input type="range" id="s-pads" min="0" max="150"></div>
      <div class="slider"><label>Envoi dans le micro <span id="s-tomic-v"></span></label><input type="range" id="s-tomic" min="0" max="150"></div>
      <div class="slider"><label>Casque <span id="s-monitor-v"></span></label><input type="range" id="s-monitor" min="0" max="150"></div>
    </div>
  </section>

  <section class="view" id="v-bt"><div id="btBox"></div></section>
</main>

<div class="mini" id="mini">
  <div class="mc" id="miniCover"></div>
  <div class="mt" id="miniText"><b id="miniTitle"></b><span id="miniArtist"></span></div>
  <button id="miniPlay" aria-label="Lecture ou pause"><svg class="ic"><use href="#i-player-play"/></svg></button>
  <button id="miniNext" aria-label="Morceau suivant"><svg class="ic"><use href="#i-player-skip-forward"/></svg></button>
</div>

<nav class="bottom" id="nav">
  <button data-v="deck" class="on"><svg class="ic"><use href="#i-star"/></svg>Mon deck</button>
  <button data-v="pads"><svg class="ic"><use href="#i-volume"/></svg>Sons</button>
  <button data-v="voice"><svg class="ic"><use href="#i-microphone"/></svg>Voix</button>
  <button data-v="music"><svg class="ic"><use href="#i-music"/></svg>Musique</button>
  <button data-v="bt" class="hidden" id="navBt"><svg class="ic"><use href="#i-trophy"/></svg>Blind test</button>
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
prefs.size ??= 'm'; prefs.vibrate ??= true; prefs.showAnswer ??= true; prefs.theme ??= 'nuit';
function savePrefs() { try { localStorage.setItem('spoka-prefs', JSON.stringify(prefs)); } catch (e) {} }

const THEMES = [
  ['nuit', 'Nuit', 'linear-gradient(135deg,#c084fc,#7c3aed 45%,#14121c 46%)', '#14121c'],
  ['soleil', 'Coucher de soleil', 'linear-gradient(170deg,#4c1d95,#be185d 55%,#f97316)', '#4c1d95'],
  ['ocean', 'Océan', 'radial-gradient(circle at 85% 0%,#22d3ee,transparent 60%),radial-gradient(circle at 0% 90%,#2563eb,transparent 60%),#06132b', '#06132b'],
];
function applyTheme() {
  const t = THEMES.find(x => x[0] === prefs.theme) || THEMES[0];
  document.documentElement.dataset.theme = t[0];
  $('themeColor').setAttribute('content', t[3]);
}
function applySize() { document.documentElement.style.setProperty('--tile', ({ s: 92, m: 112, l: 150 }[prefs.size] || 112) + 'px'); }

let catalog = null, state = null, version = '', view = 'deck', padTab = '', editing = false, deckDraft = null;

// [icon, label, color 1, color 2] of each action.
const ACTIONS = {
  'stop': ['player-stop', 'Tout arrêter', '#ff4d6d', '#c9184a'], 'mic': ['microphone', 'Micro', '#34d399', '#059669'],
  'effects': ['sparkles', 'Effets de voix', '#c084fc', '#7c3aed'], 'radio': ['radio', 'Mode radio', '#2dd4bf', '#0f766e'],
  'noise': ['wave-sine', 'Suppression du bruit', '#a3e635', '#4d7c0f'],
  'ptt': ['broadcast', 'Parler (maintenir)', '#22d3ee', '#0891b2'], 'preset-next': ['player-skip-forward', 'Effet suivant', '#c084fc', '#7c3aed'],
  'preset-previous': ['player-skip-back', 'Effet précédent', '#c084fc', '#7c3aed'], 'music-playpause': ['music', 'Musique', '#60a5fa', '#2563eb'],
  'music-next': ['player-skip-forward', 'Morceau suivant', '#60a5fa', '#2563eb'], 'music-previous': ['player-skip-back', 'Morceau précédent', '#60a5fa', '#2563eb'],
  'music-volume-up': ['volume', 'Musique plus fort', '#60a5fa', '#2563eb'], 'music-volume-down': ['volume', 'Musique moins fort', '#60a5fa', '#2563eb'],
  'media-to-mic': ['speakerphone', 'Musique dans le micro', '#fbbf24', '#f97316'], 'bt-reveal': ['eye', 'Révéler (blind test)', '#fb923c', '#e11d48'],
  'bt-next': ['chevron-right', 'Manche suivante', '#fb923c', '#e11d48'], 'bt-replay': ['repeat', 'Rejouer l’extrait', '#fb923c', '#e11d48'],
  'bt-pause': ['player-pause', 'Pause blind test', '#fb923c', '#e11d48'], 'bt-hint': ['bulb', 'Indice', '#fbbf24', '#d97706'],
  'bt-skip': ['player-skip-forward', 'Passer le morceau', '#fb923c', '#e11d48'],
};
const GREY = ['#6b7280', '#374151'];

// ---- Network ---------------------------------------------------------------------------------------------
function url(path, extra) { const q = new URLSearchParams(extra || {}); q.set('code', code); return path + '?' + q.toString(); }
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
  renderAll();
}
let es = null, pollTimer = null, failures = 0;
function connect() {
  if (!window.EventSource) { startPolling(); return; }
  es = new EventSource(url('/api/events'));
  es.onmessage = e => { failures = 0; setConnected(true); onState(JSON.parse(e.data)); };
  es.onerror = () => { setConnected(false); es.close(); es = null; if (++failures >= 3) startPolling(); else setTimeout(connect, 1000 * failures); };
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
let connected = false;
function setConnected(on) { connected = on; $('dot').classList.toggle('on', on); if (!on) $('status').textContent = 'Reconnexion…'; }
document.addEventListener('visibilitychange', () => { if (!document.hidden && !es && !pollTimer) connect(); });
function onState(s) { state = s; if (s.version !== version) { version = s.version; loadCatalog(); return; } updateLive(); }

// ---- Helpers -----------------------------------------------------------------------------------------------
let toastTimer;
function toast(text) { const t = $('toast'); t.textContent = text; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 2600); }
function buzz(ms) { if (prefs.vibrate && navigator.vibrate) navigator.vibrate(ms || 12); }
function fatal(text) { const m = document.querySelector('main'); m.innerHTML = ''; m.appendChild(el('div', 'empty', text)); }
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function icon(name) {
  const ns = 'http://www.w3.org/2000/svg', s = document.createElementNS(ns, 'svg'), u = document.createElementNS(ns, 'use');
  s.setAttribute('class', 'ic'); u.setAttribute('href', '#i-' + name); s.appendChild(u); return s;
}
function setIcon(svg, name) { if (svg) svg.firstChild.setAttribute('href', '#i-' + name); }
function labelled(btn, ic, text) { btn.textContent = ''; btn.append(icon(ic), document.createTextNode(text)); }
function fmt(s) { s = Math.max(0, Math.floor(s || 0)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
function shade(hex, f) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || ''); if (!m) return '#374151';
  const n = parseInt(m[1], 16), c = k => Math.round(((n >> k) & 255) * (1 - f));
  return '#' + [16, 8, 0].map(k => c(k).toString(16).padStart(2, '0')).join('');
}
function onPress(btn, tap, long) {
  let timer = null, longDone = false;
  btn.addEventListener('pointerdown', () => { longDone = false; if (long) timer = setTimeout(() => { longDone = true; buzz(30); long(); }, 550); });
  const cancel = () => clearTimeout(timer);
  btn.addEventListener('pointerup', cancel); btn.addEventListener('pointerleave', cancel); btn.addEventListener('pointercancel', cancel);
  btn.addEventListener('contextmenu', e => e.preventDefault());
  btn.addEventListener('click', e => { if (longDone) { e.preventDefault(); return; } buzz(); tap(); });
}

// ---- Long names --------------------------------------------------------------------------------------------
// « Applaudissements » must not be cut in the middle of the word: the label shrinks until its longest word fits.
const measure = document.createElement('canvas').getContext('2d');
function fitLabels() {
  document.querySelectorAll('.tile .lbl').forEach(l => {
    const tile = l.closest('.tile'); if (!tile || !tile.clientWidth) return;
    const ts = getComputedStyle(tile);
    const room = tile.clientWidth - parseFloat(ts.paddingLeft) - parseFloat(ts.paddingRight) - 2;
    const word = l.textContent.split(/\s+/).reduce((a, b) => b.length > a.length ? b : a, '');
    l.style.fontSize = '';
    const cs = getComputedStyle(l); const size = parseFloat(cs.fontSize);
    measure.font = cs.fontWeight + ' ' + size + 'px ' + cs.fontFamily;
    const need = measure.measureText(word).width;
    if (need > room && room > 0) l.style.fontSize = Math.max(10, Math.floor(size * room / need * 10) / 10) + 'px';
  });
}
let fitPending = false;
function fitSoon() { if (fitPending) return; fitPending = true; requestAnimationFrame(() => { fitPending = false; fitLabels(); }); }
new MutationObserver(fitSoon).observe(document.body, { childList: true, subtree: true });
addEventListener('resize', fitSoon);

// ---- Deck items ------------------------------------------------------------------------------------------
function describe(key) {
  const i = key.indexOf(':'); const kind = key.slice(0, i), value = key.slice(i + 1);
  if (kind === 'pad') { const p = catalog.pads.find(x => x.id === value); return p && { kind, value, label: p.name, icon: p.icon || 'wave-sine', c1: p.color, c2: shade(p.color, .38), sub: p.hotkey || p.category || 'Son' }; }
  if (kind === 'preset') { const p = catalog.presets.find(x => x.name === value); return p && { kind, value, label: p.name, icon: 'wand', c1: '#c084fc', c2: '#7c3aed', sub: 'Effet de voix' }; }
  if (kind === 'say') return { kind, value, label: value, icon: 'message-circle', c1: '#f472b6', c2: '#db2777', sub: 'Dire dans le micro' };
  if (kind === 'act') { const a = ACTIONS[value]; return a && { kind, value, label: a[1], icon: a[0], c1: a[2], c2: a[3], sub: '' }; }
  return null;
}
function tileFor(key, opts) {
  const d = describe(key); if (!d) return null;
  const b = el('button', 'tile'); b.dataset.key = key;
  b.style.setProperty('--c1', d.c1); b.style.setProperty('--c2', d.c2);
  const ic = el('span', 'ico'); ic.appendChild(icon(d.icon)); b.appendChild(ic);
  const lbl = el('span', 'lbl', d.label);
  const txt = el('span'); txt.append(lbl, el('span', 'sub', d.sub)); b.appendChild(txt);
  b.setAttribute('aria-label', d.label);
  const editable = opts && opts.editable;
  if (key === 'act:ptt' && !editable) bindHold(b);
  else onPress(b, () => { if (!editable) runKey(key); }, opts && opts.long ? () => pinSheet(key) : null);
  if (editable) {
    const ed = el('div', 'edit'), l = el('span'), r = el('span'), x = el('span', 'x');
    l.appendChild(icon('chevron-left')); r.appendChild(icon('chevron-right')); x.appendChild(icon('x'));
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
function renderAll() { if (!catalog) return; renderDeck(); renderPads(); renderVoice(); updateLive(); }
function deckItems() { return (editing ? deckDraft : catalog.deck).filter(k => describe(k)); }
function renderDeck() {
  const grid = $('deckGrid'); grid.innerHTML = '';
  const items = deckItems();
  $('deckEmpty').hidden = items.length > 0 || editing;
  grid.classList.toggle('editing', editing);
  for (const k of items) { const t = tileFor(k, { editable: editing }); if (t) grid.appendChild(t); }
  if (editing) { const add = el('button', 'tile addtile'); add.append(icon('plus'), el('span', 'lbl', 'Ajouter')); add.onclick = () => pickerSheet(); grid.appendChild(add); }
  const eb = $('editDeck'); eb.classList.toggle('primary', editing); labelled(eb, editing ? 'check' : 'pencil', editing ? 'Terminé' : 'Modifier');
  eb.style.display = (!editing && items.length === 0) ? 'none' : '';
  updateLive();
}
function moveDeck(key, dir) { const i = deckDraft.indexOf(key), j = i + dir; if (i < 0 || j < 0 || j >= deckDraft.length) return; [deckDraft[i], deckDraft[j]] = [deckDraft[j], deckDraft[i]]; renderDeck(); }
async function saveDeck(items) { await call('/api/deck', { items: items.join('|') }); catalog.deck = items.slice(); }
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
  for (const c of ['', ...cats]) { const b = el('button', c === padTab ? 'sel' : '', c || 'Tous'); b.onclick = () => { padTab = c; renderPads(); }; tabs.appendChild(b); }
  const norm = s => s.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  const q = norm($('padSearch').value.trim());
  const grid = $('padGrid'); grid.innerHTML = '';
  for (const p of catalog.pads.filter(p => (!padTab || p.category === padTab) && (!q || norm(p.name).includes(q)))) grid.appendChild(tileFor('pad:' + p.id, { long: true }));
  $('padNote').textContent = catalog.pads.length === 0 ? 'Aucun son dans la soundboard pour l’instant.' : 'Appui long sur un son pour l’ajouter à Mon deck.';
  updateLive();
}
$('padSearch').addEventListener('input', () => renderPads());

function renderVoice() {
  const grid = $('presetGrid'); grid.innerHTML = '';
  for (const p of catalog.presets) grid.appendChild(tileFor('preset:' + p.name, { long: true }));
  const box = $('phrases'); box.innerHTML = ''; const seen = new Set();
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

function setToggle(btn, on, icOn, icOff, labelOn, labelOff) {
  btn.classList.toggle('on', on); btn.classList.toggle('off', !on); btn.setAttribute('aria-pressed', on ? 'true' : 'false');
  labelled(btn, on ? icOn : icOff, on ? labelOn : labelOff);
}
function stateTile(t, on, icOn, icOff, subOn, subOff, base) {
  t.style.setProperty('--c1', on ? base[2] : GREY[0]); t.style.setProperty('--c2', on ? base[3] : GREY[1]);
  setIcon(t.querySelector('.ico svg'), on ? icOn : icOff); t.querySelector('.sub').textContent = on ? subOn : subOff;
}
function updateLive() {
  if (!state || !catalog) return;
  const playing = new Set(state.playing);
  document.querySelectorAll('.tile[data-key]').forEach(t => {
    const k = t.dataset.key, sub = t.querySelector('.sub');
    t.classList.remove('playing', 'current');
    if (k.startsWith('pad:')) t.classList.toggle('playing', playing.has(k.slice(4)));
    else if (k.startsWith('preset:')) t.classList.toggle('current', k.slice(7) === state.preset);
    else if (k === 'act:mic') stateTile(t, state.micEnabled, 'microphone', 'microphone-off', 'Actif', 'Coupé', ACTIONS.mic);
    else if (k === 'act:effects') stateTile(t, state.effectsEnabled, 'sparkles', 'sparkles', state.preset, 'Coupés', ACTIONS.effects);
    else if (k === 'act:radio') stateTile(t, state.radioMode, 'radio', 'radio', 'Activé', 'Désactivé', ACTIONS.radio);
    else if (k === 'act:noise') stateTile(t, state.noiseSuppression, 'wave-sine', 'wave-sine', 'Activée', 'Désactivée', ACTIONS.noise);
    else if (k === 'act:media-to-mic') stateTile(t, state.mix.mediaToMic, 'speakerphone', 'headphones', 'Partagée', 'Pour vous seul', ACTIONS['media-to-mic']);
    else if (k === 'act:music-playpause') { setIcon(t.querySelector('.ico svg'), state.music.playing ? 'player-pause' : 'player-play'); sub.textContent = state.music.hasTrack ? (state.music.title || '') : 'Aucune musique'; }
    else if (k === 'act:ptt') sub.textContent = state.radioMode ? (state.talking ? 'Votre voix passe' : 'Maintenir') : 'Mode radio désactivé';
    else if (k.startsWith('act:preset-')) sub.textContent = state.preset;
    else if (k.startsWith('act:music-volume-')) sub.textContent = Math.round(state.mix.music * 100) + ' %';
  });
  if (connected) $('status').textContent = (state.micEnabled ? 'Micro actif' : 'Micro coupé') + ' · ' + (state.effectsEnabled ? state.preset : 'sans effet')
    + (state.profile ? ' · ' + state.profile : '');
  setToggle($('vMic'), state.micEnabled, 'microphone', 'microphone-off', 'Micro actif', 'Micro coupé');
  setToggle($('vFx'), state.effectsEnabled, 'sparkles', 'sparkles', 'Effets : ' + state.preset, 'Effets coupés');
  setToggle($('vRadio'), state.radioMode, 'radio', 'radio', 'Mode radio', 'Mode radio désactivé');
  setToggle($('vNoise'), state.noiseSuppression, 'wave-sine', 'wave-sine', 'Anti-bruit', 'Anti-bruit coupé');
  $('ptt').disabled = !state.radioMode;
  $('pttNote').textContent = state.radioMode ? (state.talking ? 'Votre voix passe' : 'Votre voix ne passe que pendant l’appui') : 'Activez le mode radio pour l’utiliser';
  $('sayBtn').disabled = !state.ttsAvailable;
  $('sayNote').textContent = state.ttsAvailable ? '' : 'Aucune voix sur le PC (voir Synthèse vocale).';
  updateMusic(); updateBlindTest();
}
$('vMic').onclick = () => { buzz(); call('/api/action/mic').catch(() => {}); };
$('vFx').onclick = () => { buzz(); call('/api/action/effects').catch(() => {}); };
$('vRadio').onclick = () => { buzz(); call('/api/action/radio').catch(() => {}); };
$('vNoise').onclick = () => { buzz(); call('/api/action/noise').catch(() => {}); };
$('pPrev').onclick = () => { buzz(); call('/api/action/preset-previous').catch(() => {}); };
$('pNext').onclick = () => { buzz(); call('/api/action/preset-next').catch(() => {}); };
$('stopAll').onclick = () => { buzz(25); call('/api/action/stop').catch(() => {}); };

// ---- Music -----------------------------------------------------------------------------------------------
let musicAt = 0, coverKey = null;
const sliders = { music: 'music', pads: 'pads', tomic: 'toMic', monitor: 'monitor' };
const dragging = {};
for (const key of Object.keys(sliders)) {
  const input = $('s-' + key); let last = 0, pending = null;
  const send = () => { last = Date.now(); call('/api/mix', { key, value: (input.value / 100).toFixed(2) }, true).catch(() => {}); };
  input.addEventListener('input', () => { dragging[key] = true; $('s-' + key + '-v').textContent = input.value + ' %'; clearTimeout(pending); if (Date.now() - last > 120) send(); else pending = setTimeout(send, 120); });
  input.addEventListener('change', () => { send(); setTimeout(() => dragging[key] = false, 800); });
}
function coverInto(box, src) {
  box.textContent = '';
  if (src) { const img = el('img'); img.alt = ''; img.src = src; img.onerror = () => { box.textContent = ''; box.appendChild(icon('music')); }; box.appendChild(img); }
  else box.appendChild(icon('music'));
}
function updateMusic() {
  const m = state.music;
  $('mTitle').textContent = m.hasTrack ? (m.title || 'Sans titre') : 'Aucune musique';
  $('mArtist').textContent = m.hasTrack ? (m.artist || '') : 'Lancez une musique sur le PC ou depuis ce téléphone.';
  setIcon($('mPlay').querySelector('svg'), m.playing ? 'player-pause' : 'player-play');
  setIcon($('miniPlay').querySelector('svg'), m.playing ? 'player-pause' : 'player-play');
  $('mini').classList.toggle('show', m.hasTrack && view !== 'music' && !editing);
  $('miniTitle').textContent = m.title || ''; $('miniArtist').textContent = m.artist || '';
  musicAt = Date.now();
  const ck = m.cover || '';
  if (ck !== coverKey) { coverKey = ck; const src = ck ? (ck.startsWith('/') ? url(ck, { t: m.title || '' }) : ck) : ''; coverInto($('cover'), src); coverInto($('miniCover'), src); }
  setToggle($('mToMic'), state.mix.mediaToMic, 'speakerphone', 'headphones', 'Musique dans le micro', 'Musique pour vous seul');
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
const playPause = () => { buzz(); call('/api/action/music-playpause').catch(() => {}); };
$('mPlay').onclick = playPause; $('miniPlay').onclick = playPause;
$('mNext').onclick = $('miniNext').onclick = () => { buzz(); call('/api/action/music-next').catch(() => {}); };
$('mPrev').onclick = () => { buzz(); call('/api/action/music-previous').catch(() => {}); };
$('mToMic').onclick = () => { buzz(); call('/api/action/media-to-mic').catch(() => {}); };
$('miniText').onclick = () => show('music');

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
    const c = el('div', 'card'); const h = el('h2'); h.append(icon('trophy'), document.createTextNode('Podium')); c.appendChild(h);
    for (const p of bt.players) { const r = el('div', 'player'); const n = el('div', 'n'); n.append(el('b', null, p.rank + '. ' + p.name)); r.append(n, el('b', null, p.score + ' pts')); c.appendChild(r); }
    box.appendChild(c); return;
  }
  if (bt.suspended) {
    const c = el('div', 'card'); c.append(el('p', null, 'La partie est en pause : la page Blind test a été quittée sur le PC.'));
    const b = el('button', 'primary'); labelled(b, 'player-play', 'Reprendre la partie'); b.onclick = () => call('/api/action/bt-resume').catch(() => {}); c.appendChild(b); box.appendChild(c); return;
  }
  const stateLabel = { loading: 'Chargement de l’extrait…', listening: bt.paused ? 'En pause' : 'À l’écoute', timeup: 'Temps écoulé !', revealed: 'Réponse révélée' }[bt.state] || '';
  const head = el('div', 'card');
  const top = el('div', 'row'); top.style.justifyContent = 'space-between';
  top.append(el('b', null, 'Manche ' + bt.round + ' / ' + bt.total), el('span', 'note', stateLabel)); head.append(top);
  if (bt.state !== 'revealed') { const c2 = el('div', 'bt-count', String(bt.countdown)); c2.id = 'btCount'; c2.style.margin = '8px 0'; head.appendChild(c2); }
  const ans = el('div', prefs.showAnswer ? '' : 'hidden-answer');
  const lab = el('div', 'row'); lab.style.justifyContent = 'space-between'; lab.append(el('span', 'note', 'Réponse (visible seulement ici)'));
  const eye = el('button', 'chip'); labelled(eye, prefs.showAnswer ? 'eye-off' : 'eye', prefs.showAnswer ? 'Cacher' : 'Voir');
  eye.onclick = () => { prefs.showAnswer = !prefs.showAnswer; savePrefs(); btSig = ''; updateBlindTest(); };
  lab.appendChild(eye); ans.appendChild(lab);
  const a = el('div', 'answer', bt.answerTitle || '…'); a.appendChild(el('small', null, bt.answerArtist || '')); ans.appendChild(a);
  if (bt.hint) ans.appendChild(el('div', 'hint', bt.hint));
  head.appendChild(ans);
  const acts = el('div', 'grid'); acts.style.marginTop = '12px';
  const add = (key, label, ic) => { const t = tileFor('act:' + key); if (!t) return; t.querySelector('.lbl').textContent = label; t.querySelector('.sub').textContent = ''; if (ic) setIcon(t.querySelector('.ico svg'), ic); acts.appendChild(t); };
  if (bt.state !== 'revealed') add('bt-reveal', 'Révéler'); else add('bt-next', bt.round >= bt.total ? 'Voir le podium' : 'Manche suivante');
  if (bt.state === 'listening') add('bt-pause', bt.paused ? 'Reprendre' : 'Pause', bt.paused ? 'player-play' : null);
  add('bt-replay', 'Rejouer');
  if (bt.canHint && bt.state !== 'revealed') add('bt-hint', 'Indice');
  if (bt.state !== 'revealed') add('bt-skip', 'Passer');
  head.appendChild(acts); box.appendChild(head);
  const pl = el('div', 'card'); pl.appendChild(el('h2', null, 'Qui a trouvé ?'));
  for (const p of bt.players) {
    const r = el('div', 'player'); const n = el('div', 'n');
    n.append(el('b', null, p.rank + '. ' + p.name), el('span', null, p.score + ' pts' + (p.fast ? ' · rapide' : '')));
    const t = el('button', p.title ? 'on' : '', 'Titre +2'), ar = el('button', p.artist ? 'on' : '', 'Artiste +1');
    t.onclick = () => { buzz(); call('/api/blindtest/player', { name: p.name, field: 'title' }).catch(() => {}); };
    ar.onclick = () => { buzz(); call('/api/blindtest/player', { name: p.name, field: 'artist' }).catch(() => {}); };
    r.append(n, t, ar); pl.appendChild(r);
  }
  box.appendChild(pl);
}

// ---- Sheets ----------------------------------------------------------------------------------------------
function openSheet(build) { const s = $('sheet'); s.innerHTML = ''; build(s); s.classList.add('open'); $('sheetBg').classList.add('open'); }
function closeSheet() { $('sheet').classList.remove('open'); $('sheetBg').classList.remove('open'); }
$('sheetBg').onclick = closeSheet;
function pickRow(d) {
  const b = el('button', 'pick'), sw = el('span', 'c');
  sw.style.background = 'linear-gradient(135deg,' + d.c1 + ',' + d.c2 + ')'; sw.appendChild(icon(d.icon));
  b.append(sw, el('span', 'l', d.label)); return b;
}
function pinSheet(key) {
  const d = describe(key); if (!d) return;
  const inDeck = catalog.deck.includes(key);
  openSheet(s => {
    s.appendChild(el('h2', null, d.label));
    const b = el('button', 'pick'); labelled(b, inDeck ? 'x' : 'star', inDeck ? 'Retirer de Mon deck' : 'Ajouter à Mon deck');
    b.onclick = async () => {
      const items = inDeck ? catalog.deck.filter(k => k !== key) : [...catalog.deck, key];
      try { await saveDeck(items); renderDeck(); toast(inDeck ? 'Retiré de Mon deck' : 'Ajouté à Mon deck'); } catch (e) {}
      closeSheet();
    };
    const c = el('button', 'pick', 'Annuler'); c.onclick = closeSheet; s.append(b, c);
  });
}
function pickerSheet() {
  openSheet(s => {
    const h = el('h2'); h.append(el('span', 'grow', 'Ajouter au deck'));
    const done = el('button', 'chip primary', 'OK'); done.onclick = closeSheet; h.appendChild(done); s.appendChild(h);
    const search = el('input', 'search'); search.type = 'search'; search.placeholder = 'Rechercher…'; s.appendChild(search);
    const list = el('div'); s.appendChild(list);
    const groups = [
      ['Actions', ['act:ptt', ...Object.keys(ACTIONS).filter(a => a !== 'ptt').map(a => 'act:' + a)]],
      ...[...new Set(catalog.pads.map(p => p.category))].map(c => ['Sons' + (c ? ' · ' + c : ''), catalog.pads.filter(p => p.category === c).map(p => 'pad:' + p.id)]),
      ['Effets de voix', catalog.presets.map(p => 'preset:' + p.name)],
      ['Phrases', [...new Set([...catalog.history, ...catalog.quickPhrases])].map(t => 'say:' + t)],
    ];
    const draw = () => {
      list.innerHTML = ''; const q = search.value.trim().toLowerCase();
      for (const [title, keys] of groups) {
        const items = keys.map(k => [k, describe(k)]).filter(([k, d]) => d && (!q || d.label.toLowerCase().includes(q)));
        if (!items.length) continue;
        list.appendChild(el('h3', null, title));
        for (const [k, d] of items) {
          const b = pickRow(d); if (deckDraft.includes(k)) b.classList.add('in');
          b.onclick = () => { buzz(); if (deckDraft.includes(k)) deckDraft = deckDraft.filter(x => x !== k); else deckDraft.push(k); b.classList.toggle('in'); renderDeck(); };
          list.appendChild(b);
        }
      }
    };
    search.addEventListener('input', draw); draw();
  });
}
$('gear').onclick = () => openSheet(s => {
  s.appendChild(el('h2', null, 'Réglages de la télécommande'));
  s.appendChild(el('h3', null, 'Thème'));
  const th = el('div', 'themes');
  for (const [k, l, bg] of THEMES) {
    const b = el('button', prefs.theme === k ? 'cur' : ''), sw = el('i'); sw.style.background = bg; b.append(sw, document.createTextNode(l));
    b.onclick = () => { prefs.theme = k; savePrefs(); applyTheme(); th.querySelectorAll('button').forEach(x => x.classList.remove('cur')); b.classList.add('cur'); buzz(); };
    th.appendChild(b);
  }
  s.appendChild(th);
  s.appendChild(el('h3', null, 'Taille des boutons'));
  const seg = el('div', 'seg');
  for (const [k, l] of [['s', 'Petits'], ['m', 'Moyens'], ['l', 'Grands']]) {
    const b = el('button', prefs.size === k ? 'sel' : '', l);
    b.onclick = () => { prefs.size = k; savePrefs(); applySize(); seg.querySelectorAll('button').forEach(x => x.classList.remove('sel')); b.classList.add('sel'); };
    seg.appendChild(b);
  }
  s.appendChild(seg);
  s.appendChild(el('h3', null, 'Retour au toucher'));
  const vib = el('button', 'pick'), setVib = () => labelled(vib, 'device-mobile', prefs.vibrate ? 'Vibration activée' : 'Vibration désactivée'); setVib();
  vib.onclick = () => { prefs.vibrate = !prefs.vibrate; savePrefs(); setVib(); buzz(30); };
  s.appendChild(vib);
  if (!navigator.vibrate) s.appendChild(el('p', 'note', 'Ce téléphone ne permet pas la vibration depuis une page web (iPhone).'));
  s.appendChild(el('h3', null, 'Comme une application'));
  s.appendChild(el('p', 'note', 'iPhone : bouton Partager → « Sur l’écran d’accueil ». Android : menu ⋮ → « Ajouter à l’écran d’accueil ». La télécommande s’ouvre alors en plein écran, d’un seul geste.'));
  s.appendChild(el('p', 'note', 'Astuce : pendant vos parties, réglez la mise en veille de l’écran du téléphone sur une durée longue.'));
  const close = el('button', 'pick', 'Fermer'); close.onclick = closeSheet; s.appendChild(close);
});

// ---- Navigation --------------------------------------------------------------------------------------------
const TITLES = { deck: 'Mon deck', pads: 'Sons', voice: 'Voix', music: 'Musique', bt: 'Blind test' };
function show(v) {
  view = v;
  document.querySelectorAll('.view').forEach(x => x.classList.toggle('active', x.id === 'v-' + v));
  document.querySelectorAll('nav.bottom button').forEach(b => b.classList.toggle('on', b.dataset.v === v));
  $('ttl').textContent = TITLES[v];
  if (editing && v !== 'deck') { editing = false; deckDraft = null; renderDeck(); }
  if (state) updateMusic();
  window.scrollTo(0, 0);
}
document.querySelectorAll('nav.bottom button').forEach(b => b.onclick = () => { buzz(8); show(b.dataset.v); });

applyTheme(); applySize(); labelled($('editDeck'), 'pencil', 'Modifier');
if (!code) fatal('Code d’accès manquant : scannez le QR code affiché dans SpokaSoundBoard (Configuration → Télécommande).');
else { loadCatalog().catch(() => toast('PC injoignable : SpokaSoundBoard est-il ouvert ?')); connect(); }
</script>
</body>
</html>
"""";
}
