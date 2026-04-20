// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Pets2", "PenguinzTech", "1.0.0")]
    [Description("Tame animal companions — 1 per player, friendly to all players, command-driven attack")]
    class Pets2 : RustPlugin
    {
        #region Fields
        private static Pets2 _ins = null!;

        /// <summary>Active pets keyed by owner Steam ID.</summary>
        internal Dictionary<ulong, NpcAI> _activePets = new Dictionary<ulong, NpcAI>();

        /// <summary>Timestamp of the last pets.attack console command per player.</summary>
        private readonly Dictionary<ulong, float> _lastAttackCommand = new Dictionary<ulong, float>();

        internal PluginConfig _config = new PluginConfig();

        internal enum Action { Move, Attack, Follow, Idle }
        #endregion

        #region Config
        internal class PluginConfig
        {
            public int   MaxPetsPerPlayer  = 1;
            public float AttackCooldown    = 0.5f;
            public float AttackRange       = 25f;
            public float AiTickRate        = 0.1f;
            public bool  FriendlyToPlayers = true;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            Config.WriteObject(_config, true);
        }

        protected override void LoadDefaultConfig() => _config = new PluginConfig();
        protected override void SaveConfig() => Config.WriteObject(_config, true);
        #endregion

        #region Data Management
        private Dictionary<ulong, PetData> _petData = new Dictionary<ulong, PetData>();

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject("Pets2_data", _petData);

        private void LoadData()
        {
            try
            {
                _petData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PetData>>("Pets2_data")
                           ?? new Dictionary<ulong, PetData>();
            }
            catch
            {
                _petData = new Dictionary<ulong, PetData>();
            }
        }

        internal class PetData
        {
            public uint  PrefabID;
            public float X, Y, Z;
            public bool  NeedToSpawn;

            public PetData() { NeedToSpawn = true; }

            public PetData(NpcAI pet)
            {
                var pos = pet.entity.transform.position;
                X = pos.x;
                Y = pos.y;
                Z = pos.z;
                PrefabID    = pet.entity.prefabID;
                NeedToSpawn = false;
            }
        }
        #endregion

        #region Oxide Hooks
        private void OnServerInitialized()
        {
            _ins = this;
            LoadData();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                OnPlayerConnected(player);

            Puts("Pets2: players can bind attack with: bind leftalt+a pets.attack");
        }

        private void OnServerSave()
        {
            foreach (var kvp in _activePets)
                _petData[kvp.Key] = new PetData(kvp.Value);
            SaveData();
        }

        private void Unload()
        {
            OnServerSave();
            foreach (var kvp in _activePets)
            {
                if (kvp.Value != null && !kvp.Value.entity.IsDestroyed)
                    UnityEngine.Object.Destroy(kvp.Value);
            }
            _activePets.Clear();
            _ins = null!;
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(2f, () => OnPlayerConnected(player));
                return;
            }

            PetData? info;
            if (!_petData.TryGetValue(player.userID, out info) || info == null || !info.NeedToSpawn)
                return;

            SpawnPetForPlayer(player, info);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity == null) return;

            NpcAI? npcAi = entity.GetComponent<NpcAI>();
            if (npcAi == null) return;

            ulong ownerId = npcAi.ownerPlayerId;
            if (_activePets.ContainsKey(ownerId))
            {
                _activePets.Remove(ownerId);
                // Mark as needing respawn so they get it back on next connect
                if (_petData.ContainsKey(ownerId))
                    _petData[ownerId].NeedToSpawn = true;
            }

            // Notify the owner if they are online
            BasePlayer? owner = BasePlayer.FindByID(ownerId);
            if (owner != null)
                owner.ChatMessage("<color=#939393>Your pet has died!</color>");
        }

        private object? OnNpcTarget(BaseNpc npc, BasePlayer target)
        {
            if (npc == null || target == null) return null;
            if (_config.FriendlyToPlayers)
                return false;
            return null;
        }

        private object? OnNpcAttack(BaseNpc npc, BaseEntity attackEntity)
        {
            if (npc == null || attackEntity == null) return null;
            if (_config.FriendlyToPlayers && attackEntity.ToPlayer() != null)
                return false;
            return null;
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null) return;

            NpcAI? npcAi = entity.GetComponent<NpcAI>();
            if (npcAi == null) return;

            // Do not retaliate against player attackers
            if (hitInfo.Initiator is BasePlayer) return;

            BaseCombatEntity? attacker = hitInfo.Initiator?.GetComponent<BaseCombatEntity>();
            if (attacker != null && !attacker.IsDestroyed && npcAi.currentAction != Action.Attack)
                npcAi.SetTarget(attacker);
        }
        #endregion

        #region Chat Commands
        [ChatCommand("pet")]
        private void CmdPet(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                // Show status
                NpcAI? pet;
                if (_activePets.TryGetValue(player.userID, out pet) && pet != null)
                {
                    float hp = pet.entity?.health ?? 0f;
                    string prefab = pet.entity?.ShortPrefabName ?? "unknown";
                    player.ChatMessage($"<color=#ce422b>Pets2</color><color=#939393>: Your pet is a {prefab} with {Math.Round(hp, 1)} HP.</color>");
                }
                else
                {
                    player.ChatMessage("<color=#939393>You don't currently have a pet.</color>");
                }
                return;
            }

            if (args[0].ToLower() == "release")
            {
                ReleasePet(player);
                return;
            }

            player.ChatMessage("<color=#939393>Usage: /pet — show status | /pet release — dismiss pet</color>");
        }
        #endregion

        #region Console Commands
        [ConsoleCommand("pets.attack")]
        private void CmdPetsAttack(ConsoleSystem.Arg arg)
        {
            BasePlayer? player = arg.Player();
            if (player == null) return;

            NpcAI? npcAi;
            if (!_activePets.TryGetValue(player.userID, out npcAi) || npcAi == null)
                return;

            float now = GetCurrentTime();
            float lastTime;
            if (_lastAttackCommand.TryGetValue(player.userID, out lastTime) && now - lastTime < _config.AttackCooldown)
                return;

            _lastAttackCommand[player.userID] = now;

            Vector3 origin    = player.eyes.position;
            Vector3 direction = Quaternion.Euler(player.serverInput.current.aimAngles) * Vector3.forward;

            BaseCombatEntity? hitEntity = DoSphereCast(origin, direction, 0.5f, _config.AttackRange);
            if (hitEntity != null && !(hitEntity is BasePlayer))
            {
                npcAi.SetTarget(hitEntity);
            }
            else
            {
                player.ChatMessage("<color=#939393>No target found.</color>");
            }
        }
        #endregion

        #region Taming
        /// <summary>
        /// Called when a player tries to tame a wild NPC. Entry point for taming logic.
        /// Can be triggered by external hooks or tests. Returns false if taming failed
        /// (player already has a pet or NPC already owned).
        /// </summary>
        internal bool TryTame(BasePlayer player, BaseNpc npc)
        {
            if (player == null || npc == null) return false;

            // One pet per player hard limit
            if (_activePets.ContainsKey(player.userID))
            {
                player.ChatMessage("<color=#939393>You already have a pet. Use /pet release to dismiss it first.</color>");
                return false;
            }

            // Ensure NPC is not already someone else's pet
            if (npc.GetComponent<NpcAI>() != null)
            {
                player.ChatMessage("<color=#939393>This animal is already someone's pet.</color>");
                return false;
            }

            NpcAI npcAi = npc.gameObject.AddComponent<NpcAI>();
            npcAi.ownerPlayerId = player.userID;
            npcAi.plugin        = this;
            npcAi.entity        = npc; // Awake() does this in Unity; explicit here so stubs work

            _activePets[player.userID] = npcAi;
            player.ChatMessage("<color=#939393>You have set this animal as your pet.</color>");
            return true;
        }

        private void ReleasePet(BasePlayer player)
        {
            NpcAI? pet;
            if (!_activePets.TryGetValue(player.userID, out pet) || pet == null)
            {
                player.ChatMessage("<color=#939393>You don't currently have a pet.</color>");
                return;
            }

            _activePets.Remove(player.userID);

            if (pet.entity != null && !pet.entity.IsDestroyed)
                UnityEngine.Object.Destroy(pet);

            player.ChatMessage("<color=#939393>You released your pet.</color>");
        }
        #endregion

        #region Helpers
        private void SpawnPetForPlayer(BasePlayer player, PetData info)
        {
            Puts($"Pets2: Loading pet for player {player.userID}...");
            BaseEntity? pet = InstantiateEntity(StringPool.Get(info.PrefabID), new Vector3(info.X, info.Y, info.Z), new Quaternion());
            if (pet == null) return;

            pet.enableSaving = false;
            pet.Spawn();

            BaseNpc? npc = pet.GetComponent<BaseNpc>();
            if (npc == null) return;

            NpcAI npcAi = pet.gameObject.AddComponent<NpcAI>();
            npcAi.ownerPlayerId = player.userID;
            npcAi.plugin        = this;

            _activePets[player.userID] = npcAi;
            info.NeedToSpawn = false;
        }

        private static BaseEntity? InstantiateEntity(string type, Vector3 position, Quaternion rotation)
        {
            var go = Facepunch.Instantiate.GameObject(
                GameManager.server.FindPrefab(type), position, rotation);

            if (go == null) return null;

            go.name = type;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(
                go, Rust.Server.EntityScene);
            UnityEngine.Object.Destroy(go.GetComponent<Spawnable>());

            if (!go.activeSelf)
                go.SetActive(true);

            return go.GetComponent<BaseEntity>();
        }

        /// <summary>Virtual so tests can inject time without Unity.</summary>
        internal virtual float GetCurrentTime() => Time.realtimeSinceStartup;

        /// <summary>Virtual so tests can inject sphere-cast results.</summary>
        internal virtual BaseCombatEntity? DoSphereCast(Vector3 origin, Vector3 direction, float radius, float distance)
        {
            RaycastHit hit;
            if (Physics.SphereCast(origin, radius, direction, out hit, distance))
                return hit.GetEntity()?.GetComponent<BaseCombatEntity>();
            return null;
        }
        #endregion

        #region Components
        internal class NpcAI : MonoBehaviour
        {
            public ulong    ownerPlayerId;
            public Pets2    plugin = null!;
            public BaseNpc  entity = null!;
            internal Action currentAction = Action.Idle;
            internal BaseCombatEntity? targetEnt;

            private float _nextAiTick;

            private void Awake()
            {
                entity = GetComponent<BaseNpc>();
                currentAction = Action.Idle;
                _nextAiTick   = 0f;
            }

            private void OnDestroy()
            {
                if (plugin != null && plugin._activePets.ContainsKey(ownerPlayerId))
                    plugin._activePets.Remove(ownerPlayerId);
            }

            private void Update()
            {
                // Throttle to AiTickRate (default 10 Hz)
                float now = Time.realtimeSinceStartup;
                if (now < _nextAiTick) return;
                _nextAiTick = now + plugin._config.AiTickRate;

                if (entity == null || entity.IsDestroyed) return;

                switch (currentAction)
                {
                    case Action.Idle:
                    case Action.Follow:
                        DoFollow();
                        break;

                    case Action.Move:
                        DoMove();
                        break;

                    case Action.Attack:
                        DoAttack();
                        break;
                }
            }

            private void DoFollow()
            {
                BasePlayer? owner = BasePlayer.FindByID(ownerPlayerId);
                if (owner == null) return;

                SetBehaviour(BaseNpc.Behaviour.Idle);
                float dist = Vector3.Distance(transform.position, owner.transform.position);
                if (dist > 3f)
                    UpdateDestination(owner.transform.position, dist > 10f);
                else
                    StopMoving();
            }

            private void DoMove()
            {
                if (targetEnt == null)
                {
                    currentAction = Action.Idle;
                    return;
                }
                float dist = Vector3.Distance(transform.position, targetEnt.transform.position);
                if (dist < 1f)
                    currentAction = Action.Idle;
                else
                {
                    SetBehaviour(BaseNpc.Behaviour.Wander);
                    UpdateDestination(targetEnt.transform.position, dist > 5f);
                }
            }

            private void DoAttack()
            {
                if (targetEnt == null || targetEnt.IsDestroyed)
                {
                    currentAction = Action.Idle;
                    return;
                }

                float dist = Vector3.Distance(transform.position, targetEnt.transform.position);
                if (entity.AttackTarget != targetEnt)
                    entity.AttackTarget = targetEnt;

                SetBehaviour(BaseNpc.Behaviour.Attack);

                if (dist <= entity.AttackRange)
                    entity.StartAttack();
                else
                    UpdateDestination(targetEnt.transform.position, true);
            }

            /// <summary>Set a new attack target.</summary>
            internal void SetTarget(BaseCombatEntity target)
            {
                if (target == null) return;
                targetEnt     = target;
                currentAction = Action.Attack;
                entity.AttackTarget = target;
            }

            internal void StopMoving()
            {
                entity.IsStopped    = true;
                entity.ChaseTransform = null;
                entity.SetFact(BaseNpc.Facts.PathToTargetStatus, 0, true, true);
            }

            internal void UpdateDestination(Vector3 position, bool run)
            {
                entity.UpdateDestination(position);
                entity.TargetSpeed = run ? entity.Stats.Speed : entity.Stats.Speed * 0.3f;
            }

            internal void SetBehaviour(BaseNpc.Behaviour behaviour)
            {
                if (entity.CurrentBehaviour != behaviour)
                    entity.CurrentBehaviour = behaviour;
            }
        }

        /// <summary>
        /// Passive controller component attached to the pet NPC's owner player.
        /// Carries a reference back to the NpcAI for convenience; commands come
        /// via ConsoleCommand, not per-frame Update polling.
        /// </summary>
        internal class NPCController : MonoBehaviour
        {
            public BasePlayer player = null!;
            public NpcAI?     npcAi;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
            }
        }
        #endregion
    }
}
