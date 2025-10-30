// Reference: Oxide.Core, Oxide.Game.Rust, UnityEngine
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("RustStatsExporter", "you", "1.0.0-clean")]
    [Description("Collects per-player stats and periodically exports them to a web API.")]
    public class RustStatsExporter : CovalencePlugin
    {
        // ---------------- Config ----------------
        private class PluginConfig
        {
            public string ApiUrl = "http://127.0.0.1:8000/ingest";
            public string ApiKey = "tJXYYhqC-r4B9hv_DruWo!2o9.wVUx"; // your key
            public float FlushSeconds = 30f;
            public float DistanceSampleSeconds = 0.5f;
            public bool LogDebug = true;
        }
        private PluginConfig _cfg;

        protected override void LoadDefaultConfig() => _cfg = new PluginConfig();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig(); }
            catch
            {
                PrintWarning("Config invalid; writing defaults.");
                _cfg = new PluginConfig();
            }
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        // ---------------- Models ----------------
        private class Counters
        {
            public ulong user_id;
            public string last_name;
            public Dictionary<string, double> k = new Dictionary<string, double>();
            public double highest_range_kill_m = 0.0; // kept as max locally
        }
        private class Payload
        {
            public long server_unix_time;
            public List<Counters> players;
        }

        // ---------------- State ----------------
        private readonly Dictionary<ulong, Counters> _stats = new Dictionary<ulong, Counters>();
        private readonly Dictionary<ulong, Vector3> _lastPos = new Dictionary<ulong, Vector3>();

        private WebRequests _web; // initialized in Init()

        // ---------------- Helpers ----------------
        private Counters Get(ulong id)
        {
            if (!_stats.TryGetValue(id, out var c))
            {
                c = new Counters { user_id = id, last_name = id.ToString() };
                _stats[id] = c;
            }
            return c;
        }
        private void SetName(ulong id, string name)
        {
            if (id == 0 || string.IsNullOrEmpty(name)) return;
            Get(id).last_name = name;
        }
        private void Add(ulong id, string key, double amt = 1)
        {
            if (id == 0 || string.IsNullOrEmpty(key) || Math.Abs(amt) < double.Epsilon) return;
            var c = Get(id);
            c.k[key] = c.k.TryGetValue(key, out var cur) ? cur + amt : amt;
            if (_cfg.LogDebug) Puts($"[ADD] {id} {key} += {amt} (now {c.k[key]})");
        }
        private static bool IsBarrel(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            var p = prefab.ToLowerInvariant();
            return p.Contains("barrel") || p.Contains("oil_barrel") || p.Contains("loot-barrel");
        }
        private static string AmmoBucket(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return "other";
            var s = shortname.ToLowerInvariant();
            if (s.Contains("pistol")) return "pistol";
            if (s.Contains("rifle")) return "rifle";
            if (s.Contains("shotgun") || s.Contains("handmade.shell")) return "shotgun";
            if (s.Contains("nailgun") || s.Contains("nails")) return "nail";
            if (s.Contains("arrow") || s.Contains("bolt")) return "arrow";
            if (s.Contains("rocket")) return "rocket";
            return "other";
        }
        private static string RocketKindFromAmmo(string shortname)
{
    if (string.IsNullOrEmpty(shortname)) return null;
    switch (shortname)
    {
        case "ammo.rocket.basic":       return "basic";        // standard explosive
        case "ammo.rocket.hv":          return "hv";
        case "ammo.rocket.incendiary":  return "incendiary";
        case "ammo.rocket.smoke":       return "smoke";
        default: return null;
    }
}

        private void CountRocketByKind(BasePlayer player, string kind)
        {
            if (player == null || string.IsNullOrEmpty(kind)) return;

            // Always track per-type keys
            Add(player.userID, $"rockets.{kind}");

            // Maintain legacy "rockets.fired" as **only** standard explosive
            if (kind == "basic")
                Add(player.userID, "rockets.fired");

            // (Optional) also keep a grand total if you want later:
            // Add(player.userID, "rockets.total");
        }
