// File: RustStatsExporter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustStatsExporter", "Dyls123", "1.0.0")]
    [Description("Collects per-player gameplay stats and exports them to a web API on a timer.")]
    public class RustStatsExporter : CovalencePlugin
    {
        #region Config
        private class PluginConfig
        {
            public string ApiUrl = "https://your-api.example.com/ingest";
            public string ApiKey = ""; // optional
            public float FlushSeconds = 30f;
            public float DistanceSampleSeconds = 0.5f;
            public bool LogDebug = false;
        }

        private PluginConfig _cfg;

        protected override void LoadDefaultConfig()
        {
            _cfg = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>();
                if (_cfg == null) throw new Exception("null config");
            }
            catch
            {
                PrintWarning("Config invalid, creating default.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);
        #endregion

        #region Data structures
        private class Counters
        {
            public ulong user_id;
            public string last_name;
            public Dictionary<string, double> k = new Dictionary<string, double>();
            public double highest_range_kill_m = 0.0;
        }

        private readonly Dictionary<ulong, Counters> _stats = new Dictionary<ulong, Counters>();
        private readonly Dictionary<ulong, Vector3> _lastPos = new Dictionary<ulong, Vector3>();
        private Timer _flushTimer;
        private Timer _distanceTimer;

        // Gambling context tracking (approximation via scrap delta while at the machine)
        private enum GameCtx { None, BigWheel, Slots, Blackjack }
        private class GambleState
        {
            public GameCtx ctx = GameCtx.None;
            public int lastScrap = 0;
            public double spent = 0;
            public double profit = 0;
        }
        private readonly Dictionary<ulong, GambleState> _gamble = new Dictionary<ulong, GambleState>();
        #endregion

        #region Helpers
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
            var c = Get(id);
            c.last_name = name ?? c.last_name;
        }

        private void Add(ulong id, string key, double amt = 1)
        {
            var c = Get(id);
            if (!c.k.ContainsKey(key)) c.k[key] = 0;
            c.k[key] += amt;
            if (_cfg.LogDebug) Puts($"++ {id} {key} +{amt}");
        }

        private static string AmmoBucket(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return "other";
            shortname = shortname.ToLowerInvariant();
            if (shortname.Contains("pistol")) return "pistol";
            if (shortname.Contains("rifle")) return "rifle";
            if (shortname.Contains("shotgun") || shortname.Contains("handmade.shell")) return "shotgun";
            if (shortname.Contains("nailgun") || shortname.Contains("nails")) return "nail";
            if (shortname.Contains("arrow") || shortname.Contains("bolt")) return "arrow";
            if (shortname.Contains("rocket")) return "rocket";
            return "other";
        }

        private static bool IsBarrel(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            prefab = prefab.ToLowerInvariant();
            return prefab.Contains("barrel") || prefab.Contains("oil_barrel") || prefab.Contains("loot-barrel");
        }

        private GameCtx GuessGameCtxFromEntity(BaseEntity ent)
        {
            if (ent == null) return GameCtx.None;
            var name = ent.ShortPrefabName?.ToLowerInvariant() ?? "";
            // Names may vary slightly by build; these work broadly:
            if (name.Contains("spinningwheel") || name.Contains("bigwheel")) return GameCtx.BigWheel;
            if (name.Contains("slotmachine")) return GameCtx.Slots;
            if (name.Contains("blackjack") || name.Contains("cardtable")) return GameCtx.Blackjack;
            return GameCtx.None;
        }

        private GambleState GState(ulong id)
        {
            if (!_gamble.TryGetValue(id, out var s))
            {
                s = new GambleState();
                _gamble[id] = s;
            }
            return s;
        }

        private int CountScrap(BasePlayer p)
        {
            if (p?.inventory == null) return 0;
            // "scrap" shortname
            int total = 0;
            var all = ListPool<Item>.Get();
            p.inventory.AllItemsNoAlloc(ref all);
            foreach (var it in all)
            {
                if (it?.info?.shortname == "scrap") total += it.amount;
            }
            ListPool<Item>.Recycle(all);
            return total;
        }

        private void UpdateGambleDelta(BasePlayer p)
        {
            var id = p.userID;
            var s = GState(id);
            if (s.ctx == GameCtx.None) return;

            int scrapNow = CountScrap(p);
            int delta = scrapNow - s.lastScrap;
            if (delta != 0)
            {
                // negative delta → spent; positive → profit
                if (delta < 0) s.spent += -delta;
                if (delta > 0) s.profit += delta;

                // reflect into counters by machine
                string prefix = s.ctx == GameCtx.BigWheel ? "casino.bigwheel" :
                                s.ctx == GameCtx.Slots ? "casino.slots" :
                                "casino.blackjack";
                if (delta < 0) Add(id, $"{prefix}.spent", -delta);
                if (delta > 0) Add(id, $"{prefix}.profit", delta);

                s.lastScrap = scrapNow;
            }
        }

        private void StartDistanceSampler()
        {
            _distanceTimer?.Destroy();
            _distanceTimer = timer.Every(_cfg.DistanceSampleSeconds, () =>
            {
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.IsAlive()) continue;
                    var id = p.userID;
                    var cur = p.transform.position;

                    if (_lastPos.TryGetValue(id, out var prev))
                    {
                        float d = Vector3.Distance(prev, cur);
                        // Ignore teleports / respawns spikes
                        if (d > 0.05f && d < 50f)
                        {
                            Add(id, "distance.m", d);
                        }
                    }
                    _lastPos[id] = cur;

                    // While sampling, also keep an eye on gambling scrap delta
                    UpdateGambleDelta(p);
                }
            });
        }

        private void StartFlushTimer()
        {
            _flushTimer?.Destroy();
            _flushTimer = timer.Every(_cfg.FlushSeconds, FlushAll);
        }
        #endregion

        #region Lifecycle
        private void Init()
        {
            StartFlushTimer();
            StartDistanceSampler();
        }

        private void Unload()
        {
            _flushTimer?.Destroy();
            _distanceTimer?.Destroy();
            FlushAll();
        }
        #endregion

        #region Identity
        private void OnUserConnected(IPlayer player)
        {
            if (player == null) return;
            if (ulong.TryParse(player.Id, out var id))
            {
                SetName(id, player.Name);
            }
        }

        private void OnPlayerRespawned(BasePlayer p) => SetName(p.userID, p.displayName);
        private void OnServerInitialized()
        {
            foreach (var p in BasePlayer.activePlayerList)
                SetName(p.userID, p.displayName);
        }
        #endregion

        #region Gathering
        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity ent, Item item)
        {
            var p = ent?.ToPlayer();
            if (p == null || item == null || item.info == null) return;

            var key = item.info.shortname switch
            {
                "wood" => "gather.wood",
                "stones" => "gather.stone",
                "metal.ore" => "gather.metal",
                "hq.metal.ore" => "gather.hq",
                "scrap" => "gather.scrap",
                _ => null
            };
            if (key != null) Add(p.userID, key, item.amount);
        }

        private void OnCollectiblePickup(Item item, BasePlayer p)
        {
            if (p == null || item?.info == null) return;

            var key = item.info.shortname switch
            {
                "wood" => "gather.wood",
                "stones" => "gather.stone",
                "metal.ore" => "gather.metal",
                "hq.metal.ore" => "gather.hq",
                "scrap" => "gather.scrap",
                _ => null
            };
            if (key != null) Add(p.userID, key, item.amount);
        }
        #endregion

        #region Combat / Kills / Deaths / Bosses / Barrels / Highest range kill
        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || info == null) return;
            var attacker = info.InitiatorPlayer;

            // Barrels
            if (IsBarrel(victim.ShortPrefabName))
            {
                if (attacker != null) Add(attacker.userID, "barrels.destroyed");
                return;
            }

            // Bradley & Helicopter
            if (victim is BradleyAPC)
            {
                if (attacker != null) Add(attacker.userID, "bradley.destroyed");
                return;
            }
            if (victim is BaseHelicopter)
            {
                if (attacker != null) Add(attacker.userID, "heli.destroyed");
                return;
            }

            // Players
            var vPlayer = victim as BasePlayer;
            if (vPlayer != null)
            {
                // Count death for the victim if it's a real player (ignore NPC players)
                if (!vPlayer.IsNpc) Add(vPlayer.userID, "deaths");

                // Player-on-player kill
                if (attacker != null && attacker.userID != vPlayer.userID && !vPlayer.IsNpc && !attacker.IsNpc)
                {
                    Add(attacker.userID, "kills.player");

                    // Highest range kill (players only)
                    try
                    {
                        double range = info.ProjectileDistance > 0f
                            ? info.ProjectileDistance
                            : Vector3.Distance(attacker.eyes?.position ?? attacker.transform.position, vPlayer.transform.position);

                        var c = Get(attacker.userID);
                        if (range > c.highest_range_kill_m)
                        {
                            c.highest_range_kill_m = range;
                        }
                    }
                    catch { /* ignore */ }
                }
                return;
            }

            // NPCs & Animals
            if (victim is BaseNpc && attacker != null)
            {
                if (victim is BaseAnimalNPC) Add(attacker.userID, "kills.animal");
                else Add(attacker.userID, "kills.npc");
            }
        }

        private object OnWeaponFired(BaseProjectile proj, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shoot)
        {
            if (player == null || proj == null) return null;
            var ammo = proj.primaryMagazine?.ammoType?.shortname ?? "";
            var bucket = AmmoBucket(ammo);

            if (bucket == "rocket")
            {
                Add(player.userID, "rockets.fired");
            }
            else
            {
                Add(player.userID, $"bullets.{bucket}");
            }

            return null;
        }

        // Thrown explosives (F1, beancan, etc.)
        private void OnExplosiveThrown(ThrownWeapon weapon, BasePlayer player)
        {
            if (player == null || weapon == null) return;
            var name = (weapon?.ShortPrefabName ?? "").ToLowerInvariant();
            if (name.Contains("grenade.f1")) Add(player.userID, "explosive.f1");
            else if (name.Contains("grenade.beancan")) Add(player.userID, "explosive.beancan");
            else if (name.Contains("explosive.satchel") || name.Contains("satchel")) Add(player.userID, "explosive.satchel");
            else if (name.Contains("explosive.timed") || name.Contains("c4")) Add(player.userID, "explosive.c4"); // may be placed, not thrown
            else Add(player.userID, "explosive.other");
        }
        #endregion

        #region Looting (Airdrops / Hacked crates)
        private void OnLootEntityEnd(BasePlayer p, BaseEntity ent)
        {
            if (p == null || ent == null) return;
            var name = (ent.ShortPrefabName ?? "").ToLowerInvariant();
            if (name == "supply_drop" || name.Contains("supply_drop")) Add(p.userID, "airdrops.collected");
            else if (name.Contains("hackable") || name.Contains("hackablecrate") || name.Contains("crate_elite_hackable"))
                Add(p.userID, "hackedcrates.collected");
        }
        #endregion

        #region Gambling (enter/exit context via looting/opening the machine UI)
        private void OnLootEntity(BasePlayer p, BaseEntity ent)
        {
            // Called when player opens loot UI; use as context start for casino machines
            if (p == null || ent == null) return;
            var ctx = GuessGameCtxFromEntity(ent);
            if (ctx == GameCtx.None) return;

            var s = GState(p.userID);
            s.ctx = ctx;
            s.lastScrap = CountScrap(p);
            if (_cfg.LogDebug) Puts($"Gamble start {p.displayName} -> {ctx}, scrap={s.lastScrap}");
        }

        private void OnLootEntityEndHook(BasePlayer p, BaseEntity ent)
        {
            // helper if some builds use different hook names; not always needed
            OnLootEntityEnd(p, ent);
            ClearGamble(p);
        }

        private void OnLootEntityEnd(BasePlayer p, ItemContainer container)
        {
            // Some uMod builds use this signature; we simply clear context on any end
            ClearGamble(p);
        }

        private void OnPlayerDisconnected(BasePlayer p, string reason)
        {
            ClearGamble(p);
            _lastPos.Remove(p.userID);
        }

        private void ClearGamble(BasePlayer p)
        {
            if (p == null) return;
            var s = GState(p.userID);
            if (s.ctx != GameCtx.None)
            {
                // final scrap delta apply
                UpdateGambleDelta(p);
                if (_cfg.LogDebug) Puts($"Gamble end {p.displayName} ({s.ctx}) spent={s.spent} profit={s.profit}");
            }
            s.ctx = GameCtx.None;
            s.spent = 0;
            s.profit = 0;
            s.lastScrap = CountScrap(p);
        }
        #endregion

        #region Flush to API
        [Serializable]
        private class Payload
        {
            public long server_unix_time;
            public List<Counters> players;
        }

        private void FlushAll()
        {
            if (_stats.Count == 0) return;

            // Build payload snapshot
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

            var json = JsonUtility.ToJson(snapshot);
            if (_cfg.LogDebug) Puts($"Flushing {_stats.Count} players ({json.Length} bytes)");

            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            if (!string.IsNullOrEmpty(_cfg.ApiKey)) headers["X-API-Key"] = _cfg.ApiKey;

            // Persist locally as a backup (non-rotating simple mirror)
            Interface.Oxide.DataFileSystem.WriteObject("RustStatsExporter_LastBatch", snapshot, true);

            webrequests.Enqueue(_cfg.ApiUrl, json, (code, response) =>
            {
                if (code == 200 || code == 201 || code == 202)
                {
                    // Clear counters on success
                    foreach (var c in _stats.Values) c.k.Clear();
                    if (_cfg.LogDebug) Puts("Flush OK");
                }
                else
                {
                    PrintWarning($"Flush FAILED ({code}): {response}");
                }
            }, this, Core.Libraries.RequestMethod.POST, headers, _cfg.FlushSeconds - 1f);
        }
        #endregion

        #region Chat command (debug)
        [Command("rsx.stats")]
        private void CmdStats(IPlayer iPlayer, string cmd, string[] args)
        {
            if (!iPlayer.IsAdmin) { iPlayer.Reply("Admin only."); return; }
            if (args.Length == 0)
            {
                iPlayer.Reply("Usage: /rsx.stats <steamid|me> [key]");
                return;
            }
            ulong id = args[0].Equals("me", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(iPlayer.Id) : ulong.Parse(args[0]);
            var c = Get(id);
            if (args.Length == 1)
            {
                var top = string.Join(", ", c.k.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key}:{Math.Round(kv.Value,2)}"));
                iPlayer.Reply($"Player {c.last_name} ({id}) top: {top}\nHighest range kill: {Math.Round(c.highest_range_kill_m,1)} m");
            }
            else
            {
                var key = args[1];
                c.k.TryGetValue(key, out var v);
                iPlayer.Reply($"{key} = {v}");
            }
        }
        #endregion
    }
}
