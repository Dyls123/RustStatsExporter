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
        // Count each object once per wipe/server run
        private readonly HashSet<uint> _seenHackedCrates = new HashSet<uint>();
        private readonly HashSet<uint> _seenSupplyDrops = new HashSet<uint>();
        // Track craft task start times for accurate duration
        private readonly Dictionary<ItemCraftTask, float> _craftStart = new Dictionary<ItemCraftTask, float>();
        private readonly Dictionary<ItemCraftTask, ulong> _craftOwner = new Dictionary<ItemCraftTask, ulong>();

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
        private static string FarmKeyFromItem(Item item)
        {
            var sn = item?.info?.shortname;
            if (string.IsNullOrEmpty(sn)) return null;

            sn = sn.ToLowerInvariant();
            switch (sn)
            {
                case "corn":    return "farm.corn";
                case "pumpkin": return "farm.pumpkin";
                case "potato":  return "farm.potato";
            }

            if (sn.Contains("berry")) return "farm.berries"; // any berry colour
            if (sn.Contains("hemp") || sn == "cloth") return "farm.hemp"; // hemp seeds/cloth
            return null;
        }
        private static bool IsPlanterEntity(BaseEntity ent)
        {
            if (ent == null) return false;
            if (ent is PlanterBox) return true; // covers square/triangle/single planter variants

            var n = (ent.ShortPrefabName ?? string.Empty).ToLowerInvariant();
            return n.Contains("planter"); // fallback for any planter-shaped prefab
        }
        private bool TryGetGambleGame(BasePlayer player, out string gameKey)
        {
            gameKey = null;
            if (player == null) return false;

            // Check what the player is mounted on (slot machines/card tables use mount points)
            BaseEntity ent = player.GetMounted() as BaseEntity ?? player.GetParentEntity();

            // walk one parent up to catch chair -> table chains
            for (var i = 0; i < 2 && ent != null; i++, ent = ent.GetParentEntity())
            {
                var n = (ent.ShortPrefabName ?? string.Empty).ToLowerInvariant();

                if (n.Contains("slotmachine") || n.Contains("slot_machine"))
                { gameKey = "slots"; return true; }

                if (n.Contains("cardtable") || n.Contains("blackjack"))
                { gameKey = "blackjack"; return true; }

                if (n.Contains("poker"))
                { gameKey = "poker"; return true; }

                if (n.Contains("bigwheel") || n.Contains("wheeloffortune") || n.Contains("spinnerwheel") || n.Contains("wheel"))
                { gameKey = "bigwheel"; return true; }
            }

            return false;
        }
        private void TrackGambleScrap(BasePlayer player, string gameKey, int amount, bool gain)
        {
            if (player == null || string.IsNullOrEmpty(gameKey) || amount <= 0) return;
            var baseKey = $"casino.{gameKey}";

            if (gain)
            {
                Add(player.userID, $"{baseKey}.profit", amount);
            }
            else
            {
                Add(player.userID, $"{baseKey}.spent", amount);
                Add(player.userID, $"{baseKey}.profit", -amount);
            }
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

        private bool ShouldCountPvpShot(BasePlayer player)
        {
            if (player == null) return false;
            var origin = player.eyes?.position ?? player.transform.position;
            var forward = player.eyes?.BodyForward() ?? player.transform.forward;
            const float maxDist = 60f;   // within 60m
            const float minDot = 0.3f;   // roughly 70 degree cone in front

            foreach (var other in BasePlayer.activePlayerList)
            {
                if (other == null || other == player || other.IsNpc || !other.IsAlive()) continue;
                var dir = (other.transform.position - origin);
                var dist = dir.magnitude;
                if (dist > maxDist || dist < 0.1f) continue;
                dir.Normalize();
                var dot = Vector3.Dot(forward, dir);
                if (dot >= minDot) return true;
            }
            return false;
        }

        private string WeaponKeyFromHit(HitInfo info)
        {
            // Prefer item shortname for clarity (e.g., "rifle.ak")
            var wItem = info?.Weapon?.GetItem();
            var key = wItem?.info?.shortname;
            if (!string.IsNullOrEmpty(key))
                return key.ToLowerInvariant();

            // Fallback to weapon prefab name
            var prefab = info?.WeaponPrefab;
            if (prefab != null)
            {
                key = prefab.ShortPrefabName ?? prefab.name;
                if (!string.IsNullOrEmpty(key))
                    return key.ToLowerInvariant();
            }

            return null;
        }

        private void AddWeaponKill(BasePlayer attacker, HitInfo info, string scope)
        {
            if (attacker == null || attacker.userID == 0 || string.IsNullOrEmpty(scope)) return;
            var wKey = WeaponKeyFromHit(info);
            if (string.IsNullOrEmpty(wKey)) return;
            Add(attacker.userID, $"kills.weapon.{scope}.{wKey}");
        }

        private void CountRocketByKind(BasePlayer player, string kind)
        {
            if (player == null || string.IsNullOrEmpty(kind)) return;

            // Always track per-type keys
            Add(player.userID, $"rockets.{kind}");

            // Aggregate total fired across all rocket types for classic UI
            Add(player.userID, "rockets.fired");

            // (Optional) also keep a grand total if you want later:
            // Add(player.userID, "rockets.total");
        }
private static string RocketKindFromEntity(BaseEntity rocket)
{
    if (rocket == null) return null;

    var n = (rocket.ShortPrefabName ?? "").ToLowerInvariant();
    var pn = (rocket.PrefabName ?? "").ToLowerInvariant();

    if (n.Contains("rocket_basic") || pn.Contains("rocket_basic"))        return "basic";
    if (n.Contains("rocket_hv") || pn.Contains("rocket_hv"))              return "hv";
    if (n.Contains("rocket_incendiary") || n.Contains("rocket_fire") || pn.Contains("rocket_fire") || pn.Contains("rocket_incendiary"))
        return "incendiary";
    if (n.Contains("rocket_smoke") || pn.Contains("rocket_smoke"))        return "smoke";

    return null;
}

// ADD
private void OnExplosiveThrown(BasePlayer player, BaseEntity entity)
{
    if (player == null || entity == null) return;
    var name = (entity.ShortPrefabName ?? "").ToLowerInvariant();

    // C4 (Timed Explosive)
    if (name.Contains("explosive.timed"))
    {
        Add(player.userID, "c4.thrown");
        return;
    }

    // Satchel
    if (name.Contains("satchel"))
    {
        Add(player.userID, "satchel.thrown");
        return;
    }

    // Grenades
    if (name.Contains("grenade.f1"))
    {
        Add(player.userID, "grenade.f1.thrown");
        return;
    }
    if (name.Contains("grenade.beancan"))
    {
        Add(player.userID, "grenade.beancan.thrown");
        return;
    }
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
                    Add(id, "playtime.seconds", distIv);

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

        // ---------------- Farming ----------------
        private void CountFarmItem(GrowableEntity plant, BasePlayer player, Item item)
        {
            // Only count harvests from planted crops (i.e., in planter boxes), not wild pickups
            if (player == null || item == null || plant == null) return;
            var planter = plant.GetParentEntity();
            if (!IsPlanterEntity(planter)) return; // ignore wild plants / non-planter parents

            var key = FarmKeyFromItem(item);
            if (key != null && item.amount > 0)
                Add(player.userID, key, item.amount);
        }

        private void OnGrowableGather(GrowableEntity plant, Item item, BasePlayer player)
        {
            CountFarmItem(plant, player, item);
        }

        // Some Oxide builds fire this variant instead
        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player)
        {
            CountFarmItem(plant, player, item);
        }

        // ---------------- Crafting ----------------
        private void OnItemCraft(ItemCraftTask task, BasePlayer crafter, Item item)
        {
            if (task == null) return;
            _craftStart[task] = UnityEngine.Time.realtimeSinceStartup;
            _craftOwner[task] = crafter?.userID ?? 0;
        }

        private void OnItemCraftCancelled(ItemCraftTask task)
        {
            if (task == null) return;
            _craftStart.Remove(task);
            _craftOwner.Remove(task);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            // Attribute to crafter; fall back to item owner if needed
            ulong ownerId = 0;
            if (task != null && _craftOwner.TryGetValue(task, out var uid))
            {
                ownerId = uid;
                _craftOwner.Remove(task);
            }
            if (ownerId == 0)
                ownerId = item?.GetOwnerPlayer()?.userID ?? 0;
            if (ownerId == 0) return;

            // Prefer measured elapsed time from start hook; fallback to blueprint estimate
            float totalSeconds = 0f;
            if (task != null && _craftStart.TryGetValue(task, out var start))
            {
                totalSeconds = Mathf.Max(0f, UnityEngine.Time.realtimeSinceStartup - start);
                _craftStart.Remove(task);
            }
            else
            {
                var per = task?.blueprint != null ? Math.Max(0f, task.blueprint.time) : 0f;
                var amt = task != null ? Math.Max(1, task.amount) : 1;
                totalSeconds = per * amt;
            }

            if (totalSeconds > 0f)
                Add(ownerId, "craft.time.seconds", totalSeconds);
        }

        // ---------------- Gambling ----------------
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (item?.info?.shortname != "scrap") return;
            var player =
                container?.playerOwner
                ?? container?.GetOwnerPlayer()
                ?? container?.entityOwner as BasePlayer;
            if (player == null) return;

            if (TryGetGambleGame(player, out var gameKey))
                TrackGambleScrap(player, gameKey, item.amount, true);
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (item?.info?.shortname != "scrap") return;
            var player =
                container?.playerOwner
                ?? container?.GetOwnerPlayer()
                ?? container?.entityOwner as BasePlayer;
            if (player == null) return;

            if (TryGetGambleGame(player, out var gameKey))
                TrackGambleScrap(player, gameKey, item.amount, false);
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
                {
                    Add(attacker.userID, "bradley.destroyed");
                    AddWeaponKill(attacker, info, "events");
                }
                return;
            }
            if (victim is BaseHelicopter)
            {
                // On some servers the final explosion sets InitiatorPlayer null; fall back to the last real attacker
                if (attacker != null && attacker.userID != 0 && !attacker.IsNpc)
                {
                    Add(attacker.userID, "heli.destroyed");
                    AddWeaponKill(attacker, info, "events");
                }
                else if (info != null && info.Initiator != null)
                {
                    var owner = info.Initiator.OwnerID;
                    if (owner != 0)
                    {
                        Add(owner, "heli.destroyed");
                    }
                }
                return;
            }

    // ----- Players (human) -----
    if (victim is BasePlayer vp)
    {
        // NPC player
        if (vp.IsNpc)
        {
            if (attacker != null && attacker.userID != 0 && attacker.userID != vp.userID)
            {
                // scientist sub-types by prefab name
                var n = (vp.ShortPrefabName ?? "").ToLowerInvariant();

                if (n.Contains("tunneldweller"))
                {
                    Add(attacker.userID, "kills.tunneldweller");
                }
                else if (n.Contains("excavator"))
                {
                    Add(attacker.userID, "kills.scientist.excavator");
                }
                else if (n.Contains("ch47"))
                {
                    Add(attacker.userID, "kills.scientist.ch47");
                }
                else if (n.Contains("cargo"))
                {
                    Add(attacker.userID, "kills.scientist.cargo");
                }
                else if (n.Contains("oil") || n.Contains("heavyscientist") || n.Contains("oilrig"))
                {
                    Add(attacker.userID, "kills.scientist.oilrig");
                }
                else if (n.Contains("scientist"))
                {
                    Add(attacker.userID, "kills.scientist.normal");
                }
                else
                {
                    // fallback bucket (any other humanoid NPC)
                    Add(attacker.userID, "kills.npc");
                }
                AddWeaponKill(attacker, info, "scientist");
            }
            return;
        }

        // Real players
        if (!vp.IsNpc) Add(vp.userID, "deaths");
        if (attacker != null && attacker.userID != vp.userID && !attacker.IsNpc)
        {
            Add(attacker.userID, "kills.player");
            AddWeaponKill(attacker, info, "pvp");

            // highest range kill
            try
            {
                var range = info.ProjectileDistance > 0f
                    ? info.ProjectileDistance
                    : Vector3.Distance(attacker.eyes?.position ?? attacker.transform.position, vp.transform.position);

                var c = Get(attacker.userID);
                if (range > c.highest_range_kill_m)
                    c.highest_range_kill_m = range;
            }
            catch { }
        }
        return;
    }

    // ----- Animals (per-species buckets) -----
    if (attacker != null)
    {
        var n = (victim.ShortPrefabName ?? "").ToLowerInvariant();
        if      (n.Contains("chicken")) Add(attacker.userID, "kills.animal.chicken");
        else if (n.Contains("boar"))    Add(attacker.userID, "kills.animal.boar");
        else if (n.Contains("horse"))   Add(attacker.userID, "kills.animal.horse");
        else if (n.Contains("stag") || n.Contains("deer"))
                                      Add(attacker.userID, "kills.animal.stag");
        else if (n.Contains("wolf"))    Add(attacker.userID, "kills.animal.wolf");
        else if (n.Contains("bear"))    Add(attacker.userID, "kills.animal.bear");
        else if (n.Contains("tiger"))   Add(attacker.userID, "kills.animal.tiger");
        else if (n.Contains("panther")) Add(attacker.userID, "kills.animal.panther");
        else if (n.Contains("croc") || n.Contains("crocodile"))
                                      Add(attacker.userID, "kills.animal.crocodile");
        else if (n.Contains("snake"))   Add(attacker.userID, "kills.animal.snake");
        else
        {
            // your previous generic buckets (optional)
            if (n.Contains("scientist") || n.Contains("tunneldweller") || n.Contains("bandit"))
                Add(attacker.userID, "kills.npc");
            else
                Add(attacker.userID, "kills.animal");
        }

        // weapon stats for PvE buckets (animals/NPcs/vehicles)
        AddWeaponKill(attacker, info, "pve");
    }
}

        // Explicit heli kill hook (some builds fire this instead of OnEntityDeath)
        private void OnHelicopterKilled(BaseHelicopter heli, HitInfo info)
        {
            var attacker = info?.InitiatorPlayer;
            if (attacker != null && attacker.userID != 0 && !attacker.IsNpc)
            {
                Add(attacker.userID, "heli.destroyed");
                AddWeaponKill(attacker, info, "events");
                return;
            }

            // fallback: attribute to owner of the damaging entity (e.g., turret/rocket)
            var owner = info?.Initiator?.OwnerID ?? 0;
            if (owner != 0)
            {
                Add(owner, "heli.destroyed");
                if (attacker == null || attacker.userID == 0) // we don't have a player, but could still log weapon by prefab
                {
                    var pseudo = BasePlayer.FindByID(owner);
                    if (pseudo != null)
                        AddWeaponKill(pseudo, info, "events");
                }
            }
        }