private static string RocketKindFromEntity(BaseEntity rocket)
{
    if (rocket == null) return null;

    var n = (rocket.ShortPrefabName ?? "").ToLowerInvariant();

    if (n.Contains("rocket_basic"))      return "basic";
    if (n.Contains("rocket_hv"))         return "hv";
    if (n.Contains("rocket_incendiary")) return "incendiary";
    if (n.Contains("rocket_smoke"))      return "smoke";

    return null;
}



        // ---------------- Lifecycle ----------------
        private void Init()
        {
            LoadConfig();
            _web = Interface.Oxide.GetLibrary<WebRequests>("WebRequests");

            // start timers on server init to ensure players list is ready
        }
        private void OnServerInitialized()
        {
            // distance sampler
            var distIv = Math.Max(0.25f, _cfg.DistanceSampleSeconds);
            timer.Every(distIv, () =>
            {
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.IsAlive()) continue;
                    var id = p.userID;
                    SetName(id, p.displayName);

                    var cur = p.transform.position;
                    if (_lastPos.TryGetValue(id, out var prev))
                    {
                        var d = Vector3.Distance(prev, cur);
                        if (d > 0.05f && d < 50f)
                            Add(id, "distance.m", d);
                    }
                    _lastPos[id] = cur;
                }
            });

            // periodic flush
            var flushIv = Math.Max(5f, _cfg.FlushSeconds);
            timer.Every(flushIv, FlushAll);

            if (_cfg.LogDebug) Puts("RustStatsExporter initialized.");
        }
        private void Unload()
        {
            FlushAll();
        }

        // ---------------- Identity ----------------
        private void OnUserConnected(IPlayer player)
        {
            if (player == null) return;
            if (ulong.TryParse(player.Id, out var id))
                SetName(id, player.Name);
        }
        private void OnPlayerInit(BasePlayer p)
        {
            if (p == null) return;
            SetName(p.userID, p.displayName);
            _lastPos[p.userID] = p.transform.position;
        }

// ---------------- Gathering (wood/stone/metal/HQM/sulfur) ----------------

private void AddFromItem(ulong uid, Item item)
{
    if (uid == 0 || item == null || item.info == null) return;

    string key = null;
    switch (item.info.shortname)
    {
        case "wood":         key = "gather.wood";   break;
        case "stones":       key = "gather.stone";  break;
        case "metal.ore":    key = "gather.metal";  break;
        case "hq.metal.ore": key = "gather.hq";     break;
        case "sulfur.ore":   key = "gather.sulfur"; break;
    }

    if (key != null && item.amount > 0)
        Add(uid, key, item.amount);
}

// Default yield tick (trees/ores/animals etc.)
private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity ent, Item item)
{
    var p = ent?.ToPlayer();
    if (p == null) return;
    AddFromItem(p.userID, item);
}

// Bonus yield (weak-spot “sparkle” hits, jackhammer, etc.)
private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
{
    if (player == null) return;
    AddFromItem(player.userID, item);
}

        // (Optional) Quarry output
        // Note: this hook exists on uMod/Oxide. If your version doesn't fire it, no harm.
        /*
        private void OnQuarryGather(BaseMiningQuarry quarry, Item item)
        {
            var ownerId = quarry?.OwnerID ?? 0;
            if (ownerId != 0) AddFromItem(ownerId, item);
        }
        */

        // ---------------- Combat / Kills / Deaths / Barrels / Highest range kill ----------------
