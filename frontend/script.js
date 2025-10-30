// --- globals & helpers ---
function getAPI() {
  const el = document.getElementById("apiBase");
  return el ? el.value.trim() : "";
}
function fmt(n){ return (typeof n === "number") ? Math.round((n + Number.EPSILON)*100)/100 : n; }
function escapeHtml(s){ return (s+"").replace(/[&<>"']/g, m=>({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[m])); }
function setStatus(msg){
  const el = document.getElementById("status");
  if (el) el.textContent = msg || "";
}

// --- API calls wired to UI ---
async function loadKeys(){
  const API = getAPI();
  setStatus("Connecting…");
  try {
    const r = await fetch(`${API}/keys`);
    if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
    const data = await r.json();
    const sel = document.getElementById("keySel");
    sel.innerHTML = "";
    (data.keys || []).sort().forEach(k=>{
      const opt = document.createElement("option");
      opt.value = k; opt.textContent = k;
      sel.appendChild(opt);
    });
    setStatus(data.keys?.length ? `Loaded ${data.keys.length} keys` : "No keys yet");
  } catch (e){
    setStatus(`Connect failed: ${e.message}`);
    console.error(e);
  }
}

async function loadLeaderboard(){
  const API = getAPI();
  const key = document.getElementById("keySel").value;
  if (!key){
    document.getElementById("lb").innerHTML = `<div class="card">No key selected.</div>`;
    return;
  }
  setStatus(`Loading leaderboard: ${key}…`);
  const r = await fetch(`${API}/leaderboard/${encodeURIComponent(key)}?limit=50`);
  const data = await r.json();
  const rows = (data||[]).map((row,i)=>
    `<tr>
       <td>${i+1}</td>
       <td><a href="#" onclick="showPlayer(${row.user_id});return false;">${escapeHtml(row.last_name)}</a></td>
       <td class="mono">${row.user_id}</td>
       <td>${fmt(row.value)}</td>
     </tr>`
  ).join("");
  document.getElementById("lb").innerHTML =
    `<div class="card"><table>
       <thead><tr><th>#</th><th>Player</th><th>SteamID</th><th>${escapeHtml(key)}</th></tr></thead>
       <tbody>${rows}</tbody>
     </table></div>`;
  setStatus("");
}

async function searchPlayers(){
  const API = getAPI();
  const q = document.getElementById("q").value;
  const r = await fetch(`${API}/players/search?q=${encodeURIComponent(q)}`);
  const data = await r.json();
  const rows = (data||[]).map(p=>
    `<tr>
       <td><a href="#" onclick="showPlayer(${p.user_id});return false;">${escapeHtml(p.last_name)}</a></td>
       <td class="mono">${p.user_id}</td>
       <td class="muted">seen ${new Date(p.last_seen*1000).toLocaleString()}</td>
     </tr>`
  ).join("");
  document.getElementById("player").innerHTML =
    `<div class="card"><table>
       <thead><tr><th>Name</th><th>SteamID</th><th>Last Seen</th></tr></thead>
       <tbody>${rows}</tbody>
     </table></div>`;
}

async function showPlayer(id){
  const API = getAPI();
  const r = await fetch(`${API}/players/${id}/stats`);
  if (!r.ok){
    document.getElementById("player").innerHTML = `<div class="card">Player not found.</div>`;
    return;
  }
  const p = await r.json();
  const entries = Object.entries(p.counters||{})
    .sort(([a],[b])=>a.localeCompare(b))
    .map(([k,v])=>`<tr><td>${escapeHtml(k)}</td><td>${fmt(v)}</td></tr>`)
    .join("");
  document.getElementById("player").innerHTML = `
    <div class="card">
      <h3>${escapeHtml(p.last_name)} <small class="muted mono">(${p.user_id})</small></h3>
      <div class="muted">Last seen: ${new Date(p.last_seen*1000).toLocaleString()}</div>
      <div class="muted">Highest range kill: ${fmt(p.highest_range_kill_m)} m</div>
      <h4>Counters</h4>
      <table><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>${entries}</tbody></table>
    </div>`;
}

// Expose functions so the HTML onclick="" works
window.loadKeys = loadKeys;
window.loadLeaderboard = loadLeaderboard;
window.searchPlayers = searchPlayers;
window.showPlayer = showPlayer;

// Auto-run when the page is ready (optional)
window.addEventListener("DOMContentLoaded", () => {
  // Optionally pre-load keys once
  // loadKeys().catch(console.error);
});

const el = (id) => document.getElementById(id);
const fmt = (n) => (n == null ? "0" : Number(n).toLocaleString());

async function searchPlayers() {
  const q = el("playerSearch").value.trim();
  el("searchResults").innerHTML = "";
  el("playerDetail").innerHTML = "";
  if (!q) return;

  const r = await fetch(`${API_BASE}/players/search?q=${encodeURIComponent(q)}`);
  const data = await r.json(); // [{user_id,last_name,last_seen},...]
  if (!data || data.length === 0) {
    el("searchResults").innerHTML = `<div class="muted">No matches.</div>`;
    return;
  }

  // render result list
  el("searchResults").innerHTML = data
    .map(
      (p) => `
      <button class="result" data-id="${p.user_id}">
        <span class="name">${escapeHtml(p.last_name || p.user_id)}</span>
        <span class="id">${p.user_id}</span>
      </button>`
    )
    .join("");

  // attach click handler (delegated)
  el("searchResults").onclick = async (ev) => {
    const btn = ev.target.closest("button.result");
    if (!btn) return;
    const id = btn.getAttribute("data-id");
    loadPlayerDetail(id);
  };
}

async function loadPlayerDetail(userId) {
  const r = await fetch(`${API_BASE}/players/${encodeURIComponent(userId)}`);
  if (!r.ok) {
    el("playerDetail").innerHTML = `<div class="muted">Player not found.</div>`;
    return;
  }
  const p = await r.json(); // {user_id,last_name,last_seen,counters:{},highest_range_kill_m}

  const counters = p.counters || {};
  // Pick a few buckets to show; add more as you like
  const rows = [
    ["kills.player", "Kills"],
    ["deaths", "Deaths"],
    ["distance.m", "Distance (m)"],
    ["gather.wood", "Wood"],
    ["gather.stone", "Stone"],
    ["gather.metal", "Metal"],
    ["gather.hq", "HQM"],
    ["gather.scrap", "Scrap"],
    ["bullets.pistol", "Pistol shots"],
    ["bullets.rifle", "Rifle shots"],
    ["rockets.fired", "Rockets"],
    ["barrels.destroyed", "Barrels"],
    ["hackedcrates.collected", "Hacked crates"],
    ["airdrops.collected", "Airdrops"],
  ];

  const statsHtml = rows
    .map(([k, label]) => {
      const val = counters[k] ?? 0;
      return `<tr><td>${label}</td><td class="num">${fmt(val)}</td></tr>`;
    })
    .join("");

  el("playerDetail").innerHTML = `
    <div class="card">
      <h3>${escapeHtml(p.last_name || p.user_id)}</h3>
      <div class="muted">SteamID: ${p.user_id}</div>
      <table class="mini">
        <tbody>
          ${statsHtml}
          <tr><td>Highest Range Kill (m)</td><td class="num">${fmt(p.highest_range_kill_m)}</td></tr>
        </tbody>
      </table>
    </div>`;
}

// util to avoid HTML injection from names
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[c]));
}

