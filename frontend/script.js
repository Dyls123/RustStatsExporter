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