private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
{
    if (victim == null || info == null) return;

    var attacker = info.InitiatorPlayer;

    // ----- Barrels -----
    if (IsBarrel(victim.ShortPrefabName))
    {
        if (attacker != null && attacker.userID != 0 && !attacker.IsNpc)
            Add(attacker.userID, "barrels.destroyed");
        return;
    }

    // ----- Boss vehicles -----
    if (victim is BradleyAPC)
    {
        if (attacker != null && attacker.userID != 0 && !attacker.IsNpc)
            Add(attacker.userID, "bradley.destroyed");
        return;
    }
    if (victim is BaseHelicopter)
    {
        if (attacker != null && attacker.userID != 0 && !attacker.IsNpc)
            Add(attacker.userID, "heli.destroyed");
        return;
    }

    // ----- Players (BasePlayer covers humans and NPCs) -----
    if (victim is BasePlayer vp)
    {
        // NPC player (scientists/tunnel dwellers/etc.)
        if (vp.IsNpc)
        {
            if (attacker != null && attacker.userID != 0 && !attacker.IsNpc && attacker.userID != vp.userID)
                Add(attacker.userID, "kills.npc");
            return;
        }

        // Real player death
        Add(vp.userID, "deaths");

        if (attacker != null && attacker.userID != vp.userID && !attacker.IsNpc)
        {
            Add(attacker.userID, "kills.player");

            // Highest range kill (max)
            try
            {
                var range = info.ProjectileDistance > 0f
                    ? info.ProjectileDistance
                    : Vector3.Distance(attacker.eyes?.position ?? attacker.transform.position, vp.transform.position);
                var c = Get(attacker.userID);
                if (range > c.highest_range_kill_m)
                    c.highest_range_kill_m = range;
            }
            catch { /* ignore */ }
        }
        return;
    }

    // ----- Animals / non-player NPC prefabs (rare) -----
    if (attacker != null && !attacker.IsNpc)
    {
        var n = (victim.ShortPrefabName ?? string.Empty).ToLowerInvariant();
        if (n.Contains("boar") || n.Contains("stag") || n.Contains("wolf") || n.Contains("bear") || n.Contains("chicken") || n.Contains("horse"))
            Add(attacker.userID, "kills.animal");
        else if (n.Contains("scientist") || n.Contains("tunneldweller") || n.Contains("underwaterdweller")
              || n.Contains("bandit") || n.Contains("murderer") || n.Contains("scarecrow"))
            Add(attacker.userID, "kills.npc");
    }
}



        // simple signature
        private object OnWeaponFired(BaseProjectile proj, BasePlayer player)
        {
            var ammo = proj?.primaryMagazine?.ammoType?.shortname ?? "";
            var kind = RocketKindFromAmmo(ammo);
            if (kind != null)
            {
                CountRocketByKind(player, kind);
                return null;
            }

            // non-rocket: bucket bullets/arrows as you already do
            var bucket = AmmoBucket(ammo);
            if (bucket == "rocket")
            {
                // In rare cases shortname may be generic; treat as basic fallback
                CountRocketByKind(player, "basic");
            }
            else
            {
                Add(player.userID, $"bullets.{bucket}");
            }
            return null;
        }
private void OnRocketLaunched(BasePlayer player, BaseEntity rocket)
{
    var kind = RocketKindFromEntity(rocket) ?? "basic";
    CountRocketByKind(player, kind);
}




        // ---------------- Looting (Airdrops / Hacked crates) ----------------
        private void OnLootEntityEnd(BasePlayer p, BaseEntity ent)
        {
            if (p == null || ent == null) return;
            var name = (ent.ShortPrefabName ?? "").ToLowerInvariant();
            if (name == "supply_drop" || name.Contains("supply_drop"))
                Add(p.userID, "airdrops.collected");
            else if (name.Contains("hackable") || name.Contains("hackablecrate") || name.Contains("crate_elite_hackable"))
                Add(p.userID, "hackedcrates.collected");
        }

        // ---------------- Flush to API ----------------
        private void FlushAll()
        {
            if (_stats.Count == 0) return;

            var snapshot = new Payload
            {
                server_unix_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                players = _stats.Values.Select(c => new Counters
                {
                    user_id = c.user_id,
                    last_name = c.last_name,
                    k = new Dictionary<string, double>(c.k),
                    highest_range_kill_m = c.highest_range_kill_m
                }).ToList()
            };

            var json = JsonConvert.SerializeObject(snapshot); // Newtonsoft is reliable on all builds

            // backup locally for troubleshooting
            try
            {
                var dir = $"{Interface.Oxide.DataDirectory}/{Name}";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "RustStatsExporter_LastBatch.json"), json);
            }
            catch { /* ignore backup errors */ }

            if (_cfg.LogDebug) Puts($"[Flush] players={snapshot.players.Count}");

            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            if (!string.IsNullOrEmpty(_cfg.ApiKey)) headers["X-API-Key"] = _cfg.ApiKey;

        _web.Enqueue(
            _cfg.ApiUrl,
            json, // <-- post body (2nd arg)
            (code, resp) =>
            {
                if (code >= 200 && code < 300)
                {
                    Puts($"[Flush] OK {code}");
                    foreach (var c in _stats.Values) c.k.Clear();
                }
                else
                {
                    PrintWarning($"[Flush] FAILED {code} {resp}");
                }
            },
            this,
            Core.Libraries.RequestMethod.POST,
            headers,
            30f
        );

        }

        // ---------------- Console helper ----------------
        [ConsoleCommand("rsx.flush")]
        private void CC_Flush(ConsoleSystem.Arg arg)
        {
            FlushAll();
            arg?.ReplyWith("flush queued");
        }
    }
}