private object OnWeaponFired(BaseProjectile proj, BasePlayer player)
{
    if (player == null || proj == null) return null;

    var ammo = proj.primaryMagazine?.ammoType?.shortname ?? "";
    var sa = ammo.ToLowerInvariant();

    // Rockets via ammo type
    if (sa.Contains("rocket"))
    {
        // handled in OnRocketLaunched to avoid double-counting
        return null;
    }

    // Explosive bullets
    if (sa.Contains("explosive"))
    {
        Add(player.userID, "bullets.explosive"); // for "Explosives" tab
        return null;
    }

    // Non-explosive ammo buckets (for PvP tab)
    var bucket = AmmoBucket(ammo); // your existing helper
    if (bucket == "rocket") return null; // rockets handled above
    Add(player.userID, $"bullets.{bucket}");
    if (ShouldCountPvpShot(player))
        Add(player.userID, "pvp.shots.fired"); // only count shots taken near/at other players

    // Arrows (some bows report ‘arrow’ ammo types)
    if (sa.Contains("arrow") || sa.Contains("bolt"))
        Add(player.userID, "bullets.arrow");

    return null;
}

// Track hits/headshots against players for accuracy
private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
{
    if (attacker == null || info == null) return;
    var victim = info.HitEntity as BasePlayer;
    if (victim == null || victim.IsNpc || victim.userID == attacker.userID) return; // only PvP hits

    // Skip melee for this accuracy stat; focus on projectile-type damage
    var dmgType = info.damageTypes?.GetMajorityDamageType() ?? Rust.DamageType.Generic;
    if (dmgType == Rust.DamageType.Blunt || dmgType == Rust.DamageType.Slash || dmgType == Rust.DamageType.Stab)
        return;

    Add(attacker.userID, "pvp.shots.hit");
    if (info.isHeadshot) Add(attacker.userID, "pvp.shots.head");
}

