namespace SpokaSoundBoard.Core.Remote;

/// <summary>The phone remote: one self-contained page (no external file, works offline on the local network).</summary>
public static class RemotePage
{
    public const string Html = """
<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f1117">
<title>Télécommande SpokaSoundBoard</title>
<style>
  :root { --bg:#0f1117; --card:#1a1e2b; --border:#2a3042; --text:#f1f3f9; --sub:#9aa3b8; --accent:#7c5cff; --ok:#22c55e; --danger:#f43f5e; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
  header { position:sticky; top:0; z-index:2; background:var(--bg); padding:14px 16px 8px; border-bottom:1px solid var(--border); }
  h1 { font-size:18px; margin:0 0 10px; }
  .row { display:flex; gap:8px; flex-wrap:wrap; }
  button { font:inherit; color:var(--text); background:var(--card); border:1px solid var(--border); border-radius:10px; padding:10px 12px; cursor:pointer; }
  button:active { transform:scale(.97); }
  .on { border-color:var(--ok); color:var(--ok); }
  .off { border-color:var(--danger); color:var(--danger); }
  .stop { border-color:var(--danger); }
  .preset { display:flex; align-items:center; gap:6px; flex:1 1 100%; color:var(--sub); font-size:13px; }
  .preset b { color:var(--text); font-weight:600; flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .tabs { display:flex; gap:6px; overflow-x:auto; padding:10px 16px 2px; }
  .tabs button { border-radius:999px; padding:6px 12px; font-size:13px; white-space:nowrap; }
  .tabs .sel { border-color:var(--accent); background:#7c5cff33; }
  main { display:grid; grid-template-columns:repeat(auto-fill,minmax(140px,1fr)); gap:10px; padding:12px 16px 24px; }
  .pad { min-height:84px; text-align:left; display:flex; flex-direction:column; justify-content:space-between; border-width:2px; }
  .pad .name { font-weight:600; overflow-wrap:anywhere; }
  .pad .key { font-size:12px; color:var(--sub); }
  .pad.playing { box-shadow:0 0 0 2px var(--text) inset; }
  #msg { padding:24px 16px; color:var(--sub); text-align:center; }
</style>
</head>
<body>
<header>
  <h1>🎛️ SpokaSoundBoard</h1>
  <div class="row">
    <button id="mic">Micro</button>
    <button id="fx">Effets</button>
    <button class="stop" id="stop">⏹ Tout arrêter</button>
    <button id="music">⏯ Musique</button>
    <div class="preset"><button id="prev" aria-label="Effet précédent">◀</button><b id="preset">…</b><button id="next" aria-label="Effet suivant">▶</button></div>
  </div>
</header>
<nav class="tabs" id="tabs"></nav>
<main id="pads"></main>
<div id="msg">Connexion…</div>
<script>
const code = new URLSearchParams(location.search).get('code') || '';
let pads = [], tab = '';
async function call(path, post) {
  const r = await fetch(path + '?code=' + encodeURIComponent(code), { method: post ? 'POST' : 'GET', cache: 'no-store' });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.error || ('erreur ' + r.status));
  return data;
}
function show(text) { const m = document.getElementById('msg'); m.textContent = text || ''; m.style.display = text ? 'block' : 'none'; }
function renderTabs() {
  const cats = [...new Set(pads.map(p => p.category).filter(c => c))];
  const el = document.getElementById('tabs'); el.innerHTML = '';
  if (cats.length === 0) { el.style.display = 'none'; return; }
  el.style.display = 'flex';
  for (const c of ['', ...cats]) {
    const b = document.createElement('button');
    b.textContent = c || 'Tous'; if (c === tab) b.className = 'sel';
    b.onclick = () => { tab = c; renderTabs(); renderPads(); };
    el.appendChild(b);
  }
}
function renderPads() {
  const el = document.getElementById('pads'); el.innerHTML = '';
  const list = pads.filter(p => !tab || p.category === tab);
  for (const p of list) {
    const b = document.createElement('button');
    b.className = 'pad' + (p.playing ? ' playing' : '');
    b.style.background = p.color + '33'; b.style.borderColor = p.color;
    const n = document.createElement('span'); n.className = 'name'; n.textContent = p.name; b.appendChild(n);
    if (p.hotkey) { const k = document.createElement('span'); k.className = 'key'; k.textContent = p.hotkey; b.appendChild(k); }
    b.onclick = () => call('/api/pads/' + p.id + '/play', true).then(refresh).catch(e => show(e.message));
    el.appendChild(b);
  }
  if (pads.length === 0) show('Aucun son dans la soundboard pour l’instant.'); else show('');
}
async function refresh() {
  try {
    const [list, st] = await Promise.all([call('/api/pads'), call('/api/status')]);
    const changed = JSON.stringify(list.map(p => [p.id, p.name, p.category])) !== JSON.stringify(pads.map(p => [p.id, p.name, p.category]));
    pads = list; if (changed) renderTabs(); renderPads();
    const mic = document.getElementById('mic'); mic.textContent = st.micEnabled ? '🎙 Micro actif' : '🔇 Micro coupé'; mic.className = st.micEnabled ? 'on' : 'off';
    const fx = document.getElementById('fx'); fx.textContent = st.effectsEnabled ? '✨ Effets' : 'Effets coupés'; fx.className = st.effectsEnabled ? 'on' : '';
    document.getElementById('preset').textContent = st.preset;
    document.getElementById('music').textContent = (st.musicPlaying ? '⏸ ' : '▶ ') + (st.music || 'Musique');
  } catch (e) { show(e.message + ' — vérifiez que SpokaSoundBoard est ouvert.'); }
}
const act = (id, path) => document.getElementById(id).onclick = () => call(path, true).then(refresh).catch(e => show(e.message));
act('mic', '/api/mic/toggle'); act('fx', '/api/effects/toggle'); act('stop', '/api/stop');
act('music', '/api/music/playpause'); act('prev', '/api/preset/previous'); act('next', '/api/preset/next');
if (!code) show('Code d’accès manquant : ouvrez l’adresse affichée dans Configuration → Télécommande.'); else { refresh(); setInterval(refresh, 1500); }
</script>
</body>
</html>
""";
}
