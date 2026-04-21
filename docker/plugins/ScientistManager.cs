// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using System.Collections.Generic;
using System.Text;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ScientistManager", "PenguinzPlays", "1.0.0")]
    [Description("Location-based rules for scientist NPCs: replace type, control friendliness, override health, and customise drops")]
    class ScientistManager : RustPlugin
    {
        // ── Permissions ───────────────────────────────────────────────────────
        const string PermAdmin = "scientistmanager.admin";

        // ── Prefab paths — verify at runtime with 'scientist.prefabs' ─────────
        const string PrefabScientist      = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc.prefab";
        const string PrefabHeavyScientist = "assets/rust.ai/agents/npcplayer/humannpc/heavyscientist/heavyscientist.prefab";
        const string PrefabTunnelDweller  = "assets/rust.ai/agents/npcplayer/humannpc/tunneldweller/tunneldweller.prefab";
        const string PrefabUnderwater     = "assets/rust.ai/agents/npcplayer/humannpc/underwaterdweller/underwaterdweller.prefab";

        // ── Type name constants (used in config) ──────────────────────────────
        const string TypeScientist      = "Scientist";
        const string TypeHeavyScientist = "HeavyScientist";
        const string TypeTunnelDweller  = "TunnelDweller";
        const string TypeUnderwater     = "UnderwaterDweller";

        // ── State ─────────────────────────────────────────────────────────────
        readonly Dictionary<MonumentInfo, List<ScientistRule>> _monumentRules =
            new Dictionary<MonumentInfo, List<ScientistRule>>();

        readonly Dictionary<NetworkableId, ScientistRule> _managedNpcs =
            new Dictionary<NetworkableId, ScientistRule>();

        // Keyed by Unity GetInstanceID() — assigned before Spawn(), so safe to
        // check inside OnEntitySpawned which fires synchronously during Spawn().
        readonly HashSet<int> _pendingReplacements = new HashSet<int>();

        readonly HashSet<NetworkableId> _provoked = new HashSet<NetworkableId>();

        // Scientist netId → (rule, world position) — consumed when corpse spawns.
        readonly Dictionary<NetworkableId, PendingLootEntry> _pendingLoot =
            new Dictionary<NetworkableId, PendingLootEntry>();

        struct PendingLootEntry
        {
            public ScientistRule Rule;
            public Vector3       Position;
        }

        PluginConfig _config;

        // ── Nested config classes ─────────────────────────────────────────────

        #region Configuration

        class PluginConfig
        {
            public List<ScientistRule> Rules = new List<ScientistRule>();
        }

        class ScientistRule
        {
            public string       Name                  = "";
            public string       MonumentFilter        = "";
            public List<string> AffectTypes           = new List<string>();
            public string       ReplaceWithType       = "";
            public bool         Friendly              = false;
            public bool         FriendlyUntilAttacked = false;
            public float        HealthOverride        = 0f;
            public bool         DropWeaponOnGround    = false;
            public string       LootMode              = "None"; // None | Append | Replace
            public List<LootEntry> Loot               = new List<LootEntry>();
        }

        class LootEntry
        {
            public string ShortName  = "";
            public int    MinAmount  = 1;
            public int    MaxAmount  = 1;
            public float  Chance     = 1.0f;
            public ulong  SkinId     = 0;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig
            {
                Rules = new List<ScientistRule>
                {
                    new ScientistRule
                    {
                        Name              = "Example - Oil Rig Heavy Scientists",
                        MonumentFilter    = "oil_rig",
                        AffectTypes       = new List<string> { TypeScientist },
                        ReplaceWithType   = TypeHeavyScientist,
                        Friendly          = false,
                        FriendlyUntilAttacked = false,
                        HealthOverride    = 0f,
                        DropWeaponOnGround = false,
                        LootMode          = "None",
                        Loot              = new List<LootEntry>()
                    },
                    new ScientistRule
                    {
                        Name              = "Example - Passive Junkpile Scientists",
                        MonumentFilter    = "junkpile",
                        AffectTypes       = new List<string>(),
                        ReplaceWithType   = "",
                        Friendly          = true,
                        FriendlyUntilAttacked = true,
                        HealthOverride    = 80f,
                        DropWeaponOnGround = true,
                        LootMode          = "Replace",
                        Loot              = new List<LootEntry>
                        {
                            new LootEntry { ShortName = "scrap",           MinAmount = 5, MaxAmount = 20, Chance = 1.0f },
                            new LootEntry { ShortName = "pistol.revolver", MinAmount = 1, MaxAmount = 1,  Chance = 0.3f }
                        }
                    }
                }
            };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
            if (_config?.Rules == null)
            {
                _config = new PluginConfig();
                SaveConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
        }

        void OnServerInitialized()
        {
            if (_config.Rules == null || _config.Rules.Count == 0)
            {
                Puts("No rules configured — plugin is passive.");
                return;
            }

            BuildMonumentCache();
            Puts($"Ready — {_config.Rules.Count} rule(s), {_monumentRules.Count} monument(s) matched.");
        }

        void Unload()
        {
            _monumentRules.Clear();
            _managedNpcs.Clear();
            _pendingReplacements.Clear();
            _provoked.Clear();
            _pendingLoot.Clear();
        }

        void BuildMonumentCache()
        {
            _monumentRules.Clear();

            var allMonuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
            foreach (var monument in allMonuments)
            {
                var objName = monument.name;
                if (objName.Contains("monument_marker") ||
                    objName.Contains("prevent_building_monument_"))
                    continue;

                var displayName = monument.displayPhrase?.english?.Trim();
                if (string.IsNullOrEmpty(displayName))
                    displayName = objName;

                for (int i = 0; i < _config.Rules.Count; i++)
                {
                    var rule = _config.Rules[i];
                    if (string.IsNullOrEmpty(rule.MonumentFilter)) continue;

                    bool nameMatch    = objName.IndexOf(rule.MonumentFilter,
                                            StringComparison.OrdinalIgnoreCase) >= 0;
                    bool displayMatch = displayName.IndexOf(rule.MonumentFilter,
                                            StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch && !displayMatch) continue;

                    List<ScientistRule> list;
                    if (!_monumentRules.TryGetValue(monument, out list))
                    {
                        list = new List<ScientistRule>();
                        _monumentRules[monument] = list;
                    }
                    list.Add(rule);
                    Puts($"  Rule '{rule.Name}' → monument '{displayName}'");
                }
            }
        }

        // ── Hooks ─────────────────────────────────────────────────────────────

        void OnEntitySpawned(BaseNetworkable networkable)
        {
            // ── NPCPlayerCorpse path — apply pending loot rule ────────────────
            var corpse = networkable as NPCPlayerCorpse;
            if (corpse != null)
            {
                ApplyPendingLoot(corpse);
                return;
            }

            // ── Scientist spawn path ──────────────────────────────────────────
            var scientist = networkable as ScientistNPC;
            if (scientist == null) return;

            // Break replacement spawn loop — instance ID is set before Spawn().
            if (_pendingReplacements.Contains(scientist.GetInstanceID())) return;

            if (_monumentRules.Count == 0) return;
            if (scientist.net == null) return;

            var matchedRule = FindMatchingRule(scientist);
            if (matchedRule == null) return;

            ApplyRule(scientist, matchedRule);
        }

        object OnNpcTarget(BaseCombatEntity entity, BasePlayer target)
        {
            if (entity?.net == null || target == null) return null;

            ScientistRule rule;
            if (!_managedNpcs.TryGetValue(entity.net.ID, out rule)) return null;

            if (rule.Friendly) return true;

            if (rule.FriendlyUntilAttacked && !_provoked.Contains(entity.net.ID))
                return true;

            return null;
        }

        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null || info == null) return;

            ScientistRule rule;
            if (!_managedNpcs.TryGetValue(entity.net.ID, out rule)) return;
            if (!rule.FriendlyUntilAttacked) return;

            if (info.InitiatorPlayer != null)
                _provoked.Add(entity.net.ID);
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null) return;
            var netId = entity.net.ID;

            ScientistRule rule;
            if (!_managedNpcs.TryGetValue(netId, out rule))
            {
                _managedNpcs.Remove(netId);
                _provoked.Remove(netId);
                return;
            }

            // Drop held weapon on ground before corpse is created.
            if (rule.DropWeaponOnGround)
            {
                var heldItem = entity.GetActiveItem();
                if (heldItem != null)
                {
                    heldItem.RemoveFromContainer();
                    heldItem.Drop(
                        entity.transform.position + Vector3.up * 0.5f,
                        Vector3.up * 2f + UnityEngine.Random.insideUnitSphere
                    );
                }
            }

            // Queue loot modification for when the corpse spawns.
            if (!string.Equals(rule.LootMode, "None", StringComparison.OrdinalIgnoreCase) &&
                rule.Loot != null && (rule.LootMode != "Append" || rule.Loot.Count > 0))
            {
                var entry = new PendingLootEntry
                {
                    Rule     = rule,
                    Position = entity.transform.position
                };
                _pendingLoot[netId] = entry;

                // Prune stale entry if no corpse appears within 10 seconds.
                var keyCapture = netId;
                timer.Once(10f, () => _pendingLoot.Remove(keyCapture));
            }

            _managedNpcs.Remove(netId);
            _provoked.Remove(netId);
        }

        void OnEntityKill(BaseNetworkable networkable)
        {
            if (networkable?.net == null) return;
            var netId = networkable.net.ID;
            _managedNpcs.Remove(netId);
            _provoked.Remove(netId);
            _pendingLoot.Remove(netId);
        }

        // ── Apply Logic ───────────────────────────────────────────────────────

        ScientistRule FindMatchingRule(ScientistNPC scientist)
        {
            var pos = scientist.transform.position;

            foreach (var kvp in _monumentRules)
            {
                var monument = kvp.Key;
                if (monument == null || !monument.IsInBounds(pos)) continue;

                var rules = kvp.Value;
                for (int i = 0; i < rules.Count; i++)
                {
                    if (RuleMatchesType(scientist, rules[i]))
                        return rules[i];
                }
            }
            return null;
        }

        void ApplyRule(ScientistNPC scientist, ScientistRule rule)
        {
            string currentType = GetScientistTypeString(scientist);
            bool needsReplacement = !string.IsNullOrEmpty(rule.ReplaceWithType) &&
                                    !string.Equals(rule.ReplaceWithType, currentType,
                                        StringComparison.OrdinalIgnoreCase);

            if (needsReplacement)
            {
                string prefab = GetPrefabForType(rule.ReplaceWithType);
                if (prefab == null)
                {
                    PrintWarning($"Unknown ReplaceWithType '{rule.ReplaceWithType}' in rule '{rule.Name}'. " +
                                 "Run 'scientist.prefabs' to see valid prefab paths.");
                    return;
                }

                var pos      = scientist.transform.position;
                var rot      = scientist.transform.rotation;
                var ruleRef  = rule;

                scientist.Kill();

                NextTick(() =>
                {
                    if (!this.IsLoaded) return;

                    var newEntity = GameManager.server.CreateEntity(prefab, pos, rot);
                    if (newEntity == null)
                    {
                        PrintWarning($"CreateEntity failed for '{prefab}' (rule '{ruleRef.Name}'). " +
                                     "Run 'scientist.prefabs' to verify the prefab path.");
                        return;
                    }

                    _pendingReplacements.Add(newEntity.GetInstanceID());
                    newEntity.Spawn();
                    _pendingReplacements.Remove(newEntity.GetInstanceID());

                    if (newEntity.IsDestroyed || newEntity.net == null) return;

                    var newScientist = newEntity as ScientistNPC;
                    if (newScientist == null) return;

                    ApplyHealthAndRegister(newScientist, ruleRef);
                });
            }
            else
            {
                ApplyHealthAndRegister(scientist, rule);
            }
        }

        void ApplyHealthAndRegister(ScientistNPC scientist, ScientistRule rule)
        {
            if (scientist == null || scientist.IsDestroyed || scientist.net == null) return;

            if (rule.HealthOverride > 0f)
                scientist.InitializeHealth(rule.HealthOverride, rule.HealthOverride);

            bool needsTracking = rule.Friendly || rule.FriendlyUntilAttacked ||
                                 rule.DropWeaponOnGround ||
                                 !string.Equals(rule.LootMode, "None",
                                     StringComparison.OrdinalIgnoreCase);

            if (needsTracking)
                _managedNpcs[scientist.net.ID] = rule;
        }

        void ApplyPendingLoot(NPCPlayerCorpse corpse)
        {
            if (_pendingLoot.Count == 0) return;

            var pos = corpse.transform.position;
            var bestKey  = default(NetworkableId);
            var bestDist = 3f;

            foreach (var kvp in _pendingLoot)
            {
                float d = Vector3.Distance(kvp.Value.Position, pos);
                if (d < bestDist) { bestDist = d; bestKey = kvp.Key; }
            }

            if (bestKey == default(NetworkableId)) return;

            var entry = _pendingLoot[bestKey];
            _pendingLoot.Remove(bestKey);

            var rule = entry.Rule;

            if (corpse.containers == null || corpse.containers.Length == 0) return;
            var container = corpse.containers[0];

            if (string.Equals(rule.LootMode, "Replace", StringComparison.OrdinalIgnoreCase))
            {
                container.Clear();
                ItemManager.DoRemoves();
            }

            if (rule.Loot == null) return;

            for (int i = 0; i < rule.Loot.Count; i++)
            {
                var loot = rule.Loot[i];
                if (loot.Chance < 1f && UnityEngine.Random.value > loot.Chance) continue;

                var def = ItemManager.FindItemDefinition(loot.ShortName);
                if (def == null)
                {
                    PrintWarning($"Unknown item shortname '{loot.ShortName}' in rule '{rule.Name}'.");
                    continue;
                }

                int amt  = UnityEngine.Random.Range(loot.MinAmount, loot.MaxAmount + 1);
                var item = ItemManager.Create(def, amt, loot.SkinId);
                if (!item.MoveToContainer(container))
                    item.Remove();
            }
        }

        // ── Console Commands ──────────────────────────────────────────────────

        [ConsoleCommand("scientist.list")]
        void CcList(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;

            if (_managedNpcs.Count == 0)
            {
                a.ReplyWith("ScientistManager: no managed NPCs currently alive.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"ScientistManager: {_managedNpcs.Count} managed NPC(s):");

            foreach (var kvp in _managedNpcs)
            {
                var netId    = kvp.Key;
                var rule     = kvp.Value;
                bool provoked = _provoked.Contains(netId);

                var entity   = BaseNetworkable.serverEntities.Find(netId);
                string posStr = entity != null
                    ? entity.transform.position.ToString()
                    : "(dead/missing)";
                var combat   = entity as BaseCombatEntity;
                string hpStr = combat != null ? $"{combat.health:F0}hp" : "?";

                sb.AppendLine($"  [{netId.Value}] rule='{rule.Name}' " +
                              $"friendly={rule.Friendly} fua={rule.FriendlyUntilAttacked} " +
                              $"provoked={provoked} hp={hpStr} pos={posStr}");
            }

            a.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("scientist.prefabs")]
        void CcPrefabs(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;

            var sb = new StringBuilder();
            sb.AppendLine("ScientistManager — NPC-related prefab paths:");

            foreach (var kvp in StringPool.toString)
            {
                var path = kvp.Value;
                if (path == null) continue;
                if (path.IndexOf("scientist",        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("tunneldweller",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("underwaterdweller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("heavyscientist",    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.AppendLine($"  {path}");
                }
            }

            a.ReplyWith(sb.ToString());
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        bool RuleMatchesType(ScientistNPC scientist, ScientistRule rule)
        {
            if (rule.AffectTypes == null || rule.AffectTypes.Count == 0) return true;

            var typeStr = GetScientistTypeString(scientist);
            for (int i = 0; i < rule.AffectTypes.Count; i++)
            {
                if (string.Equals(rule.AffectTypes[i], typeStr,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Subtypes checked first — they all satisfy `is ScientistNPC`.
        string GetScientistTypeString(ScientistNPC scientist)
        {
            if (scientist is HeavyScientist)    return TypeHeavyScientist;
            if (scientist is TunnelDweller)     return TypeTunnelDweller;
            if (scientist is UnderwaterDweller) return TypeUnderwater;
            return TypeScientist;
        }

        string GetPrefabForType(string typeName)
        {
            switch (typeName)
            {
                case TypeScientist:      return PrefabScientist;
                case TypeHeavyScientist: return PrefabHeavyScientist;
                case TypeTunnelDweller:  return PrefabTunnelDweller;
                case TypeUnderwater:     return PrefabUnderwater;
                default:                 return null;
            }
        }

        bool CheckPerm(ConsoleSystem.Arg a)
        {
            if (a.Connection == null && !a.IsClientside) return true;
            var p = a.Connection?.player as BasePlayer;
            if (p != null && permission.UserHasPermission(p.UserIDString, PermAdmin)) return true;
            a.ReplyWith("No permission.");
            return false;
        }
    }
}