private void OnRocketLaunched(BasePlayer player, BaseEntity rocket)
{
    var kind = RocketKindFromEntity(rocket) ?? "basic";

    // Prefer explicit player from hook
    if (player != null)
    {
        CountRocketByKind(player, kind);
        return;
    }

    // Fallback: attribute to owner of the rocket entity if available
    var ownerId = rocket?.OwnerID ?? 0;
    if (ownerId != 0)
    {
        CountRocketByKind(BasePlayer.FindByID(ownerId), kind);
    }
    else
    {
        // worst case: no owner/player, do nothing to avoid misattribution
    }
}




        // ---------------- Looting (Airdrops / Hacked crates) ----------------
        private void OnLootEntityEnd(BasePlayer p, BaseEntity ent)
{
    if (p == null || ent == null || ent.net == null) return;

    uint id = (uint)ent.net.ID.Value; // unique per spawned entity
    var name = (ent.ShortPrefabName ?? string.Empty).ToLowerInvariant();

    // Airdrops — count only once per supply drop
    if (name == "supply_drop" || name.Contains("supply_drop"))
    {
        if (_seenSupplyDrops.Add(id))
            Add(p.userID, "airdrops.collected");
        return;
    }

    // Hacked (locked) crates — count only once per crate
    // Prefer class check when available; keep name check as fallback
    if (ent is HackableLockedCrate || name.Contains("hackable") || name.Contains("hackablecrate") || name.Contains("crate_elite_hackable"))
    {
        if (_seenHackedCrates.Add(id))
            Add(p.userID, "hackedcrates.collected");
        return;
    }
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