document.addEventListener("DOMContentLoaded", () => {
  const btn = el("playerSearchBtn");
  btn?.addEventListener("click", searchPlayers);
  el("playerSearch")?.addEventListener("keydown", (e) => {
    if (e.key === "Enter") searchPlayers();
  });
  // --- Search / Player detail ---
function escapeHtml(s){ return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

const searchInput = document.getElementById('playerSearch');
const searchBtn   = document.getElementById('playerSearchBtn');
const searchDD    = document.getElementById('searchResults');

async function searchPlayers(){
  const q = (searchInput.value || '').trim();
  searchDD.innerHTML = '';
  searchDD.hidden = true;
  if(!q) return;

  try{
    const r = await fetch(`${API_BASE}/players/search?q=${encodeURIComponent(q)}`);
    const data = await r.json(); // [{user_id,last_name,last_seen},...]
    if(!Array.isArray(data) || data.length === 0){
      searchDD.innerHTML = `<div class="row"><span class="name" style="opacity:.7">No matches</span></div>`;
      searchDD.hidden = false;
      return;
    }
    searchDD.innerHTML = data.map(p => `
      <div class="row" data-id="${p.user_id}">
        <span class="name">${escapeHtml(p.last_name || p.user_id)}</span>
        <span class="id">${p.user_id}</span>
      </div>
    `).join('');
    searchDD.hidden = false;
  }catch(e){
    console.error('search error', e);
  }
}

async function loadPlayerDetail(userId){
  try{
    const r = await fetch(`${API_BASE}/players/${encodeURIComponent(userId)}`);
    if(!r.ok){ return; }
    const p = await r.json(); // {user_id,last_name,counters,highest_range_kill_m,...}
    searchDD.hidden = true;

    // Build a quick summary table
    const c = p.counters || {};
    const rows = [
      ['Kills', c['kills.player'] || 0],
      ['Deaths', c['deaths'] || 0],
      ['Distance (km)', ((c['distance.m']||0)/1000).toFixed(2)],
      ['Wood', c['gather.wood'] || 0],
      ['Stone', c['gather.stone'] || 0],
      ['Metal Ore', c['gather.metal'] || 0],
      ['HQ Ore', c['gather.hq'] || 0],
      ['Highest Range Kill (m)', p.highest_range_kill_m || 0]
    ];

    // Simple modal-ish card (re-uses your styles)
    const card = document.createElement('div');
    card.className = 'section';
    card.style.position = 'fixed';
    card.style.top = '20%';
    card.style.left = '50%';
    card.style.transform = 'translateX(-50%)';
    card.style.zIndex = '2000';
    card.style.maxWidth = '520px';
    card.innerHTML = `
      <h2 style="margin:18px">${escapeHtml(p.last_name || p.user_id)}</h2>
      <div class="hint" style="margin:0 18px 12px">SteamID: ${p.user_id}</div>
      <table style="margin:0 18px 18px; width: calc(100% - 36px)">
        <tbody>
          ${rows.map(([label,val]) => `<tr><td style="padding:8px 10px;border-top:1px solid var(--border)">${label}</td><td style="padding:8px 10px;border-top:1px solid var(--border);text-align:right">${Number(val).toLocaleString()}</td></tr>`).join('')}
        </tbody>
      </table>
      <div style="display:flex;justify-content:flex-end;gap:10px;margin:0 18px 18px">
        <button id="closePlayerCard" style="height:34px;border:1px solid var(--border);border-radius:8px;background:#1e293b;color:#fff;padding:0 12px;cursor:pointer">Close</button>
      </div>
    `;
    document.body.appendChild(card);
    document.getElementById('closePlayerCard').onclick = () => card.remove();
  }catch(e){
    console.error('loadPlayerDetail error', e);
  }
}

// Wire events
searchBtn?.addEventListener('click', searchPlayers);
searchInput?.addEventListener('keydown', (e) => { if(e.key === 'Enter') searchPlayers(); });
searchDD?.addEventListener('click', (e) => {
  const row = e.target.closest('.row');
  if(!row) return;
  loadPlayerDetail(row.getAttribute('data-id'));
});

// Hide dropdown when clicking elsewhere
document.addEventListener('click', (e) => {
  if(!searchDD.hidden && !e.target.closest('.search-wrap')) searchDD.hidden = true;
});

});
