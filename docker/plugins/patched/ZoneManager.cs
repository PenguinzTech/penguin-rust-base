// PATCHED by penguin-rust-base: fixed removed Oxide APIs (arg.Player()→arg.Connection?.player, userID.Get()→userID)

﻿using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Oxide.Plugins
{
    [Info("Zone Manager", "k1lly0u", "3.1.10")]
    [Description("An advanced management system for creating in-game zones")]
    public class ZoneManager : RustPlugin
    {
        #region Fields
        [PluginReference] Plugin Backpacks, PopupNotifications, Spawns;

        private StoredData storedData;

        private DynamicConfigFile data;


        private readonly Hash<string, Zone> zones = new Hash<string, Zone>();
        private readonly Hash<Plugin, HashSet<string>> temporaryZones = new Hash<Plugin, HashSet<string>>();

        private static readonly Hash<ulong, EntityZones> zonedPlayers = new Hash<ulong, EntityZones>();

        private static readonly Hash<NetworkableId, EntityZones> zonedEntities = new Hash<NetworkableId, EntityZones>();

        private readonly Dictionary<ulong, string> lastPlayerZone = new Dictionary<ulong, string>();


        private readonly ZoneFlags globalFlags = new ZoneFlags();

        private readonly ZoneFlags adminBypass = new ZoneFlags();


        private static readonly ZoneFlags tempFlags = new ZoneFlags();

        private static readonly StringBuilder sb = new StringBuilder();


        private bool zonesInitialized = false;


        private static ZoneManager Instance { get; set; }

        private const string PERMISSION_ZONE = "zonemanager.zone";

        private const string PERMISSION_IGNORE_FLAG = "zonemanager.ignoreflag.";

        private const int PLAYER_MASK = 131072;

        private const int TARGET_LAYERS = ~(1 << 10 | 1 << 18 | 1 << 28 | 1 << 29);
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            Instance = this;

            adminBypass.SetFlags(ZoneFlags.NoBuild, ZoneFlags.NoDeploy, ZoneFlags.NoCup, ZoneFlags.NoUpgrade, ZoneFlags.NoChat, ZoneFlags.NoVoice, ZoneFlags.KillSleepers, ZoneFlags.EjectSleepers, ZoneFlags.NoSignUpdates);

            lang.RegisterMessages(Messages, this);
            
            permission.RegisterPermission(PERMISSION_ZONE, this);

            foreach (string flag in ZoneFlags.NameToIndex.Keys)
                permission.RegisterPermission(PERMISSION_IGNORE_FLAG + flag.ToLower(), this);

            LoadData();
        }

        private void OnServerInitialized() => InitializeZones();

        private void OnTerrainInitialized() => InitializeZones();

        private void OnPlayerConnected(BasePlayer player) => updateBehaviour.QueueUpdate(player);

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (!temporaryZones.TryGetValue(plugin, out HashSet<string> set))
                return;

            foreach (string zoneId in set)
            {
                if (!zones.TryGetValue(zoneId, out Zone zone))
                    continue;

                if (zone.definition.Owner != plugin)
                    continue;
            
                zones.Remove(zoneId);

                UnityEngine.Object.DestroyImmediate(zone.gameObject);
                Interface.CallHook("OnZoneErased", zoneId);
            }

            temporaryZones.Remove(plugin);
        }

        private void OnEntityKill(BaseEntity baseEntity)
        {
            if (!baseEntity || !baseEntity.IsValid() || baseEntity.IsDestroyed)
                return;

            if (!zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZones)) 
                return;

            for (int i = entityZones.Zones.Count - 1; i >= 0; i--)
            {
                Zone zone = entityZones.Zones[i];
                if (!zone)
                    continue;

                zone.OnEntityExitZone(baseEntity, false, true);
            }

            zonedEntities.Remove(baseEntity.net.ID);
        }

        private void Unload()
        {
            DestroyUpdateBehaviour();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, ZMUI);

            foreach (KeyValuePair<string, Zone> kvp in zones)
                UnityEngine.Object.DestroyImmediate(kvp.Value.gameObject);

            zones.Clear();
            temporaryZones.Clear();
            zonedPlayers.Clear();
            zonedEntities.Clear();
            
            Instance = null;
            Configuration = null;
        }
        #endregion

        #region UpdateQueue
        private UpdateBehaviour m_UpdateBehaviour;
        
        private UpdateBehaviour updateBehaviour
        {
            get
            {
                if (m_UpdateBehaviour) 
                    return m_UpdateBehaviour;
                
                m_UpdateBehaviour = new GameObject("ZoneManager.UpdateBehaviour").AddComponent<UpdateBehaviour>();

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                    m_UpdateBehaviour.QueueUpdate(player);

                return m_UpdateBehaviour;
            }
        }

        private void DestroyUpdateBehaviour()
        {
            if (updateBehaviour)
                UnityEngine.Object.Destroy(updateBehaviour.gameObject);
        }

        // Queue and check players for new zones and that they are still in old zones. Previously any plugin that put a player to sleep and teleports them out of a zone
        // without calling the OnPlayerSleep hook would bypass a player zone update which would result in players being registered in zones they were no longer in.
        // Options are to either continually check and update players, or have every plugin that teleports players call the hook...
        private class UpdateBehaviour : MonoBehaviour
        {
            private readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();

            private readonly Queue<BasePlayer> playerUpdateQueue = new Queue<BasePlayer>();

            private const float MAX_MS = 0.25f;

            private void OnDestroy()
            {
                playerUpdateQueue.Clear();
            }

            public void QueueUpdate(BasePlayer player)
            {
                if (!playerUpdateQueue.Contains(player))
                    playerUpdateQueue.Enqueue(player);
            }

            public void Reset() => playerUpdateQueue.Clear();

            private void Update()
            {
                if (Time.frameCount % 10 != 0)
                    return;

                sw.Reset();
                sw.Start();

                while (playerUpdateQueue.Count > 0)
                {
                    if (sw.Elapsed.TotalMilliseconds >= MAX_MS)
                    {
                        sw.Stop();
                        return;
                    }

                    BasePlayer player = playerUpdateQueue.Dequeue();
                    if (!player || !player.IsConnected)
                        continue;

                    Instance?.UpdatePlayerZones(player);

                    InvokeHandler.Invoke(this, () => QueueUpdate(player), 2f);
                }
            }
        }
        #endregion

        #region Flag Hooks
        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            if (!planner || !gameObject)
                return;

            BasePlayer player = planner.GetOwnerPlayer();
            if (!player)
                return;

            BaseEntity entity = gameObject.ToBaseEntity();
            if (!entity)
                return;

            if (entity is BuildingBlock block)
            {
                if (!HasPlayerFlag(player, ZoneFlags.NoBuild)) 
                    return;
                
                List<ItemAmount> list = block.BuildCost();

                block.Invoke(() =>
                {
                    for (int i = 0; i < list?.Count; i++)
                    {
                        ItemAmount itemAmount = list[i];
                        player.GiveItem(ItemManager.Create(itemAmount.itemDef, Mathf.Clamp(Mathf.RoundToInt(itemAmount.amount), 1, int.MaxValue)));
                    }

                    if (entity && !entity.IsDestroyed)
                        entity.Kill(BaseNetworkable.DestroyMode.Gib);
                }, 0.1f);

                SendMessage(player, Message("noBuild", player.UserIDString));
            }
            else if (entity is SimpleBuildingBlock)
            {
                if (!HasPlayerFlag(player, ZoneFlags.NoBuild)) 
                    return;
                
                KillEntityAndReturnItem(player, entity, planner.GetItem());
                SendMessage(player, Message("noBuild", player.UserIDString));
            }
            else
            {
                if (entity is BuildingPrivilege)
                {
                    if (!HasPlayerFlag(player, ZoneFlags.NoCup)) 
                        return;
                    
                    KillEntityAndReturnItem(player, entity, planner.GetItem());
                    SendMessage(player, Message("noCup", player.UserIDString));
                }
                else
                {
                    if (!HasPlayerFlag(player, ZoneFlags.NoDeploy)) 
                        return;
                    
                    KillEntityAndReturnItem(player, entity, planner.GetItem());                       
                    SendMessage(player, Message("noDeploy", player.UserIDString));
                }
            }
        }

        private object OnStructureUpgrade(BuildingBlock buildingBlock, BasePlayer player, BuildingGrade.Enum grade)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoUpgrade))
            {
                SendMessage(player, Message("noUpgrade", player.UserIDString));
                return true;
            }
            return null;
        }

        private void OnItemDeployed(Deployer deployer, ItemModDeployable itemModDeployable, BaseEntity deployedEntity) // DoDeploy_Regular
        {
            BasePlayer player = deployer.GetOwnerPlayer();
            if (!player)
                return;

            if (HasPlayerFlag(player, ZoneFlags.NoDeploy))
            {
                KillEntityAndReturnItem(player, deployedEntity, deployer.GetItem());
                SendMessage(player, Message("noDeploy", player.UserIDString));
            }
        }
                
        private void OnItemDeployed(Deployer deployer, BaseEntity parentEntity, BaseEntity deployedEntity) // DoDeploy_Slot
        {
            BasePlayer player = deployer.GetOwnerPlayer();
            if (!player)
                return;

            if (HasPlayerFlag(player, ZoneFlags.NoDeploy))
            {
                KillEntityAndReturnItem(player, deployedEntity, deployer.GetItem());
                SendMessage(player, Message("noDeploy", player.UserIDString));
            }
        }

        private void KillEntityAndReturnItem(BasePlayer player, BaseEntity entity, Item item)
        {
            ItemDefinition itemDefinition = item?.info;
            //int amount = item.amount;
            ulong skin = item?.skin ?? 0UL;

            entity.Invoke(() =>
            {
                if (entity && !entity.IsDestroyed)
                    entity.Kill(BaseNetworkable.DestroyMode.Gib);

                if (itemDefinition)
                    player.GiveItem(ItemManager.Create(itemDefinition, 1, skin));

            }, 0.1f);
        }

        private void OnItemUse(Item item, int amount)
        {
            BaseEntity entity = item?.parent?.entityOwner;
            if (!entity)
                return;

            if (entity is FlameTurret or AutoTurret or GunTrap)
            {
                if (HasEntityFlag(entity, ZoneFlags.InfiniteTrapAmmo))
                    item.amount += amount;
                return;
            }

            if (entity is not SearchLight) 
                return;
            
            if (HasEntityFlag(entity, ZoneFlags.AlwaysLights))
            {
                item.amount += amount;
                return;
            }

            if (!HasEntityFlag(entity, ZoneFlags.AutoLights)) 
                return;
            
            if (TOD_Sky.Instance.Cycle.Hour > Configuration.AutoLights.OnTime || TOD_Sky.Instance.Cycle.Hour < Configuration.AutoLights.OffTime)
                item.amount += amount;
        }
               
        private object OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            if (!player)
                return null;

            if (!HasPlayerFlag(player, ZoneFlags.NoChat)) 
                return null;
            
            SendMessage(player, Message("noChat", player.UserIDString));
            return true;
        }

        private object OnBetterChat(Oxide.Core.Libraries.Covalence.IPlayer iPlayer, string message)
        {
            BasePlayer player = iPlayer.Object as BasePlayer;
            return OnPlayerChat(player, message, Chat.ChatChannel.Global);
        }

        private object OnPlayerVoice(BasePlayer player, Byte[] data)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoVoice))
            {
                SendMessage(player, Message("noVoice", player.UserIDString));
                return true;
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection?.player as BasePlayer;
            if (!player || string.IsNullOrEmpty(arg.cmd?.Name))
                return null;

            if (arg.cmd.Name == "kill" && HasPlayerFlag(player, ZoneFlags.NoSuicide))
            {
                SendMessage(player, Message("noSuicide", player.UserIDString));
                return true;
            }
            return null;
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (!player)
                return;

            if (HasPlayerFlag(player, ZoneFlags.KillSleepers))
            {
                player.Die();
                return;
            }

            if (HasPlayerFlag(player, ZoneFlags.EjectSleepers))
            {
                if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones) || entityZones.Count == 0)
                    return;

                foreach (Zone zone in entityZones.Zones)
                {
                    if (!zone)
                        continue;

                    if (HasFlag(zone, ZoneFlags.EjectSleepers))
                    {
                        EjectPlayer(player, zone);
                        return;
                    }
                }
            }
        }
        
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
        {
            if (!entity || GetEntityComponent<ResourceDispenser>(entity))
                return null;

            BasePlayer attacker = hitinfo.InitiatorPlayer;
            BasePlayer victim = entity as BasePlayer;

            if (victim)
            {                
                if (hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Fall)
                {
                    if (HasPlayerFlag(victim, ZoneFlags.NoFallDamage))
                        return true;
                }

                if (victim.IsSleeping() && HasPlayerFlag(victim, ZoneFlags.SleepGod))
                    return true;

                if (attacker)
                {
                    if (IsNpc(victim))
                        return null;

                    if (HasPlayerFlag(victim, ZoneFlags.PvpGod))
                    {
                        if (attacker == victim && hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Suicide)
                        {
                            if (HasPlayerFlag(victim, ZoneFlags.NoSuicide))
                                return true;
                            return null;
                        }
                        if (IsNpc(attacker) && Configuration.NPCHurtPvpGod)
                            return null;

                        return true;
                    }
                    
                    if (HasPlayerFlag(attacker, ZoneFlags.PvpGod) && !IsNpc(attacker))                    
                        return true;                    
                }
                
                else if (HasPlayerFlag(victim, ZoneFlags.PveGod) && !IsNpc(victim))
                    return true;
                
                else if (hitinfo.Initiator is FireBall && HasPlayerFlag(victim, ZoneFlags.PvpGod))
                    return true;
                
                return null;
            }

            BaseNpc baseNpc = entity as BaseNpc;
            if (baseNpc)
            {
                if (HasEntityFlag(baseNpc, ZoneFlags.NoPve))
                {
                    if (attacker && CanBypass(attacker, ZoneFlags.NoPve))
                        return null;
                    return true;
                }
                return null;
            }

            if (entity is BuildingBlock or SimpleBuildingBlock)
            {
                if (HasEntityFlag(entity, ZoneFlags.NoBuildingDamage))
                {
                    if (attacker)
                    {
                        if (CanBypass(attacker, ZoneFlags.NoBuildingDamage))
                            return null;

                        if (HasPlayerFlag(attacker, ZoneFlags.NoBuildingDamage))
                            return true;
                    }
                    
                    if (hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Decay && Configuration.DecayDamageUndestr)
                        return null;
                    
                    return true;
                }
            }

            if (entity is not LootContainer && entity is not PatrolHelicopter)
            {
                if (HasEntityFlag(entity, ZoneFlags.UnDestr))
                {
                    if (attacker)
                    {
                        if (CanBypass(attacker, ZoneFlags.UnDestr))
                            return null;

                        if (HasPlayerFlag(attacker, ZoneFlags.UnDestr))
                            return true;
                    }

                    if (hitinfo.damageTypes.GetMajorityDamageType() == DamageType.Decay && Configuration.DecayDamageUndestr)
                        return null;

                    return true;
                }
            }

            return null;
        }

        private void OnEntitySpawned(BaseEntity baseEntity)
            => NextTick(() => CanSpawn(baseEntity));

        private void CanSpawn(BaseEntity baseEntity)
        {
            if (!baseEntity.IsValid() || baseEntity.IsDestroyed)
                return;

            if (Interface.CallHook("CanSpawnInZone", baseEntity) != null)
                return;

            if (baseEntity is BaseCorpse corpse)
            {
                if (HasEntityFlag(corpse, ZoneFlags.NoCorpse) && !CanBypass(corpse.OwnerID, ZoneFlags.NoCorpse))
                    corpse.Invoke(() => baseEntity.Kill(BaseNetworkable.DestroyMode.None), 0.1f);
            }
            if (baseEntity is LootContainer or JunkPile)
            {
                if (HasEntityFlag(baseEntity, ZoneFlags.NoLootSpawns))
                    baseEntity.Invoke(() => baseEntity.Kill(BaseNetworkable.DestroyMode.None), 0.1f);
            }
            else if (baseEntity is BaseNpc or NPCPlayer)
            {
                if (HasEntityFlag(baseEntity, ZoneFlags.NoNPCSpawns))
                    baseEntity.Invoke(() => baseEntity.Kill(BaseNetworkable.DestroyMode.None), 0.1f);
            }
            else if (baseEntity is DroppedItem or WorldItem)
            {
                if (HasEntityFlag(baseEntity, ZoneFlags.NoDrop))
                {
                    ((WorldItem)baseEntity).item.Remove(0f);
                    baseEntity.Invoke(() => baseEntity.Kill(BaseNetworkable.DestroyMode.None), 0.1f);
                }
            }
            else if (baseEntity is DroppedItemContainer)
            {
                if (HasEntityFlag(baseEntity, ZoneFlags.NoDrop))
                    baseEntity.Invoke(() => baseEntity.Kill(BaseNetworkable.DestroyMode.None), 0.1f);
            }
        }

        private object CanBeWounded(BasePlayer player, HitInfo hitinfo) => HasPlayerFlag(player, ZoneFlags.NoWounded) ? (object)false : null;

        private object CanUpdateSign(BasePlayer player, Signage sign)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoSignUpdates))
            {
                SendMessage(player, Message("noSignUpdates", player.UserIDString));
                return false;
            }
            return null;
        }

        private object OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoOvenToggle))
            {
                SendMessage(player, Message("noOvenToggle", player.UserIDString));
                return true;
            }
            return null;
        }

        private object CanUseVending(BasePlayer player, VendingMachine machine)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoVending))
            {
                SendMessage(player, Message("noVending", player.UserIDString));
                return false;
            }
            return null;
        }

        private object CanHideStash(BasePlayer player, StashContainer stash)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoStash))
            {
                SendMessage(player, Message("noStash", player.UserIDString));
                return false;
            }
            return null;
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint bp, int amount)
        {
            BasePlayer player = itemCrafter.GetComponent<BasePlayer>();
            if (player && HasPlayerFlag(player, ZoneFlags.NoCraft))
            {
                SendMessage(player, Message("noCraft", player.UserIDString));
                return false;
            }
            return null;
        }

        private void OnDoorOpened(Door door, BasePlayer player)
        {
            if (HasPlayerFlag(player, ZoneFlags.NoDoorAccess))
            {
                SendMessage(player, Message("noDoor", player.UserIDString));
                door.CloseRequest();
            }
        }

        private object OnSprayCreate(SprayCan sprayCan, Vector3 position, Quaternion rotation)
        {
            if (!sprayCan)
                return null;

            BasePlayer player = sprayCan.GetOwnerPlayer();
            if (!player)
                return null;

            if (HasPlayerFlag(player, ZoneFlags.NoSprays))
            {
                SendMessage(player, Message("nosprays", player.UserIDString));
                return false;
            }

            return null;
        }

        #region Looting Hooks
        private object CanLootPlayer(BasePlayer target, BasePlayer looter) => OnLootPlayerInternal(looter, target);

        private void OnLootPlayer(BasePlayer looter, BasePlayer target) => OnLootPlayerInternal(looter, target);

        private object OnLootPlayerInternal(BasePlayer looter, BasePlayer target)
        {
            if (HasPlayerFlag(looter, ZoneFlags.NoPlayerLoot) || (target != null && HasPlayerFlag(target, ZoneFlags.NoPlayerLoot)))
            {
                if (looter == target && Backpacks != null)
                {
                    object hookResult = Backpacks.Call("CanLootPlayer", target, looter);
                    if (hookResult is bool result && result)
                        return true;
                }

                SendMessage(looter, Message("noLoot", looter.UserIDString));
                NextTick(looter.EndLooting);
                return false;
            }
            return null;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity is LootableCorpse corpse)
                OnLootCorpse(corpse, player);
            if (entity is DroppedItemContainer container)
                OnLootContainer(container, player);
            if (entity is StorageContainer)
                OnLootInternal(player, ZoneFlags.NoBoxLoot);
        }

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            if (corpse is NPCPlayerCorpse)
            {
                if (!HasPlayerFlag(player, ZoneFlags.NoNPCLoot)) 
                    return null;
                
                SendMessage(player, Message("noLoot", player.UserIDString));
                return false;
            }

            if (corpse.playerSteamID == player.userID && HasPlayerFlag(player, ZoneFlags.LootSelf))
                return null;
            
            return CanLootInternal(player, ZoneFlags.NoPlayerLoot);
        }

        private void OnLootCorpse(LootableCorpse corpse, BasePlayer player)
        {
            if (corpse is NPCPlayerCorpse)
            {
                if (!HasPlayerFlag(player, ZoneFlags.NoNPCLoot)) 
                    return;
                
                SendMessage(player, Message("noLoot", player.UserIDString));
                NextTick(player.EndLooting);
                return;
            }

            if (corpse.playerSteamID == player.userID && HasPlayerFlag(player, ZoneFlags.LootSelf))
                return;

            OnLootInternal(player, ZoneFlags.NoPlayerLoot);
        }

        private void OnLootContainer(DroppedItemContainer container, BasePlayer player)
        {
            if (container.playerSteamID == player.userID && HasPlayerFlag(player, ZoneFlags.LootSelf))
                return;

            OnLootInternal(player, ZoneFlags.NoPlayerLoot);
        }

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container)
        {
            if (container.playerSteamID == player.userID && HasPlayerFlag(player, ZoneFlags.LootSelf))
                return null;

            return CanLootInternal(player, ZoneFlags.NoPlayerLoot);
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container) => CanLootInternal(player, ZoneFlags.NoBoxLoot);

        private object CanLootInternal(BasePlayer player, int flag)
        {
            if (!player || !HasPlayerFlag(player, flag)) 
                return null;
            
            SendMessage(player, Message("noLoot", player.UserIDString));
            return false;
        }

        private void OnLootInternal(BasePlayer player, int flag)
        {
            if (!player || !HasPlayerFlag(player, flag)) 
                return;
            
            SendMessage(player, Message("noLoot", player.UserIDString));
            NextTick(player.EndLooting);
        }
        #endregion

        #region Pickup Hooks
        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity) => CanPickupInternal(player, ZoneFlags.NoEntityPickup);

        private object CanPickupLock(BasePlayer player, BaseLock baseLock) => CanPickupInternal(player, ZoneFlags.NoEntityPickup);

        private object OnItemPickup(Item item, BasePlayer player) => CanPickupInternal(player, ZoneFlags.NoPickup);

        private object CanPickupInternal(BasePlayer player, int flag)
        {
            if (!HasPlayerFlag(player, flag)) 
                return null;
            
            SendMessage(player, Message("noPickup", player.UserIDString));
            return false;
        }
        #endregion

        #region Gather Hooks        
        private object CanLootEntity(ResourceContainer container, BasePlayer player) => OnGatherInternal(player);

        private object OnCollectiblePickup(Item item, BasePlayer player) => OnGatherInternal(player);

        private object OnGrowableGather(GrowableEntity plant, Item item, BasePlayer player) => OnGatherInternal(player);

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item) => OnGatherInternal(player);

        private object OnGatherInternal(BasePlayer player)
        {
            if (!player || !HasPlayerFlag(player, ZoneFlags.NoGather)) 
                return null;
            
            SendMessage(player, Message("noGather", player.UserIDString));
            return true;

        }
        #endregion

        #region Targeting Hooks
        private object OnTurretTarget(AutoTurret turret, BasePlayer player) => OnTargetPlayerInternal(player, ZoneFlags.NoTurretTargeting);

        private object CanBradleyApcTarget(BradleyAPC apc, BasePlayer player)
        {
            if (player && HasPlayerFlag(player, ZoneFlags.NoAPCTargeting))
                return false;
            return null;
        }

        private object CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer player)
        {
            if (!player || !HasPlayerFlag(player, ZoneFlags.NoHeliTargeting)) 
                return null;
            
            heli.interestZoneOrigin = heli.GetRandomPatrolDestination();
            return false;
        }

        private object CanHelicopterStrafeTarget(PatrolHelicopterAI heli, BasePlayer player)
        {
            if (player && HasPlayerFlag(player, ZoneFlags.NoHeliTargeting))
                return false;
            return null;
        }

        private object OnHelicopterTarget(HelicopterTurret turret, BasePlayer player) => OnTargetPlayerInternal(player, ZoneFlags.NoHeliTargeting);

        private object OnNpcTarget(BaseCombatEntity entity, BasePlayer player) => OnTargetPlayerInternal(player, ZoneFlags.NoNPCTargeting);

        private object OnTargetPlayerInternal(BasePlayer player, int flag)
        {
            if (player && HasPlayerFlag(player, flag))
                return true;
            return null;
        }
        #endregion

        #region Mounting Hooks
        private object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (!player || !entity)
                return null;

            if (!entity.VehicleParent())
                return null;

            if (!HasPlayerFlag(player, ZoneFlags.NoVehicleMounting)) 
                return null;
            
            SendMessage(player, Message("novehiclemounting", player.UserIDString));
            return false;
        }

        private object CanDismountEntity(BasePlayer player, BaseMountable entity)
        {
            if (!player || !entity)
                return null;

            if (!entity.VehicleParent())
                return null;

            if (!HasPlayerFlag(player, ZoneFlags.NoVehicleDismounting)) 
                return null;
            
            SendMessage(player, Message("novehicledismounting", player.UserIDString));
            return false;

        }
        #endregion

        #region Additional KillSleeper Checks
        private void OnPlayerSleep(BasePlayer player)
        {
            if (!player)
                return;

            //player.Invoke(()=> UpdatePlayerZones(player), 1f); // Manually update the zones a player is in. Sleeping players don't trigger OnTriggerEnter or OnTriggerExit            

            timer.In(2f, () =>
            {
                if (!player || !player.IsSleeping())
                    return;

                if (player.IsConnected) 
                    return;
                
                if (HasPlayerFlag(player, ZoneFlags.KillSleepers))
                {
                    player.Invoke(() => KillSleepingPlayer(player), 3f);
                    return;
                }

                if (HasPlayerFlag(player, ZoneFlags.EjectSleepers))
                {
                    player.Invoke(() =>
                    {
                        if (!player || !player.IsSleeping())
                            return;

                        if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones) || entityZones.Count == 0)
                            return;

                        foreach (Zone zone in entityZones.Zones)
                        {
                            if (!zone)
                                continue;

                            if (HasFlag(zone, ZoneFlags.EjectSleepers))
                                EjectPlayer(player, zone);
                        }
                    }, 3f);
                }
            });
        }

        private void OnPlayerSleepEnd(BasePlayer player) => updateBehaviour.QueueUpdate(player);

        private void KillSleepingPlayer(BasePlayer player)
        {
            if (!player || !player.IsSleeping())
                return;

            if (!HasPlayerFlag(player, ZoneFlags.KillSleepers)) 
                return;
            
            if (player.IsConnected)
                OnPlayerSleep(player);
            else player.Die();
        }

        private void UpdatePlayerZones(BasePlayer player)
        {
            if (!player)
                return;

            if (zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
            {
                List<Zone> list = Pool.Get<List<Zone>>();
                list.AddRange(entityZones.Zones);
                
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Zone zone = list[i];
                    if (!zone || !zone.definition.Enabled)
                        continue;

                    if (zone.definition.Size != Vector3.zero)
                    {
                        if (!IsInsideBounds(zone, player.transform.position))
                            OnPlayerExitZone(player, zone);
                    }
                    else
                    {
                        if (Vector3.Distance(player.transform.position, zone.transform.position) > zone.definition.Radius)
                            OnPlayerExitZone(player, zone);
                    }
                }
                
                Pool.FreeUnmanaged(ref list);
            }

            foreach (Zone zone in zones.Values)
            {
                if (!zone)
                    continue;

                if (entityZones != null && entityZones.Zones.Contains(zone))
                    continue;

                if (zone.definition.Size != Vector3.zero)
                {
                    if (IsInsideBounds(zone, player.transform.position))
                        OnPlayerEnterZone(player, zone);
                }
                else
                {
                    if (Vector3.Distance(player.transform.position, zone.transform.position) <= zone.definition.Radius)
                        OnPlayerEnterZone(player, zone);
                }
            }

            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.SafeZone) && !player.InSafeZone())
                player.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, false);
        }

        private bool IsInsideBounds(Zone zone, Vector3 worldPos) => zone?.collider?.ClosestPoint(worldPos) == worldPos;
        #endregion
        
        private T GetEntityComponent<T>(BaseEntity entity) where T : EntityComponentBase
        {
            for (int i = 0; i < entity.Components.Count; i++)
            {
                EntityComponentBase component = entity.Components[i];
                if (component is T t)
                    return t;
            }

            return null;
        }
        #endregion

        #region Zone Functions
        private void InitializeZones()
        {
            if (zonesInitialized)
                return;

            foreach (Zone.Definition definition in storedData.definitions)
            {
                Zone zone = new GameObject().AddComponent<Zone>();
                zone.InitializeZone(definition);

                zones.Add(definition.Id, zone);
            }

            foreach (Zone zone in zones.Values)
                zone.FindZoneParent();

            zonesInitialized = true;

            UnsubscribeAll();
            UpdateHookSubscriptions();
        }

        private static bool ReverseVelocity(BaseVehicle baseVehicle)
        {
            if (baseVehicle is BaseVehicleModule module)
                baseVehicle = module.Vehicle;

            if (!baseVehicle || !baseVehicle.IsVehicleRoot() || !baseVehicle.rigidBody) 
                return false;
            
            if (baseVehicle.AnyMounted() && baseVehicle is BaseHelicopter)
            {
                baseVehicle.rigidBody.velocity *= -1f;
                Vector3 euler = baseVehicle.transform.eulerAngles;
                baseVehicle.transform.rotation = Quaternion.Euler(euler.x, euler.y - 180f, euler.z);
            }
            else
            {
                Vector3 force = (baseVehicle.rigidBody.velocity.normalized * -1f) * baseVehicle.rigidBody.mass * 4f;
                baseVehicle.rigidBody.velocity = Vector3.zero;
                baseVehicle.rigidBody.AddForce(force, ForceMode.Impulse);
            }

            return true;
        }

        private void EjectPlayer(BasePlayer player, Zone zone)
        {
            if (zone.keepInList.Contains(player.userID) || zone.whitelist.Contains(player.userID))
                return;

            if (!string.IsNullOrEmpty(zone.definition.Permission))
            {
                if (HasPermission(player, zone.definition.Permission))
                    return;
            }

            if (player.isMounted && ReverseVelocity(player.GetMountedVehicle()))
            {
                SendMessage(player, Message("eject", player.UserIDString));
                return;
            }

            Vector3 position = Vector3.zero;
            if (Spawns && !string.IsNullOrEmpty(zone.definition.EjectSpawns))
            {
                object success = Spawns.Call("GetRandomSpawn", zone.definition.EjectSpawns);
                if (success is Vector3 vector3)
                    position = vector3;
            }

            if (position == Vector3.zero)
            {
                float distance = zone.definition.Size != Vector3.zero ? Mathf.Max(zone.definition.Size.x, zone.definition.Size.z) : zone.definition.Radius;

                position = zone.transform.position + (((player.transform.position.XZ3D() - zone.transform.position.XZ3D()).normalized) * (distance + 10f));

                if (Physics.Raycast(new Ray(new Vector3(position.x, position.y + 300, position.z), Vector3.down), out RaycastHit rayHit, 500, TARGET_LAYERS, QueryTriggerInteraction.Ignore))
                    position.y = rayHit.point.y;
                else position.y = TerrainMeta.HeightMap.GetHeight(position);
            }

            player.MovePosition(position);
            player.ClientRPCPlayer(null, player, "ForcePositionTo", player.transform.position);
            player.SendNetworkUpdateImmediate();

            SendMessage(player, Message("eject", player.UserIDString));
        }

        private void AttractPlayer(BasePlayer player, Zone zone)
        {
            if (player.isMounted && ReverseVelocity(player.GetMountedVehicle()))
            {
                SendMessage(player, Message("attract", player.UserIDString));
                return;
            }

            float distance = zone.definition.Size != Vector3.zero ? Mathf.Max(zone.definition.Size.x, zone.definition.Size.z) : zone.definition.Radius;

            Vector3 position = zone.transform.position + (player.transform.position - zone.transform.position).normalized * (distance - 5f);
            position.y = TerrainMeta.HeightMap.GetHeight(position);

            player.MovePosition(position);
            player.ClientRPCPlayer(null, player, "ForcePositionTo", player.transform.position);
            player.SendNetworkUpdateImmediate();

            SendMessage(player, Message("attract", player.UserIDString));
        }

        private void ShowZone(BasePlayer player, string zoneId, float time = 30)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            if (zone.definition.Size != Vector3.zero)
            {
                Vector3 center = zone.definition.Location;
                Quaternion rotation = Quaternion.Euler(zone.definition.Rotation);
                Vector3 size = zone.definition.Size / 2;
                Vector3 point1 = RotatePointAroundPivot(new Vector3(center.x + size.x, center.y + size.y, center.z + size.z), center, rotation);
                Vector3 point2 = RotatePointAroundPivot(new Vector3(center.x + size.x, center.y - size.y, center.z + size.z), center, rotation);
                Vector3 point3 = RotatePointAroundPivot(new Vector3(center.x + size.x, center.y + size.y, center.z - size.z), center, rotation);
                Vector3 point4 = RotatePointAroundPivot(new Vector3(center.x + size.x, center.y - size.y, center.z - size.z), center, rotation);
                Vector3 point5 = RotatePointAroundPivot(new Vector3(center.x - size.x, center.y + size.y, center.z + size.z), center, rotation);
                Vector3 point6 = RotatePointAroundPivot(new Vector3(center.x - size.x, center.y - size.y, center.z + size.z), center, rotation);
                Vector3 point7 = RotatePointAroundPivot(new Vector3(center.x - size.x, center.y + size.y, center.z - size.z), center, rotation);
                Vector3 point8 = RotatePointAroundPivot(new Vector3(center.x - size.x, center.y - size.y, center.z - size.z), center, rotation);

                player.SendConsoleCommand("ddraw.line", time, Color.blue, point1, point2);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point1, point3);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point1, point5);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point4, point2);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point4, point3);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point4, point8);

                player.SendConsoleCommand("ddraw.line", time, Color.blue, point5, point6);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point5, point7);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point6, point2);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point8, point6);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point8, point7);
                player.SendConsoleCommand("ddraw.line", time, Color.blue, point7, point3);
            }
            else player.SendConsoleCommand("ddraw.sphere", time, Color.blue, zone.definition.Location, zone.definition.Radius);
        }

        private Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivot, Quaternion rotation) => rotation * (point - pivot) + pivot;

        #endregion

        #region Component
        public class Zone : MonoBehaviour
        {
            public Definition definition;

            public ZoneFlags disabledFlags = new ZoneFlags();

            public Zone parent;


            public List<BasePlayer> players = Pool.Get<List<BasePlayer>>();

            public List<BaseEntity> entities = Pool.Get<List<BaseEntity>>();

            private List<IOEntity> ioEntities = Pool.Get<List<IOEntity>>();


            public List<ulong> keepInList = Pool.Get<List<ulong>>();

            public List<ulong> whitelist = Pool.Get<List<ulong>>();

            public Hash<ulong, EntityZones> entityZones = new Hash<ulong, EntityZones>();


            private Rigidbody rigidbody;

            public Collider collider;

            public Bounds colliderBounds;


            private ChildSphereTrigger<TriggerRadiation> radiation;

            private ChildSphereTrigger<TriggerComfort> comfort;

            private ChildSphereTrigger<TriggerTemperature> temperature;

            private ChildSphereTrigger<TriggerSafeZone> safeZone;


            private readonly Hash<BaseVehicle, float> lastReversedTimes = new Hash<BaseVehicle, float>();

            private int creationFrame;

            private bool isTogglingLights = false;

            private void Awake()
            {
                gameObject.layer = (int)Layer.Reserved1;
                gameObject.name = "ZoneManager";
                enabled = false;

                creationFrame = Time.frameCount;
            }

            private void OnDestroy()
            {
                EmptyZone();

                Pool.FreeUnmanaged(ref players);
                Pool.FreeUnmanaged(ref entities);
                Pool.FreeUnmanaged(ref ioEntities);
                Pool.FreeUnmanaged(ref keepInList);
                Pool.FreeUnmanaged(ref whitelist);

                Interface.CallHook("OnZoneDestroyed", definition.Id);
            }

            private void EmptyZone()
            {
                RemovePlayersFromTriggers();

                keepInList.Clear();

                ioEntities.Clear();

                for (int i = players.Count - 1; i >= 0; i--)
                    Instance?.OnPlayerExitZone(players[i], this);

                for (int i = entities.Count - 1; i >= 0; i--)
                    Instance?.OnEntityExitZone(entities[i], this);
            }

            #region Zone Initialization
            public void InitializeZone(Definition definition)
            {
                if (this.definition == null)
                    Interface.CallHook("OnZoneInitialize", definition.Id);
                
                this.definition = definition;

                transform.position = definition.Location;

                transform.rotation = Quaternion.Euler(definition.Rotation);

                if (definition.Enabled)
                {
                    RegisterPermission();

                    InitializeCollider();

                    InitializeAutoLights();

                    InitializeRadiation();

                    InitializeSafeZone();

                    InitializeComfort();

                    InitializeTemperature();

                    RemovePlayersFromTriggers();

                    AddPlayersToTriggers();

                    OnZoneFlagsChanged();
                }
                else
                {
                    InvokeHandler.CancelInvoke(this, CheckAlwaysLights);
                    InvokeHandler.CancelInvoke(this, CheckLights);

                    if (isLightsOn)
                        ServerMgr.Instance.StartCoroutine(ToggleLights(false));

                    EmptyZone();

                    if (collider)
                        DestroyImmediate(collider);

                    if (rigidbody)
                        DestroyImmediate(rigidbody);
                }

                enabled = definition.Enabled;
            }

            public void FindZoneParent()
            {
                if (string.IsNullOrEmpty(definition.ParentID))
                    return;

                if (Instance == null)
                {
                    Debug.LogError($"[ZoneManager] Zone attempted to find parent zone, but plugin instance is null...");
                    return;
                }

                Instance.zones.TryGetValue(definition.ParentID, out parent);
            }

            public void Reset()
            {
                InvokeHandler.CancelInvoke(this, CheckAlwaysLights);
                InvokeHandler.CancelInvoke(this, CheckLights);

                if (isLightsOn)
                    ServerMgr.Instance.StartCoroutine(ToggleLights(false));

                EmptyZone();

                InitializeZone(definition);
                FindZoneParent();
            }

            public void OnZoneFlagsChanged()
            {
                if (HasFlag(ZoneFlags.PoweredSwitches))
                {
                    if (!InvokeHandler.IsInvoking(this, IOTick))
                        InvokeHandler.InvokeRandomized(this, IOTick, 0f, 1f, 0.1f);
                }
                else InvokeHandler.CancelInvoke(this, IOTick);
            }

            private void RegisterPermission()
            {
                if (Instance == null)
                {
                    Debug.LogError($"[ZoneManager] Zone attempted to register permission, but plugin instance is null...");
                    return;
                }

                if (!string.IsNullOrEmpty(definition.Permission) && !Instance.permission.PermissionExists(definition.Permission))
                    Instance.permission.RegisterPermission(definition.Permission, Instance);
            }

            private void InitializeCollider()
            {
                if (collider)
                    DestroyImmediate(collider);

                if (rigidbody)
                    DestroyImmediate(rigidbody);

                rigidbody = gameObject.AddComponent<Rigidbody>();
                rigidbody.useGravity = false;
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = true;
                rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;

                SphereCollider sphereCollider = gameObject.GetComponent<SphereCollider>();
                BoxCollider boxCollider = gameObject.GetComponent<BoxCollider>();

                if (definition.Size != Vector3.zero)
                {
                    if (sphereCollider)
                        Destroy(sphereCollider);

                    if (!boxCollider)
                    {
                        boxCollider = gameObject.AddComponent<BoxCollider>();
                        boxCollider.isTrigger = true;
                    }
                    boxCollider.size = definition.Size;
                    colliderBounds = boxCollider.bounds;
                    collider = boxCollider;
                }
                else
                {
                    if (boxCollider)
                        Destroy(boxCollider);

                    if (!sphereCollider)
                    {
                        sphereCollider = gameObject.AddComponent<SphereCollider>();
                        sphereCollider.isTrigger = true;
                    }
                    sphereCollider.radius = definition.Radius;
                    colliderBounds = sphereCollider.bounds;
                    collider = sphereCollider;
                }
            }
            #endregion

            #region Triggers
            private void InitializeRadiation()
            {                
                if (definition.Radiation > 0)
                {
                    radiation ??= new ChildSphereTrigger<TriggerRadiation>(gameObject, "Radiation");
                    radiation.Trigger.RadiationAmountOverride = definition.Radiation;
                    radiation.Collider.radius = collider is SphereCollider ? definition.Radius : Mathf.Min(definition.Size.x, definition.Size.y, definition.Size.z) * 0.5f;
                    radiation.Trigger.enabled = this.enabled;
                }
                else radiation?.Destroy();
            }

            private void InitializeComfort()
            {
                if (definition.Comfort > 0)
                {
                    comfort ??= new ChildSphereTrigger<TriggerComfort>(gameObject, "Comfort");
                    comfort.Trigger.baseComfort = definition.Comfort;
                    comfort.Trigger.triggerSize = comfort.Collider.radius = collider is SphereCollider ? definition.Radius : Mathf.Min(definition.Size.x, definition.Size.y, definition.Size.z) * 0.5f;
                    comfort.Trigger.enabled = this.enabled;
                }
                else comfort?.Destroy();
            }

            private void InitializeTemperature()
            {
                if (definition.Temperature != 0)
                {
                    temperature ??= new ChildSphereTrigger<TriggerTemperature>(gameObject, "Temperature");
                    temperature.Trigger.Temperature = definition.Temperature;
                    temperature.Trigger.triggerSize = temperature.Collider.radius = collider is SphereCollider ? definition.Radius : Mathf.Min(definition.Size.x, definition.Size.y, definition.Size.z) * 0.5f;
                    temperature.Trigger.enabled = this.enabled;
                }
                else temperature?.Destroy();
            }

            private void InitializeSafeZone()
            {
                if (definition.SafeZone)
                {
                    safeZone ??= new ChildSphereTrigger<TriggerSafeZone>(gameObject, "SafeZone");
                }
                else safeZone?.Destroy();
            }
                        
            private void AddToTrigger(TriggerBase triggerBase, BasePlayer player)
            {
                if (!triggerBase || !player)
                    return;

                triggerBase.entityContents ??= new HashSet<BaseEntity>();

                if (!triggerBase.entityContents.Add(player)) 
                    return;
                
                player.EnterTrigger(triggerBase);

                if (triggerBase is not TriggerSafeZone) 
                    return;
                
                if (player.IsItemHoldRestricted(player.inventory.containerBelt.FindItemByUID(player.svActiveItemID)))
                    player.UpdateActiveItem(default(ItemId));

                player.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, true);
            }

            private void RemoveFromTrigger(TriggerBase triggerBase, BasePlayer player)
            {
                if (!triggerBase || !player)
                    return;

                if (triggerBase.entityContents == null || !triggerBase.entityContents.Contains(player)) 
                    return;
                
                triggerBase.entityContents.Remove(player);
                player.LeaveTrigger(triggerBase);

                if (triggerBase is not TriggerSafeZone) 
                    return;
                
                if (!player.InSafeZone())
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, false);
            }

            private void AddPlayersToTriggers()
            {
                for (int i = 0; i < players.Count; i++)
                {
                    BasePlayer player = players[i];

                    if (safeZone != null)
                        AddToTrigger(safeZone.Trigger, player);

                    if (radiation != null)
                        AddToTrigger(radiation.Trigger, player);

                    if (comfort != null)
                        AddToTrigger(comfort.Trigger, player);

                    if (temperature != null)
                        AddToTrigger(temperature.Trigger, player);
                }
            }

            private void RemovePlayersFromTriggers()
            {
                for (int i = 0; i < players.Count; i++)                    
                {
                    BasePlayer player = players[i];

                    if (safeZone != null)
                        RemoveFromTrigger(safeZone.Trigger, player);

                    if (radiation != null)
                        RemoveFromTrigger(radiation.Trigger, player);

                    if (comfort != null)
                        RemoveFromTrigger(comfort.Trigger, player);

                    if (temperature != null)
                        RemoveFromTrigger(temperature.Trigger, player);
                }
            }

            private class ChildSphereTrigger<T> where T : TriggerBase
            {
                public GameObject Object { get; private set; }

                public SphereCollider Collider { get; private set; }

                public T Trigger { get; private set; }

                public ChildSphereTrigger(GameObject parent, string name)
                {
                    Object = parent.CreateChild();
                    Object.name = name;
                    Object.layer = (int)Layer.TransparentFX;

                    Collider = Object.AddComponent<SphereCollider>();
                    Collider.isTrigger = true;

                    Trigger = Object.AddComponent<T>();
                    Trigger.interestLayers = 0;
                }

                public void Destroy() => UnityEngine.Object.Destroy(Object);
            }
            #endregion

            #region Autolights
            private bool isLightsOn = false;

            private void InitializeAutoLights()
            {
                if (HasFlag(ZoneFlags.AlwaysLights))
                {
                    isLightsOn = true;

                    InvokeHandler.CancelInvoke(this, CheckAlwaysLights);
                    InvokeHandler.InvokeRandomized(this, CheckAlwaysLights, 5f, 60f, 10f);
                }
                else if (HasFlag(ZoneFlags.AutoLights))
                {
                    InvokeHandler.CancelInvoke(this, CheckLights);
                    InvokeHandler.InvokeRandomized(this, CheckLights, 5f, 20f, 10f);
                }
            }

            private void CheckAlwaysLights()
            {
                ServerMgr.Instance.StartCoroutine(ToggleLights(true));
            }

            private void CheckLights()
            {
                float currentTime = TOD_Sky.Instance.Cycle.Hour;

                bool shouldBeActive = currentTime > Configuration.AutoLights.OnTime || currentTime < Configuration.AutoLights.OffTime;

                if (shouldBeActive == isLightsOn) 
                    return;
                
                isLightsOn = shouldBeActive;
                ServerMgr.Instance.StartCoroutine(ToggleLights(isLightsOn));
            }

            private IEnumerator ToggleLights(bool active)
            {
                while (isTogglingLights)
                    yield return null;

                isTogglingLights = true;

                bool requiresFuel = Configuration.AutoLights.RequiresFuel;

                for (int i = 0; i < entities.Count; i++)
                {
                    if (ToggleLight(entities[i], active, requiresFuel))
                        yield return CoroutineEx.waitForEndOfFrame;
                }

                isTogglingLights = false;
            }

            private bool ToggleLight(BaseEntity baseEntity, bool active, bool requiresFuel)
            {
                BaseOven baseOven = baseEntity as BaseOven;
                if (baseOven)
                {
                    if (active)
                    {
                        if (!baseOven.IsOn())
                        {
                            if ((requiresFuel && baseOven.FindBurnable() != null) || !requiresFuel)
                                baseOven.SetFlag(BaseEntity.Flags.On, true);
                        }
                    }
                    else
                    {
                        if (baseOven.IsOn())
                            baseOven.StopCooking();
                    }

                    return true;
                }

                SearchLight searchLight = baseEntity as SearchLight;
                if (searchLight)
                {
                    if (active)
                    {
                        if (!searchLight.IsOn())
                            searchLight.SetFlag(BaseEntity.Flags.On, true);
                    }
                    else
                    {
                        if (searchLight.IsOn())
                            searchLight.SetFlag(BaseEntity.Flags.On, false);
                    }

                    return true;
                }

                return false;
            }
            #endregion

            #region Entity Detection            
            private void OnTriggerEnter(Collider col)
            {
                if (!definition.Enabled || !col || !col.gameObject)
                    return;

                BaseEntity baseEntity = col.gameObject.ToBaseEntity();
                if (!baseEntity || !baseEntity.IsValid())
                    return;

                if (baseEntity is BasePlayer { IsNpc: false } player)
                {
                    Instance?.OnPlayerEnterZone(player, this);

                    if (parent)
                        Instance?.UpdateZoneEntityFlags(this);

                    return;
                }

                Instance?.OnEntityEnterZone(baseEntity, this);
            }

            private void OnTriggerExit(Collider col)
            {
                if (!definition.Enabled || !col || !col.gameObject)
                    return;

                BaseEntity baseEntity = col.gameObject.ToBaseEntity();
                if (!baseEntity || !baseEntity.IsValid())
                    return;

                if (baseEntity is BasePlayer { IsNpc: false } player)
                {
                    Instance?.OnPlayerExitZone(player, this);

                    return;
                }

                Instance?.OnEntityExitZone(baseEntity, this);
            }

            public void OnPlayerEnterZone(BasePlayer player)
            {
                if (!players.Contains(player))
                    players.Add(player);

                if (zonedPlayers.TryGetValue(player.userID, out EntityZones entityZone))
                    entityZones[player.userID] = entityZone;
                
                if (safeZone != null)
                    AddToTrigger(safeZone.Trigger, player);

                if (radiation != null)
                    AddToTrigger(radiation.Trigger, player);

                if (comfort != null)
                    AddToTrigger(comfort.Trigger, player);

                if (temperature != null)
                    AddToTrigger(temperature.Trigger, player);
            }            

            public void OnPlayerExitZone(BasePlayer player)
            {
                players.Remove(player);
                entityZones.Remove(player.userID);
                
                if (safeZone != null)
                    RemoveFromTrigger(safeZone.Trigger, player);

                if (radiation != null)
                    RemoveFromTrigger(radiation.Trigger, player);

                if (comfort != null)
                    RemoveFromTrigger(comfort.Trigger, player);

                if (temperature != null)
                    RemoveFromTrigger(temperature.Trigger, player);
            }

            public void OnEntityEnterZone(BaseEntity baseEntity)
            {
                entities.Add(baseEntity);

                if (zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZone))
                    entityZones[baseEntity.net.ID.Value] = entityZone;

                if (HasFlag(ZoneFlags.NoDecay))
                {
                    DecayEntity decayEntity = baseEntity.GetComponentInParent<DecayEntity>();
                    if (decayEntity)
                    {
                        decayEntity.decay = null;
                    }
                }

                if (HasFlag(ZoneFlags.NoStability))
                {
                    if (baseEntity is StabilityEntity entity)
                    {
                        entity.grounded = true;
                    }
                }

                if (HasFlag(ZoneFlags.NpcFreeze) && baseEntity.IsNpc)
                {
                    if (baseEntity is BaseAnimalNPC animalNpc)
                    {
                        animalNpc.brain.SetEnabled(false);
                        return;
                    }

                    if (baseEntity is global::HumanNPC humanNpc)
                    {
                        humanNpc.Brain.SetEnabled(false);
                        return;
                    }

                    if (baseEntity is ScarecrowNPC scarecrowNpc)
                    {
                        scarecrowNpc.Brain.SetEnabled(false);
                        return;
                    }

                    if (baseEntity is BaseNpc npc)
                    {
                        npc.CancelInvoke(npc.TickAi);
                        return;
                    }
                }

                if (baseEntity is SmartSwitch or ElectricSwitch or RFReceiver)
                {
                    ioEntities.Add((IOEntity)baseEntity);

                    if (HasFlag(ZoneFlags.PoweredSwitches))
                    {
                        ((IOEntity)baseEntity).SetFlag(BaseEntity.Flags.Reserved8, true);
                        ((IOEntity)baseEntity).currentEnergy = int.MaxValue;
                    }
                }

                if (HasFlag(ZoneFlags.AlwaysLights) || (HasFlag(ZoneFlags.AutoLights) && isLightsOn))
                {
                    if (baseEntity is BaseOven or SearchLight)
                    {
                        ToggleLight(baseEntity, true, Configuration.AutoLights.RequiresFuel);
                    }
                }
            }

            public void OnEntityExitZone(BaseEntity baseEntity, bool resetDecay, bool isDead = false)
            {
                entities.Remove(baseEntity);

                entityZones.Remove(baseEntity.net.ID.Value);
                
                if (isDead)
                    return;

                if (resetDecay)
                {
                    if (HasFlag(ZoneFlags.NoDecay))
                    {
                        DecayEntity decayEntity = baseEntity.GetComponentInParent<DecayEntity>();
                        if (decayEntity)
                        {
                            decayEntity.decay = PrefabAttribute.server.Find<Decay>(decayEntity.prefabID);
                        }
                    }
                }

                if (HasFlag(ZoneFlags.NpcFreeze) && baseEntity.IsNpc)
                {                    
                    if (baseEntity is BaseAnimalNPC animalNpc)
                    {
                        animalNpc.brain.SetEnabled(true);
                        return;
                    }

                    if (baseEntity is global::HumanNPC humanNpc)
                    {
                        humanNpc.Brain.SetEnabled(true);
                        return;
                    }

                    if (baseEntity is ScarecrowNPC scarecrowNpc)
                    {
                        scarecrowNpc.Brain.SetEnabled(true);
                        return;
                    }

                    if (baseEntity is BaseNpc npc)
                    {
                        npc.InvokeRandomized(npc.TickAi, 0.1f, 0.1f, 0.00500000035f);
                        return;
                    }
                }

                if (baseEntity is SmartSwitch or ElectricSwitch or RFReceiver)
                {
                    IOEntity ioEntity = (IOEntity)baseEntity;
                    ioEntities.Remove(ioEntity);

                    if (HasFlag(ZoneFlags.PoweredSwitches))
                    {
                        ioEntity.SetFlag(BaseEntity.Flags.Reserved8, false);
                        ioEntity.currentEnergy = 0;

                        for (int i = 0; i < ioEntity.inputs.Length; i++)
                        {
                            IOEntity fromEntity = ioEntity.inputs[i].connectedTo.Get();
                            if (fromEntity)
                                fromEntity.MarkDirtyForceUpdateOutputs();
                        }
                    }
                }

                if (!HasFlag(ZoneFlags.AlwaysLights) && (!HasFlag(ZoneFlags.AutoLights) || !isLightsOn)) 
                    return;
                
                if (baseEntity is BaseOven or SearchLight)
                {
                    ToggleLight(baseEntity, false, false);
                }
            }
            #endregion

            #region IO Power            
            private void IOTick()
            {
                for (int i = 0; i < ioEntities.Count; i++)
                {
                    IOEntity ioEntity = ioEntities[i];

                    if (!ioEntity || ioEntity.IsDestroyed) 
                        continue;
                    
                    ioEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                    ioEntity.currentEnergy = int.MaxValue;
                }
            }
            #endregion

            #region Vehicle Enter/Exit
            public bool TryReverseVelocity(BaseEntity baseEntity)
            {
                if (Time.frameCount == creationFrame)
                    return false;

                if (baseEntity is not BaseVehicle baseVehicle) 
                    return false;
                
                if (baseVehicle is BaseVehicleModule module)
                    baseVehicle = module.Vehicle;

                if (!CanReverseVelocity(baseVehicle)) 
                    return false;

                if (!ReverseVelocity(baseVehicle)) 
                    return false;
                
                lastReversedTimes[baseVehicle] = Time.time;
                return true;

            }

            private bool CanReverseVelocity(BaseVehicle baseVehicle)
            {
                if (lastReversedTimes.TryGetValue(baseVehicle, out float lastReversedTime))
                    return Time.time - lastReversedTime > 0.5f;

                return true;
            }
            #endregion

            #region Helpers
            public bool HasPermission(BasePlayer player)
            {
                if (Instance == null)
                {
                    Debug.LogError($"[ZoneManager] Zone attempted to check player permission, but plugin instance is null...");
                    return false;
                }

                return string.IsNullOrEmpty(definition.Permission) || Instance.permission.UserHasPermission(player.UserIDString, definition.Permission);
            }

            public bool CanLeaveZone(BasePlayer player) => !keepInList.Contains(player.userID);

            public bool CanEnterZone(BasePlayer player) => HasPermission(player) || !CanLeaveZone(player) || whitelist.Contains(player.userID);
            #endregion

            #region Flags
            public void AddFlag(int flag) 
            {
                definition.Flags.AddFlag(flag);
                OnZoneFlagsChanged();
            }

            public void RemoveFlag(int flag)
            {
                definition.Flags.RemoveFlag(flag);
                OnZoneFlagsChanged();
            }

            public bool HasFlag(int flag) => definition.Flags.HasFlag(flag) && !disabledFlags.HasFlag(flag);

            public bool HasDisabledFlag(int flag) => disabledFlags.HasFlag(flag);

            public void AddDisabledFlag(int flag)
            {
                disabledFlags.AddFlag(flag);
                OnZoneFlagsChanged();
            }

            public void RemoveDisabledFlag(int flag)
            {
                disabledFlags.AddFlag(flag);
                OnZoneFlagsChanged();
            }
            #endregion

            #region Zone Definition
            public class Definition
            {
                public string Id { get; set; }
                
                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string Name { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public float Radius { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public float Radiation { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public float Comfort { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public float Temperature { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public bool SafeZone { get; set; }

                public Vector3 Location { get; set; }

                public Vector3 Size { get; set; }

                public Vector3 Rotation { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string ParentID { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string EnterMessage { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string LeaveMessage { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string Permission { get; set; }

                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public string EjectSpawns { get; set; }

                [DefaultValue(true)]
                [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public bool Enabled { get; set; } = true;

                [JsonIgnore]
                public Plugin Owner { get; private set; }
                
                [JsonIgnore]
                public bool IsTemporary { get; private set; }

                public ZoneFlags Flags { get; set; }

                public Definition() { }

                public Definition(Vector3 position)
                {
                    Radius = 20f;
                    Location = position;

                    Flags = new ZoneFlags();
                }

                public void WithOwner(Plugin owner)
                {
                    Owner = owner;
                    IsTemporary = true;
                }
            }
            #endregion
        }
        #endregion

        #region Entity Management
        private void OnPlayerEnterZone(BasePlayer player, Zone zone)
        {
            if (!player || IsNpc(player))
                return;

            if (!zone.CanEnterZone(player))
            {
                EjectPlayer(player, zone);
                return;
            }

            if (HasFlag(zone, ZoneFlags.Eject))
            {
                if (!CanBypass(player, ZoneFlags.Eject) && !IsAdmin(player))
                {
                    EjectPlayer(player, zone);
                    return;
                }
            }

            //if (HasFlag(zone, ZoneFlags.KeepVehiclesOut) && player.isMounted && ReverseVelocity(player.GetMountedVehicle()))
            //{
            //    SendMessage(player, Message("novehiclesenter", player.UserIDString));
            //    return;
            //}

            if (player.IsSleeping() && !player.IsConnected)
            {
                if (HasFlag(zone, ZoneFlags.KillSleepers))
                {
                    if (!CanBypass(player, ZoneFlags.KillSleepers) && !IsAdmin(player))
                    {
                        player.Die();
                        return;
                    }
                }

                if (HasFlag(zone, ZoneFlags.EjectSleepers))
                {
                    if (!CanBypass(player, ZoneFlags.EjectSleepers) && !IsAdmin(player))
                    {
                        EjectPlayer(player, zone);
                        return;
                    }
                }
            }

            if (HasFlag(zone, ZoneFlags.Kill))
            {
                if (!CanBypass(player, ZoneFlags.Kill) && !IsAdmin(player))
                {
                    player.Die();
                    return;
                }
            }

            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
                zonedPlayers[player.userID] = entityZones = new EntityZones();

            if (!entityZones.EnterZone(zone))
                return;

            if (zone.parent)
                entityZones.UpdateFlags();
            else entityZones.AddFlags(zone.definition.Flags);

            zone.OnPlayerEnterZone(player);

            UpdateMetabolismForPlayer(player);

            if (!string.IsNullOrEmpty(zone.definition.EnterMessage))
            {
                if (PopupNotifications != null && Configuration.Notifications.Popups)
                    PopupNotifications.Call("CreatePopupNotification", string.Format(zone.definition.EnterMessage, player.displayName), player);
                else SendMessage(player, zone.definition.EnterMessage, player.displayName);
            }

            Interface.CallHook("OnEnterZone", zone.definition.Id, player);
        }

        private void OnPlayerExitZone(BasePlayer player, Zone zone)
        {
            if (!player || IsNpc(player))
                return;

            //if (HasFlag(zone, ZoneFlags.KeepVehiclesIn) && player.isMounted && ReverseVelocity(player.GetMountedVehicle()))
            //{
            //    SendMessage(player, Message("novehiclesleave", player.UserIDString));
            //    return;
            //}

            if (!zone.CanLeaveZone(player))
            {
                AttractPlayer(player, zone);
                return;
            }

            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
                return;

            entityZones.LeaveZone(zone);

            if (entityZones.ShouldRemove())
                zonedPlayers.Remove(player.userID);
            else entityZones.UpdateFlags();

            zone.OnPlayerExitZone(player);

            UpdateMetabolismForPlayer(player);

            if (!string.IsNullOrEmpty(zone.definition.LeaveMessage))
            {
                if (PopupNotifications != null && Configuration.Notifications.Popups)
                    PopupNotifications.Call("CreatePopupNotification", string.Format(zone.definition.LeaveMessage, player.displayName), player);
                else SendMessage(player, zone.definition.LeaveMessage, player.displayName);
            }

            Interface.CallHook("OnExitZone", zone.definition.Id, player);
        }

        private void UpdateMetabolismForPlayer(BasePlayer player)
        {
            if (!player)
                return;

            MetabolismAttribute bleeding = player.metabolism.bleeding;
            if (HasPlayerFlag(player, ZoneFlags.NoBleed))
            {
                bleeding.value = 0f;
                bleeding.max = 0f;
            }
            else bleeding.max = 1f;

            MetabolismAttribute oxygen = player.metabolism.oxygen;
            if (HasPlayerFlag(player, ZoneFlags.NoDrown))
            {
                oxygen.value = 1f;
                oxygen.min = 1f;
            }
            else oxygen.min = 0f;

            MetabolismAttribute poison = player.metabolism.poison;
            if (HasPlayerFlag(player, ZoneFlags.NoPoison))
            {
                poison.value = 0f;
                poison.max = 0f;
            }
            else poison.max = 100f;

            MetabolismAttribute calories = player.metabolism.calories;
            if (HasPlayerFlag(player, ZoneFlags.NoStarvation))
            {
                calories.value = Mathf.Max(calories.value, 50f);
                calories.min = calories.value;
            }
            else calories.min = 0f;

            MetabolismAttribute hydration = player.metabolism.hydration;
            if (HasPlayerFlag(player, ZoneFlags.NoThirst))
            {
                hydration.value = Mathf.Max(hydration.value, 50f);
                hydration.min = hydration.value;
            }
            else hydration.min = 0f;

            MetabolismAttribute radiation_level = player.metabolism.radiation_level;
            MetabolismAttribute radiation_poison = player.metabolism.radiation_poison;
            if (HasPlayerFlag(player, ZoneFlags.NoRadiation))
            {
                radiation_level.value = 0f;
                radiation_level.max = 0f;
                radiation_poison.value = 0f;
                radiation_poison.max = 0f;
            }
            else
            {
                radiation_level.max = 100f;
                radiation_poison.max = 500f;
            }

            player.metabolism.SendChangesToClient();
        }

        private void OnEntityEnterZone(BaseEntity baseEntity, Zone zone)
        {
            if (!baseEntity || !baseEntity.IsValid())
                return;

            if (zone.HasFlag(ZoneFlags.KeepVehiclesOut) && !zone.entities.Contains(baseEntity) && zone.TryReverseVelocity(baseEntity))
            {
                BasePlayer player = (baseEntity as BaseVehicle).GetDriver();
                if (player)
                    SendMessage(player, Message("novehiclesenter", player.UserIDString));
                return;
            }

            if (!zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZones))
                zonedEntities[baseEntity.net.ID] = entityZones = new EntityZones();

            if (!entityZones.EnterZone(zone))
                return;

            if (zone.parent)
                entityZones.UpdateFlags();
            else entityZones.AddFlags(zone.definition.Flags);

            zone.OnEntityEnterZone(baseEntity);

            Interface.CallHook("OnEntityEnterZone", zone.definition.Id, baseEntity);
        }

        private void OnEntityExitZone(BaseEntity baseEntity, Zone zone)
        {
            if (!baseEntity || !baseEntity.IsValid())
                return;

            if (zone.HasFlag(ZoneFlags.KeepVehiclesOut) && zone.entities.Contains(baseEntity) && zone.TryReverseVelocity(baseEntity))
            {
                BasePlayer player = (baseEntity as BaseVehicle).GetDriver();
                if (player)
                    SendMessage(player, Message("novehiclesleave", player.UserIDString));

                return;
            }

            if (!zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZones))
                return;

            entityZones.LeaveZone(zone);

            if (entityZones.ShouldRemove())
                zonedEntities.Remove(baseEntity.net.ID);
            else entityZones.UpdateFlags();

            zone.OnEntityExitZone(baseEntity, !entityZones.HasFlag(ZoneFlags.NoDecay));

            Interface.CallHook("OnEntityExitZone", zone.definition.Id, baseEntity);
        }
        #endregion

        #region Helpers
        private bool IsAdmin(BasePlayer player) => player?.net?.connection?.authLevel > 0;

        private bool IsNpc(BasePlayer player) => player.IsNpc || player is NPCPlayer;

        private bool HasPermission(BasePlayer player, string perm) => IsAdmin(player) || permission.UserHasPermission(player.UserIDString, perm);

        private bool HasPermission(ConsoleSystem.Arg arg, string perm)
        {
            BasePlayer player = arg.Connection?.player as BasePlayer;
            return !player || permission.UserHasPermission(player.UserIDString, perm);
        }
        
        private bool CanBypass(BasePlayer player, int flag) => CanBypass(player.UserIDString, flag);

        private bool CanBypass(ulong playerId, int flag) => CanBypass(playerId.ToString(), flag);
        
        private bool CanBypass(string playerId, int flag)
        {
            if (ZoneFlags.IndexToName.TryGetValue(flag, out string flagName))
                return permission.UserHasPermission(playerId, PERMISSION_IGNORE_FLAG + flagName);
            
            Debug.LogError($"[ZoneManager] CanBypass called with invalid flag : {flag}");
            return false;
        }

        private void SendMessage(BasePlayer player, string message, params object[] args)
        {
            if (player)
            {
                if (args.Length > 0)
                    message = string.Format(message, args);
                SendReply(player, $"<color={Configuration.Notifications.Color}>{Configuration.Notifications.Prefix}</color> {message}");
            }
            else Puts(message);
        }

        private Zone GetZoneByID(string zoneId) => zones.ContainsKey(zoneId) ? zones[zoneId] : null;

        private void AddToKeepinlist(Zone zone, BasePlayer player)
        {
            zone.keepInList.Add(player.userID);

            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones) || !entityZones.Zones.Contains(zone))
                AttractPlayer(player, zone);
        }

        private void RemoveFromKeepinlist(Zone zone, BasePlayer player) => zone.keepInList.Remove(player.userID);

        private void AddToWhitelist(Zone zone, BasePlayer player)
        {
            if (!zone.whitelist.Contains(player.userID))
                zone.whitelist.Add(player.userID);
        }

        private void RemoveFromWhitelist(Zone zone, BasePlayer player) => zone.whitelist.Remove(player.userID);

        private bool HasPlayerFlag(BasePlayer player, int flag)
        {
            if (!player)
                return false;

            if (adminBypass.HasFlag(flag) && IsAdmin(player))
                return false;

            if (CanBypass(player, flag))
                return false;

            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
                return false;

            return entityZones.HasFlag(flag);
        }

        private bool HasEntityFlag(BaseEntity baseEntity, int flag)
        {
            if (!baseEntity.IsValid())
                return false;

            if (!zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZones))
                return false;

            return entityZones.HasFlag(flag);
        }
        #endregion

        #region API 

        #region Zone Management       

        private void SetZoneStatus(string zoneId, bool active)
        {
            Zone zone = GetZoneByID(zoneId);
            if (zone)
            {
                zone.definition.Enabled = active;
                zone.InitializeZone(zone.definition);
            }
        }

        private Vector3 GetZoneLocation(string zoneId) => GetZoneByID(zoneId)?.definition.Location ?? Vector3.zero;

        private object GetZoneRadius(string zoneID) => GetZoneByID(zoneID)?.definition.Radius;

        private object GetZoneSize(string zoneID) => GetZoneByID(zoneID)?.definition.Size;

        private object GetZoneName(string zoneID) => GetZoneByID(zoneID)?.definition.Name;

        private object CheckZoneID(string zoneID) => GetZoneByID(zoneID)?.definition.Id;

        private object GetZoneIDs()
        {
            string[] array = new string[zones.Count];
            int i = 0;
            foreach (string zoneId in zones.Keys)
            {
                array[i] = zoneId;
                i++;
            }
            
            return array;
        }
        
        private void GetZoneIDsNoAlloc(List<string> list) => list.AddRange(zones.Keys);

        private bool IsPositionInZone(string zoneID, Vector3 position)
        {
            Zone zone = GetZoneByID(zoneID);
            if (!zone)
                return false;

            if (zone.definition.Size != Vector3.zero)
                return IsInsideBounds(zone, position); 
            return Vector3.Distance(position, zone.transform.position) <= zone.definition.Radius;            
        }
        
        private bool IsPositionInAnyZone(Vector3 position)
        {
            foreach (KeyValuePair<string, Zone> zone in zones)
            {
                if (zone.Value.definition.Size != Vector3.zero && IsInsideBounds(zone.Value, position))
                    return true;

                if (Vector3.Distance(position, zone.Value.transform.position) <= zone.Value.definition.Radius)
                    return true;
            }

            return false;
        }
        
        private void GetZonesAtPosition(Vector3 position, List<string> results)
        {
            foreach (KeyValuePair<string, Zone> zone in zones)
            {
                if (zone.Value.definition.Size != Vector3.zero && IsInsideBounds(zone.Value, position))
                {
                    results.Add(zone.Key);
                    continue;
                }

                if (Vector3.Distance(position, zone.Value.transform.position) <= zone.Value.definition.Radius)
                    results.Add(zone.Key);
            }
        }

        private List<BasePlayer> GetPlayersInZone(string zoneID)
        {
            Zone zone = GetZoneByID(zoneID);
            if (!zone)
                return new List<BasePlayer>();

            return new List<BasePlayer>(zone.players);
        }

        private List<BaseEntity> GetEntitiesInZone(string zoneId)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return new List<BaseEntity>();

            return new List<BaseEntity>(zone.entities);
        }
        
        private void GetPlayersInZoneNoAlloc(string zoneID, List<BasePlayer> list)
        {
            Zone zone = GetZoneByID(zoneID);
            if (!zone)
                return;

            list.AddRange(zone.players);
        }

        private void GetEntitiesInZoneNoAlloc(string zoneId, List<BaseEntity> list)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            list.AddRange(zone.entities);
        }

        private bool isPlayerInZone(string zoneID, BasePlayer player) => IsPlayerInZone(zoneID, player);
        
        private bool isEntityInZone(string zoneID, BaseEntity entity) => IsEntityInZone(zoneID, entity);

        private bool IsPlayerInZone(string zoneID, BasePlayer player)
        {
            Zone zone = GetZoneByID(zoneID);
            if (!zone)
                return false;

            return zone.players.Contains(player);
        }

        private bool IsEntityInZone(string zoneID, BaseEntity entity)
        {
            Zone zone = GetZoneByID(zoneID);
            return zone && zone.entities.Contains(entity);
        }

        private string[] GetPlayerZoneIDs(BasePlayer player)
        {
            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
                return Array.Empty<string>();

            string[] array = new string[entityZones.Zones.Count];
            for (int i = 0; i < entityZones.Zones.Count; i++)
                array[i] = entityZones.Zones[i].definition.Id;
            
            return array;
        }

        private string[] GetEntityZoneIDs(BaseEntity entity)
        {
            if (!zonedEntities.TryGetValue(entity.net.ID, out EntityZones entityZones))
                return Array.Empty<string>();

            string[] array = new string[entityZones.Zones.Count];
            for (int i = 0; i < entityZones.Zones.Count; i++)
                array[i] = entityZones.Zones[i].definition.Id;
            
            return array;
        }
        
        private void GetPlayerZoneIDsNoAlloc(BasePlayer player, List<string> list)
        {
            if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones))
                return;
            
            foreach (Zone zone in entityZones.Zones)
                list.Add(zone.definition.Id);
        }

        private void GetEntityZoneIDsNoAlloc(BaseEntity entity, List<string> list)
        {
            if (!zonedEntities.TryGetValue(entity.net.ID, out EntityZones entityZones))
                return;
            
            foreach (Zone zone in entityZones.Zones)
                list.Add(zone.definition.Id);
        }

        private bool HasFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            if (!ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))             
            {
                Debug.Log($"[ZoneManager] A plugin has call HasFlag with a invalid flag : {flagName}");
                return false;
            }

            return zone.HasFlag(v);
        }

        private bool PositionHasFlag(Vector3 position, string flagName)
        {
            if (!ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))             
            {
                Debug.Log($"[ZoneManager] A plugin has call PositionHasFlag with a invalid flag : {flagName}");
                return false;
            }
            
            foreach (KeyValuePair<string, Zone> zone in zones)
            {
                if (!zone.Value.HasFlag(v))
                    continue;
                
                if (zone.Value.definition.Size != Vector3.zero && IsInsideBounds(zone.Value, position))
                    return true;

                if (Vector3.Distance(position, zone.Value.transform.position) <= zone.Value.definition.Radius)
                    return true;
            }

            return false;
        }

        private void AddFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))
                zone.AddFlag(v);
            else Debug.Log($"[ZoneManager] A plugin has call AddFlag with a invalid flag : {flagName}");           
        }

        private void RemoveFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))
                zone.RemoveFlag(v);
            else Debug.Log($"[ZoneManager] A plugin has call RemoveFlag with a invalid flag : {flagName}");
        }

        private bool HasDisabledFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v)) 
                return zone.HasDisabledFlag(v);
            
            Debug.Log($"[ZoneManager] A plugin has call HasDisabledFlag with a invalid flag : {flagName}");
            return false;

        }

        private void AddDisabledFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))
                zone.AddDisabledFlag(v);
            else Debug.Log($"[ZoneManager] A plugin has call AddDisabledFlag with a invalid flag : {flagName}");            
        }

        private void RemoveDisabledFlag(string zoneId, string flagName)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v))
                zone.RemoveDisabledFlag(v);
            else Debug.Log($"[ZoneManager] A plugin has call RemoveDisabledFlag with a invalid flag : {flagName}");            
        }

        private bool CreateOrUpdateZone(string zoneId, string[] args, Vector3 position = default(Vector3))
        {
            bool result = CreateOrUpdateZoneInternal(zoneId, args, position);
            if (result)
                SaveData();
            return result;
        }
        
        private void CreateOrUpdateZones(List<(string, string[], Vector3)> zones, List<bool> results = null)
        {
            bool any = false;
            foreach ((string zoneId, string[] args, Vector3 position) in zones)
            {
                bool result = CreateOrUpdateZoneInternal(zoneId, args, position);
                any |= result;
                results?.Add(result);
            }
            
            if (any)
                SaveData();
        }
        
        private bool CreateOrUpdateTemporaryZone(Plugin owner, string zoneId, string[] args, Vector3 position = default(Vector3)) => 
            CreateOrUpdateZoneInternal(zoneId, args, position, owner);

        private void CreateOrUpdateTemporaryZones(Plugin owner, List<(string, string[], Vector3)> zones, List<bool> results = null)
        {
            foreach ((string zoneId, string[] args, Vector3 position) in zones)
            {
                bool result = CreateOrUpdateZoneInternal(zoneId, args, position, owner);
                results?.Add(result);
            }
        }

        private bool CreateOrUpdateZoneInternal(string zoneId, string[] args, Vector3 position = default(Vector3), Plugin owner = null)
        {
            Zone.Definition definition;
            bool update = true;
            if (!zones.TryGetValue(zoneId, out Zone zone))
            {
                zone = new GameObject().AddComponent<Zone>();
                definition = new Zone.Definition { Id = zoneId, Radius = 20, Flags = new ZoneFlags() };
                
                zones[zoneId] = zone;
                
                if (owner)
                {
                    definition.WithOwner(owner);
                    if (!temporaryZones.TryGetValue(owner, out HashSet<string> set))
                        temporaryZones[owner] = new HashSet<string>();

                    temporaryZones[owner].Add(zoneId);
                }
                
                zone.InitializeZone(definition);
                update = false;
            }
            else definition = zone.definition;

            if (definition.Owner && owner != definition.Owner)
                return false;

            UpdateZoneDefinition(zone, args);

            if (position != default(Vector3))
                definition.Location = position;

            zone.definition = definition;
            zone.Reset();

            Interface.CallHook(update ? "OnZoneUpdated" : "OnZoneCreated", zoneId);
            
            return true;
        }

        private bool EraseZone(string zoneId)
        {
            bool result = EraseZoneInternal(zoneId);
            if (result)
                SaveData();
            
            return result;
        }

        private void EraseZones(List<string> zoneIds, List<bool> results = null)
        {
            bool any = false;
            foreach (string zoneId in zoneIds)
            {
                bool result = EraseZoneInternal(zoneId);
                any |= result;
                results?.Add(result);
            }
            
            if (any)
                SaveData();
        }
        
        private bool EraseTemporaryZone(Plugin owner, string zoneId) => EraseZoneInternal(zoneId, owner);

        private void EraseTemporaryZones(Plugin owner, List<string> zoneIds, List<bool> results = null)
        {
            foreach (string zoneId in zoneIds)
            {
                bool result = EraseZoneInternal(zoneId, owner);
                results?.Add(result);
            }
        }
        
        private bool EraseZoneInternal(string zoneId, Plugin owner = null)
        {
            if (!zones.TryGetValue(zoneId, out Zone zone))
                return false;

            Plugin zoneOwner = zone.definition.Owner;

            // Only compare zone owner if the owner param is provided so users can remove temporary zones without
            // needing to unload the plugin that created them
            if (owner && zoneOwner && owner != zoneOwner)
                return false;

            zones.Remove(zoneId);
            
            if (zoneOwner && temporaryZones.TryGetValue(zoneOwner, out HashSet<string> set))
                set.Remove(zoneId);

            UnityEngine.Object.DestroyImmediate(zone.gameObject);
            Interface.CallHook("OnZoneErased", zoneId);
            return true;
        }

        private List<string> ZoneFieldListRaw()
        {
            List<string> list = new List<string> { "name", "ID", "radius", "rotation", "size", "Location", "enter_message", "leave_message", "radiation", "comfort", "temperature" };
            list.AddRange(ZoneFlags.NameToIndex.Keys);
            return list;
        }

        private Dictionary<string, string> ZoneFieldList(string zoneId)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return null;

            Dictionary<string, string> fields = new Dictionary<string, string>
            {
                { "name", zone.definition.Name },
                { "ID", zone.definition.Id },
                { "comfort", zone.definition.Comfort.ToString() },
                { "temperature", zone.definition.Temperature.ToString() },
                { "radiation", zone.definition.Radiation.ToString() },
                { "safezone", zone.definition.SafeZone.ToString() },
                { "radius", zone.definition.Radius.ToString() },
                { "rotation", zone.definition.Rotation.ToString() },
                { "size", zone.definition.Size.ToString() },
                { "Location", zone.definition.Location.ToString() },
                { "enter_message", zone.definition.EnterMessage },
                { "leave_message", zone.definition.LeaveMessage },
                { "permission", zone.definition.Permission },
                { "ejectspawns", zone.definition.EjectSpawns }
            };

            foreach (KeyValuePair<string, int> kvp in ZoneFlags.NameToIndex)
                fields[kvp.Key] = zone.HasFlag(kvp.Value).ToString();

            return fields;
        }
        #endregion

        #region Entity Management        
        private bool AddPlayerToZoneKeepinlist(string zoneId, BasePlayer player)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            AddToKeepinlist(zone, player);
            return true;
        }

        private bool RemovePlayerFromZoneKeepinlist(string zoneId, BasePlayer player)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            RemoveFromKeepinlist(zone, player);
            return true;
        }

        private bool AddPlayerToZoneWhitelist(string zoneId, BasePlayer player)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            AddToWhitelist(zone, player);
            return true;
        }

        private bool RemovePlayerFromZoneWhitelist(string zoneId, BasePlayer player)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
                return false;

            RemoveFromWhitelist(zone, player);
            return true;
        }

        private bool EntityHasFlag(BaseEntity baseEntity, string flagName)
        {
            if (!baseEntity.IsValid())
                return false;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v)) 
                return HasEntityFlag(baseEntity, v);
            
            Debug.LogError($"[ZoneManager] A plugin has called EntityHasFlag with a invalid flag : {flagName}");
            return false;

        }

        private bool PlayerHasFlag(BasePlayer player, string flagName)
        {
            if (!player)
                return false;

            if (ZoneFlags.NameToIndex.TryGetValue(flagName, out int v)) 
                return HasPlayerFlag(player, v);
            
            Debug.LogError($"[ZoneManager] A plugin has called EntityHasFlag with a invalid flag : {flagName}");
            return false;

        }
        #endregion

        #region Plugin Integration
        private object CanRedeemKit(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoKits) ? "You may not redeem a kit inside this area" : null;

        private object CanTeleport(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoTp) ? "You may not teleport in this area" : null;

        private object canRemove(BasePlayer player) => CanRemove(player);

        private object CanRemove(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoRemove) ? "You may not use the remover tool in this area" : null;

        private bool CanChat(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoChat) ? false : true;

        private object CanTrade(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoTrade) ? "You may not trade in this area" : null;

        private object canShop(BasePlayer player) => CanShop(player);

        private object CanShop(BasePlayer player) => HasPlayerFlag(player, ZoneFlags.NoShop) ? "You may not use the store in this area" : null;
        #endregion

        #endregion

        #region Flags
        public class ZoneFlags
        {
            public static readonly int AutoLights;
            public static readonly int AlwaysLights;

            public static readonly int NoKits;
            public static readonly int NoTrade;
            public static readonly int NoShop;
            public static readonly int NoTp;
            public static readonly int NoRemove;

            public static readonly int NoPoison;
            public static readonly int NoStarvation;
            public static readonly int NoThirst;
            public static readonly int NoRadiation;
            public static readonly int NoDrown;
            public static readonly int NoBleed;
            public static readonly int NoWounded;

            public static readonly int Eject;
            public static readonly int EjectSleepers;

            public static readonly int PvpGod;
            public static readonly int PveGod;
            public static readonly int SleepGod;
            public static readonly int NoPve;
            public static readonly int NoFallDamage;

            public static readonly int UnDestr;
            public static readonly int NoBuildingDamage;
            public static readonly int NoDecay;

            public static readonly int NoBuild;
            public static readonly int NoStability;
            public static readonly int NoUpgrade;

            public static readonly int NoSprays;
            public static readonly int NoDeploy;

            public static readonly int PoweredSwitches;

            public static readonly int LootSelf;
            public static readonly int NoPlayerLoot;
            public static readonly int NoNPCLoot;
            public static readonly int NoBoxLoot;
            public static readonly int NoPickup;
            public static readonly int NoCollect;
            public static readonly int NoDrop;
            public static readonly int NoLootSpawns;
            public static readonly int NoEntityPickup;
            public static readonly int NoGather;

            public static readonly int NoHeliTargeting;
            public static readonly int NoTurretTargeting;
            public static readonly int NoAPCTargeting;
            public static readonly int NoNPCTargeting;
            public static readonly int NpcFreeze;
            public static readonly int NoNPCSpawns;

            public static readonly int KeepVehiclesIn;
            public static readonly int KeepVehiclesOut;
            public static readonly int NoVehicleMounting;
            public static readonly int NoVehicleDismounting;

            public static readonly int NoChat;
            public static readonly int NoVoice;
            
            public static readonly int NoCorpse;
            public static readonly int NoSuicide;
            public static readonly int KillSleepers;            

            public static readonly int Kill;
            public static readonly int InfiniteTrapAmmo;

            public static readonly int NoCup;            
            public static readonly int NoSignUpdates;
            public static readonly int NoOvenToggle;
            public static readonly int NoVending;
            public static readonly int NoStash;
            public static readonly int NoCraft;  
            public static readonly int NoDoorAccess;

            public static readonly int Custom1;
            public static readonly int Custom2;
            public static readonly int Custom3;
            public static readonly int Custom4;
            public static readonly int Custom5;

            public static readonly Hash<string, int> NameToIndex;
            public static readonly Hash<int, string> IndexToName;

            private static readonly int Count;

            private readonly BitArray _bitArray;

            static ZoneFlags()
            {
                NameToIndex = new Hash<string, int>(StringComparer.OrdinalIgnoreCase);
                IndexToName = new Hash<int, string>();

                FieldInfo[] fields = typeof(ZoneFlags).GetFields(BindingFlags.Public | BindingFlags.Static);

                int index = 0;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo fieldInfo = fields[i];

                    if (fieldInfo.FieldType != typeof(int)) 
                        continue;
                    
                    fieldInfo.SetValue(null, index);

                    NameToIndex[fieldInfo.Name] = index;
                    IndexToName[index] = fieldInfo.Name;
                    
                    index++;
                }

                Count = index;// NameToIndex.Values.Max() + 1;
            }

            public static bool Find(string flagName, out int index)
            {
                foreach (KeyValuePair<string, int> kvp in NameToIndex)
                {
                    if (!flagName.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)) 
                        continue;
                    
                    index = kvp.Value;
                    return true;
                }

                index = 0;
                return false;
            }

            public ZoneFlags()
            {
                _bitArray = new BitArray(Count, false);               
            }

            public bool this[int key]
            {
                get => _bitArray[key];
                set => _bitArray[key] = value;
            }

            public bool HasFlag(int flag) => this[flag];
            
            public void AddFlag(int flag) => this[flag] = true;

            public void RemoveFlag(int flag) => this[flag] = false;

            public void SetFlags(params int[] array)
            {
                for (int i = 0; i < _bitArray.Length; i++)
                {
                    this[i] = array.Contains(i);
                }
            }

            public void Clear() => _bitArray.SetAll(false);
            
            public bool HasFlag(string flagName)
            {
                if (!NameToIndex.TryGetValue(flagName, out int v))   
                {
                    Debug.LogError($"[ZoneManager] ZoneFlags.HasFlag used with invalid flag string : {flagName}");
                    return false;
                }
                return this[v];
            }

            public void AddFlag(string flagName)
            {
                if (!NameToIndex.TryGetValue(flagName, out int v))                    
                    Debug.LogError($"[ZoneManager] ZoneFlags.AddFlag used with invalid flag string : {flagName}");
                else this[v] = true;
            }

            public void RemoveFlag(string flagName)
            {
                if (!NameToIndex.TryGetValue(flagName, out int v))                   
                    Debug.LogError($"[ZoneManager] ZoneFlags.RemoveFlag used with invalid flag string : {flagName}");
                else this[v] = false;
            }

            public void SetFlags(params string[] array)
            {
                List<int> list = Pool.Get<List<int>>();
                for (int i = 0; i < array.Length; i++)
                {
                    if (NameToIndex.TryGetValue(array[i], out int v))                        
                        Debug.Log($"[ZoneManager] ZoneFlags.SetFlags used with invalid flag string : {array[i]}");
                    else list.Add(v);
                }

                for (int i = 0; i < _bitArray.Length; i++)
                {
                    this[i] = list.Contains(i);
                }
                Pool.FreeUnmanaged(ref list);
            }

            public void AddFlags(ZoneFlags zoneFlags)
            {
                for (int i = 0; i < zoneFlags._bitArray.Length; i++)
                {
                    if (zoneFlags._bitArray[i])
                        this[i] = true;
                }                
            }

            public void RemoveFlags(ZoneFlags zoneFlags)
            {
                for (int i = 0; i < zoneFlags._bitArray.Length; i++)
                {
                    if (zoneFlags._bitArray[i])
                        this[i] = false;
                }
            }

            public bool CompareTo(ZoneFlags zoneFlags)
            {
                for (int i = 0; i < _bitArray.Length; i++)
                {
                    if (this[i] != zoneFlags[i])
                        return false;
                }

                return true;
            }

            public override string ToString()
            {
                sb.Clear();

                foreach (KeyValuePair<string, int> flag in NameToIndex)
                {
                    if (HasFlag(flag.Value))
                    {
                        sb.Append(sb.Length == 0 ? flag.Key : ", " + flag.Key);
                    }
                }

                return sb.ToString();
            }
        }

        private void AddFlag(Zone zone, int flag)
        {
            zone.definition.Flags.AddFlag(flag);

            if (NeedsUpdateSubscriptions())
                UpdateHookSubscriptions();

            zone.Reset();
        }

        private void RemoveFlag(Zone zone, int flag)
        {
            zone.definition.Flags.RemoveFlag(flag);

            if (NeedsUpdateSubscriptions())
            {
                UnsubscribeAll();
                UpdateHookSubscriptions();
            }

            zone.Reset();
        }

        private bool HasFlag(Zone zone, int flag) => zone.definition.Flags.HasFlag(flag) && ! zone.disabledFlags.HasFlag(flag);
                
        private void UpdateZoneEntityFlags(Zone zone)
        {
            foreach (EntityZones entityZones in zone.entityZones.Values)
                entityZones?.UpdateFlags();
            
            /*for (int i = 0; i < zonedPlayers.Count; i++)
            {
                EntityZones entityZones = zonedPlayers.ElementAt(i).Value;

                if (entityZones.Zones.Contains(zone))
                {
                    entityZones.UpdateFlags();
                }
            }

            for (int i = 0; i < zonedEntities.Count; i++)
            {
                EntityZones entityZones = zonedEntities.ElementAt(i).Value;

                if (entityZones.Zones.Contains(zone))
                {
                    entityZones.UpdateFlags();
                }
            }*/
        }
        #endregion

        #region Hook Subscriptions
        private bool HasGlobalFlag(int flag) => globalFlags.HasFlag(flag);

        private void UpdateGlobalFlags()
        {
            globalFlags.Clear();

            foreach (Zone zone in zones.Values)
            {
                if (!zone)
                    continue;

                globalFlags.AddFlags(zone.definition.Flags);
            }
        }

        private bool NeedsUpdateSubscriptions()
        {
            tempFlags.Clear();

            foreach (Zone zone in zones.Values)
            {
                if (!zone)
                    continue;

                tempFlags.AddFlags(zone.definition.Flags);
            }

            bool isMatch = tempFlags.CompareTo(globalFlags);

            return !isMatch;
        }

        private void UpdateHookSubscriptions()
        {
            UpdateGlobalFlags();

            if (HasGlobalFlag(ZoneFlags.NoBuild) || HasGlobalFlag(ZoneFlags.NoCup) || HasGlobalFlag(ZoneFlags.NoDeploy))
                Subscribe(nameof(OnEntityBuilt));

            if (HasGlobalFlag(ZoneFlags.NoUpgrade))
                Subscribe(nameof(OnStructureUpgrade));

            if (HasGlobalFlag(ZoneFlags.NoDeploy))
                Subscribe(nameof(OnItemDeployed));

            if (HasGlobalFlag(ZoneFlags.InfiniteTrapAmmo) || HasGlobalFlag(ZoneFlags.AlwaysLights) || HasGlobalFlag(ZoneFlags.AutoLights))
                Subscribe(nameof(OnItemUse));

            if (HasGlobalFlag(ZoneFlags.NoChat))
                Subscribe(nameof(OnPlayerChat));

            if (HasGlobalFlag(ZoneFlags.NoSuicide))
                Subscribe(nameof(OnServerCommand));

            if (HasGlobalFlag(ZoneFlags.KillSleepers) || HasGlobalFlag(ZoneFlags.EjectSleepers))
                Subscribe(nameof(OnPlayerDisconnected));

            if (HasGlobalFlag(ZoneFlags.NoFallDamage) || HasGlobalFlag(ZoneFlags.SleepGod) || HasGlobalFlag(ZoneFlags.PvpGod) || HasGlobalFlag(ZoneFlags.PveGod) || HasGlobalFlag(ZoneFlags.NoPve) || HasGlobalFlag(ZoneFlags.UnDestr) || HasGlobalFlag(ZoneFlags.NoBuildingDamage))
                Subscribe(nameof(OnEntityTakeDamage));

            if (HasGlobalFlag(ZoneFlags.NoWounded))
                Subscribe(nameof(CanBeWounded));

            if (HasGlobalFlag(ZoneFlags.NoSignUpdates))
                Subscribe(nameof(CanUpdateSign));

            if (HasGlobalFlag(ZoneFlags.NoOvenToggle))
                Subscribe(nameof(OnOvenToggle));

            if (HasGlobalFlag(ZoneFlags.NoVending))
                Subscribe(nameof(CanUseVending));

            if (HasGlobalFlag(ZoneFlags.NoStash))
                Subscribe(nameof(CanHideStash));

            if (HasGlobalFlag(ZoneFlags.NoCraft))
                Subscribe(nameof(CanCraft));

            if (HasGlobalFlag(ZoneFlags.NoDoorAccess))
                Subscribe(nameof(OnDoorOpened));

            if (HasGlobalFlag(ZoneFlags.NoVoice))
                Subscribe(nameof(OnPlayerVoice));

            if (HasGlobalFlag(ZoneFlags.NoPlayerLoot))
            {
                Subscribe(nameof(CanLootPlayer));
                Subscribe(nameof(OnLootPlayer));
            }

            if (HasGlobalFlag(ZoneFlags.NoVehicleMounting))
                Subscribe(nameof(CanMountEntity));

            if (HasGlobalFlag(ZoneFlags.NoVehicleDismounting))
                Subscribe(nameof(CanDismountEntity));

            if (HasGlobalFlag(ZoneFlags.LootSelf) || HasGlobalFlag(ZoneFlags.NoPlayerLoot))
                Subscribe(nameof(OnLootEntity));

            if (HasGlobalFlag(ZoneFlags.LootSelf) || HasGlobalFlag(ZoneFlags.NoPlayerLoot) || HasGlobalFlag(ZoneFlags.NoBoxLoot) || HasGlobalFlag(ZoneFlags.NoGather))
                Subscribe(nameof(CanLootEntity));

            if (HasGlobalFlag(ZoneFlags.NoEntityPickup))
            {
                Subscribe(nameof(CanPickupEntity));
                Subscribe(nameof(CanPickupLock));
                Subscribe(nameof(OnItemPickup));
            }

            if (HasGlobalFlag(ZoneFlags.NoGather))
            {
                Subscribe(nameof(OnCollectiblePickup));
                Subscribe(nameof(OnGrowableGather));
                Subscribe(nameof(OnDispenserGather));
            }

            if (HasGlobalFlag(ZoneFlags.NoTurretTargeting))
                Subscribe(nameof(OnTurretTarget));

            if (HasGlobalFlag(ZoneFlags.NoAPCTargeting))
                Subscribe(nameof(CanBradleyApcTarget));

            if (HasGlobalFlag(ZoneFlags.NoHeliTargeting))
            {
                Subscribe(nameof(CanHelicopterTarget));
                Subscribe(nameof(CanHelicopterStrafeTarget));
                Subscribe(nameof(OnHelicopterTarget));
            }

            if (HasGlobalFlag(ZoneFlags.NoNPCTargeting))
                Subscribe(nameof(OnNpcTarget));
        }

        private void UnsubscribeAll()
        {
            Unsubscribe(nameof(OnEntityBuilt));
            Unsubscribe(nameof(OnStructureUpgrade));
            Unsubscribe(nameof(OnItemDeployed));
            Unsubscribe(nameof(OnItemUse));
            Unsubscribe(nameof(OnPlayerChat));
            Unsubscribe(nameof(OnServerCommand));
            Unsubscribe(nameof(OnPlayerDisconnected));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(CanBeWounded));
            Unsubscribe(nameof(CanUpdateSign));
            Unsubscribe(nameof(OnOvenToggle));
            Unsubscribe(nameof(CanUseVending));
            Unsubscribe(nameof(CanHideStash));
            Unsubscribe(nameof(CanCraft));
            Unsubscribe(nameof(OnDoorOpened));
            Unsubscribe(nameof(CanLootPlayer));
            Unsubscribe(nameof(OnLootPlayer));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(CanPickupLock));
            Unsubscribe(nameof(OnItemPickup));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnCollectiblePickup));
            Unsubscribe(nameof(OnGrowableGather));
            Unsubscribe(nameof(OnDispenserGather));
            Unsubscribe(nameof(OnTurretTarget));
            Unsubscribe(nameof(CanBradleyApcTarget));
            Unsubscribe(nameof(CanHelicopterTarget));
            Unsubscribe(nameof(CanHelicopterStrafeTarget));
            Unsubscribe(nameof(OnHelicopterTarget));
            Unsubscribe(nameof(OnNpcTarget));
            Unsubscribe(nameof(OnPlayerVoice));
            Unsubscribe(nameof(CanMountEntity));
            Unsubscribe(nameof(CanDismountEntity));
        }
        #endregion

        #region Zone Creation
        private void UpdateZoneDefinition(Zone zone, string[] args, BasePlayer player = null)
        {
            for (int i = 0; i < args.Length; i += 2)
            {
                string value;
                switch (args[i].ToLower())
                {
                    case "name":
                        value = zone.definition.Name = args[i + 1];
                        break;

                    case "id":
                        string newId = args[i + 1];
                        if (newId != zone.definition.Id && !zones.ContainsKey(newId))
                        {
                            string oldId = zone.definition.Id;
                            zones.Remove(oldId);
                            zone.definition.Id = newId;
                            zones.Add(newId, zone);

                            if (zone.definition.Owner && temporaryZones.TryGetValue(zone.definition.Owner, out HashSet<string> set))
                            {
                                set.Remove(oldId);
                                set.Add(newId);
                            }
                        }
                        value = newId;
                        break;

                    case "comfort":
                        if (float.TryParse(args[i + 1], out float comfort))
                            zone.definition.Comfort = comfort;
                        
                        value = zone.definition.Comfort.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "temperature":
                        if (float.TryParse(args[i + 1], out float temperature))
                            zone.definition.Temperature = temperature;
                        
                        value = zone.definition.Temperature.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "radiation":
                        if (float.TryParse(args[i + 1], out float radiation))
                            zone.definition.Radiation = radiation;
                        
                        value = zone.definition.Radiation.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "safezone":
                        if (bool.TryParse(args[i + 1], out bool safeZone))
                            zone.definition.SafeZone = safeZone;
                            
                        value = zone.definition.SafeZone.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "radius":
                        if (float.TryParse(args[i + 1], out float radius))
                        {
                            zone.definition.Radius = radius;
                            zone.definition.Size = Vector3.zero;
                        }
                        value = zone.definition.Radius.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "rotation":
                        if (float.TryParse(args[i + 1], out float rotation))
                            zone.definition.Rotation = Quaternion.AngleAxis(rotation, Vector3.up).eulerAngles;
                        else if (player)
                            zone.definition.Rotation = new Vector3(0, player.GetNetworkRotation().eulerAngles.y, 0);
                        
                        value = zone.definition.Rotation.ToString();
                        break;

                    case "location":
                        if (player && args[i + 1].Equals("here", StringComparison.OrdinalIgnoreCase))
                            zone.definition.Location = player.transform.position;
                        else
                        {
                            string[] location = args[i + 1].Trim().Split(' ');
                            if (location.Length == 3 && float.TryParse(location[0], out float lX) && float.TryParse(location[1], out float lY) && float.TryParse(location[2], out float lZ))
                                zone.definition.Location = new Vector3(lX, lY, lZ);
                            else if (player)
                            {
                                SendMessage(player, "Invalid location format. Correct syntax is \"/zone location \"x y z\"\" - or - \"/zone location here\"");
                                continue;
                            }
                        }

                        value = zone.definition.Location.ToString();
                        break;

                    case "size":
                        string[] size = args[i + 1].Trim().Split(' ');
                        if (size.Length == 3 && float.TryParse(size[0], out float sX) && float.TryParse(size[1], out float sY) && float.TryParse(size[2], out float sZ))
                            zone.definition.Size = new Vector3(sX, sY, sZ);
                        else if (player)
                        {
                            SendMessage(player, "Invalid size format, Correct syntax is \"/zone size \"x y z\"\"");
                            continue;
                        }

                        value = zone.definition.Size.ToString();
                        break;

                    case "enter_message":
                        value = zone.definition.EnterMessage = args[i + 1];
                        break;

                    case "leave_message":
                        value = zone.definition.LeaveMessage = args[i + 1];
                        break;

                    case "parentid":
                        value = args[i + 1];
                        if (zones.TryGetValue((string)value, out Zone parent))
                        {
                            zone.definition.ParentID = (string)value;
                            zone.parent = parent;

                            UpdateZoneEntityFlags(zone);
                        }
                        else if (player)
                        {
                            SendMessage(player, $"Unable to find zone with ID {value}");
                            continue;
                        }
                        break;

                    case "permission":
                        string permission = args[i + 1];

                        if (!permission.StartsWith("zonemanager."))
                            permission = $"zonemanager.{permission}";

                        value = zone.definition.Permission = permission;
                        break;

                    case "ejectspawns":
                        value = zone.definition.EjectSpawns = args[i + 1];
                        break;

                    case "enabled":
                    case "enable":
                        if (bool.TryParse(args[i + 1], out bool enabled))
                            zone.definition.Enabled = enabled;

                        value = enabled.ToString();
                        break;

                    default:
                        if (!bool.TryParse(args[i + 1], out bool active))
                            active = false;

                        value = active.ToString();

                        if (ZoneFlags.Find(args[i], out int v))
                        {
                            if (active)
                                zone.AddFlag(v);
                            else zone.RemoveFlag(v);
                        }
                        else if (player)
                            SendMessage(player, $"Invalid zone flag: {args[i]}");
                        break;
                }
                if (player)
                    SendMessage(player, $"{args[i]} set to {value}");
            }
        }
        #endregion

        #region Commands
        
        private StringBuilder _sb = new StringBuilder();
        
        [ChatCommand("zone_add")]
        private void cmdChatZoneAdd(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            string zoneId = GetRandomZoneID();

            if (!CreateOrUpdateZone(zoneId, Array.Empty<string>(), player.transform.position))
            {
                SendMessage(player, $"Unable to create zone with ID {zoneId}");
                return;
            }

            lastPlayerZone[player.userID] = zoneId;

            ShowZone(player, zoneId);

            SendMessage(player, "You have successfully created a new zone with ID : {0}!\nYou can edit it using the /zone_edit command", zoneId);
        }

        private string GetRandomZoneID()
        {
            string zoneId = UnityEngine.Random.Range(1, 99999999).ToString();
            if (zones.ContainsKey(zoneId))
                return GetRandomZoneID();

            return zoneId;
        }

        [ChatCommand("zone_wipe")]
        private void cmdChatZoneReset(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            List<string> zoneIds = Pool.Get<List<string>>();
            foreach (KeyValuePair<string, Zone> kvp in zones)
            {
                if (kvp.Value.definition.IsTemporary)
                    continue;
                
                zoneIds.Add(kvp.Key);
            }
            
            EraseZones(zoneIds);
            
            Pool.FreeUnmanaged(ref zoneIds);

            updateBehaviour.Reset();

            SaveData();

            SendMessage(player, "Wiped zone data");
        }

        [ChatCommand("zone_remove")]
        private void cmdChatZoneRemove(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            if (args.Length == 0)
            {
                SendMessage(player, "Invalid syntax! /zone_remove <zone ID>");
                return;
            }

            bool result = EraseZone(args[0]);
            if (!result)
            {
                SendMessage(player, "A zone with the specified ID does not exist");
                return;
            }

            SendMessage(player, "Successfully removed zone : {0}", args[0]);
        }

        [ChatCommand("zone_stats")]
        private void cmdChatZoneStats(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            SendMessage(player, "Zones : {0}", zones.Count);
            SendMessage(player, "Players in Zones: {0}", zonedPlayers.Count);
            SendMessage(player, "Entities in Zones: {0}", zonedEntities.Count);
        }

        [ChatCommand("zone_edit")]
        private void cmdChatZoneEdit(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            string zoneId;
            if (args.Length == 0)
            {
                if (!zonedPlayers.TryGetValue(player.userID, out EntityZones entityZones) || entityZones.Count != 1)
                {
                    SendMessage(player, "You must enter a zone ID. /zone_edit <zone ID>");
                    return;
                }
                zoneId = entityZones.Zones[0].definition.Id;
            }
            else zoneId = args[0];

            if (!zones.ContainsKey(zoneId))
            {
                SendMessage(player, "The specified zone does not exist");
                return;
            }

            lastPlayerZone[player.userID] = zoneId;

            SendMessage(player, "You are now editing the zone with ID : {0}", zoneId);
            ShowZone(player, zoneId);
        }

        [ChatCommand("zone_list")]
        private void cmdChatZoneList(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            if (zones.Count == 0)
            {
                SendMessage(player, "No zone have been created");
                return;
            }

            _sb.Clear();

            void AppendLine(StringBuilder sb, Zone zone)
            {
                sb.Append($"<color={Configuration.Notifications.Color}>ID:</color> {zone.definition.Id} ");
                
                if (!string.IsNullOrEmpty(zone.definition.Name))
                    sb.Append($" (<color={Configuration.Notifications.Color}>{zone.definition.Name}</color>) ");

                sb.Append($" - {zone.definition.Location} ");
                
                sb.Append("\n");
            }

            _sb.AppendLine($"<size=13><color={Configuration.Notifications.Color}>Zones:</color>");
            
            foreach (Zone zone in zones.Values)
            {
                if (zone.definition.IsTemporary)
                    continue;
                
                AppendLine(_sb, zone);
            }
            
            foreach (KeyValuePair<Plugin, HashSet<string>> kvp in temporaryZones)
            {
                _sb.AppendLine($"<color={Configuration.Notifications.Color}>{kvp.Key.Name} Temporary Zones:</color>");
                foreach (string zoneId in kvp.Value)
                {
                    if (zones.TryGetValue(zoneId, out Zone zone))
                        AppendLine(_sb, zone);
                }
            }
            
            player.ChatMessage(_sb.ToString());
            //SendMessage(player, $"ID: {zone.Key} - {zone.Value.definition.Name} - {zone.Value.definition.Location}");
        }
        
        [ChatCommand("zone")]
        private void cmdChatZone(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            if (!lastPlayerZone.TryGetValue(player.userID, out string zoneId))
            {
                SendMessage(player, "You must start editing a zone first. /zone_edit <zone ID>");
                return;
            }

            if (!zones.TryGetValue(zoneId, out Zone zone))
            {
                SendMessage(player, "Unable to find a zone with ID : {0}", zoneId);
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage("/zone <option> <value>");
                
                void AppendLine(StringBuilder sb, string field, object value)
                {
                    sb.AppendLine($"<color={Configuration.Notifications.Color}>{field}:</color> {value}");
                }
                
                _sb.Clear();
                AppendLine(_sb, "<size=13>ID", zone.definition.Id);
                AppendLine(_sb, "Name", zone.definition.Name);
                AppendLine(_sb, "Enabled", zone.definition.Enabled);
                
                if (!string.IsNullOrEmpty(zone.definition.ParentID))
                    AppendLine(_sb, "Parent Zone ID", zone.definition.ParentID);
                
                if (!string.IsNullOrEmpty(zone.definition.Permission))
                    AppendLine(_sb, "Permission", zone.definition.Permission);
                
                AppendLine(_sb, "Location", zone.definition.Location);
                if (zone.definition.Size == Vector3.zero)
                    AppendLine(_sb, "Radius", zone.definition.Radius);
                else
                {
                    AppendLine(_sb, "Size", zone.definition.Size);
                    AppendLine(_sb, "Rotation", zone.definition.Rotation);
                }
                
                if (!string.IsNullOrEmpty(zone.definition.EjectSpawns))
                    AppendLine(_sb, "Eject Spawns", zone.definition.EjectSpawns);
                
                AppendLine(_sb, "Comfort", zone.definition.Comfort);
                AppendLine(_sb, "Temperature", zone.definition.Temperature);
                AppendLine(_sb, "Radiation", zone.definition.Radiation);
                AppendLine(_sb, "Safe Zone", zone.definition.SafeZone);
                
                AppendLine(_sb, "Enter Message", zone.definition.EnterMessage);
                AppendLine(_sb, "Leave Message", zone.definition.LeaveMessage);
                
                AppendLine(_sb, "Flags", zone.definition.Flags);
                
                player.ChatMessage(_sb.ToString());
                
                ShowZone(player, zoneId);
                return;
            }

            if (args[0].ToLower() == "flags")
            {
                OpenFlagEditor(player, zoneId);
                return;
            }

            if (args.Length % 2 != 0)
            {
                SendMessage(player, "Value missing. You must follow a option with a value");
                return;
            }
            UpdateZoneDefinition(zone, args, player);
            
            zone.Reset();
            
            Interface.CallHook("OnZoneUpdated", zoneId);
            
            if (!zone.definition.IsTemporary)
                SaveData();
            
            ShowZone(player, zoneId);
        }

        [ChatCommand("zone_flags")]
        private void cmdChatZoneFlags(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            if (!lastPlayerZone.TryGetValue(player.userID, out string zoneId))
            {
                SendMessage(player, "You must start editing a zone first. /zone_edit <zone ID>");
                return;
            }

            OpenFlagEditor(player, zoneId);
        }

        [ChatCommand("zone_player")]
        private void cmdChatZonePlayer(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            BasePlayer targetPlayer = player;
            if (args is { Length: > 0 })
            {
                targetPlayer = BasePlayer.Find(args[0]);
                if (!targetPlayer)
                {
                    SendMessage(player, "Unable to find a player with the specified information");
                    return;
                }
            }

            if (!zonedPlayers.TryGetValue(targetPlayer.userID, out EntityZones entityZones))
            {
                SendReply(player, "The specified player is not in any zone");
                return;
            }

            SendMessage(player, $"--- {targetPlayer.displayName} ---");
            SendMessage(player, $"Has Flags: {entityZones.Flags.ToString()}");
            SendMessage(player, "Is in zones:");

            foreach (Zone zone in entityZones.Zones)
                SendMessage(player, $"{zone.definition.Id}: {zone.definition.Name} - {zone.definition.Location}");
        }

        private RaycastHit[] m_RaycastBuffer = new RaycastHit[64];
        
        [ChatCommand("zone_entity")]
        private void cmdChatZoneEntity(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PERMISSION_ZONE))
            {
                SendMessage(player, "You don't have access to this command");
                return;
            }

            BaseEntity baseEntity = null;
            int count = Physics.RaycastNonAlloc(player.eyes.HeadRay(), m_RaycastBuffer, 3f);

            for (int i = 0; i < count; i++)
            {
                RaycastHit raycastHit = m_RaycastBuffer[i];
                BaseEntity entity = raycastHit.GetEntity();
                if (entity && !entity.IsDestroyed)
                {
                    baseEntity = entity;
                    break;
                }
            }
            
            if (!baseEntity)
            {
                SendMessage(player, "No entity found");
                return;
            }

            if (!zonedEntities.TryGetValue(baseEntity.net.ID, out EntityZones entityZones))
            {
                SendReply(player, "The specified entity is not in any zone");
                return;
            }

            SendMessage(player, $"--- {baseEntity.ShortPrefabName} ({baseEntity.net.ID}) ---");
            SendMessage(player, $"Has Flags: {entityZones.Flags.ToString()}");
            SendMessage(player, "Is in zones:");

            foreach (Zone zone in entityZones.Zones)
                SendMessage(player, $"{zone.definition.Id}: {zone.definition.Name} - {zone.definition.Location}");
        }

        [ConsoleCommand("zone")]
        private void ccmdZone(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg, PERMISSION_ZONE))
            {
                SendReply(arg, "You don't have access to this command");
                return;
            }

            string zoneId = arg.GetString(0);
            if (!arg.HasArgs(3) || !zones.TryGetValue(zoneId, out Zone zone))
            {
                SendReply(arg, "Zone ID not found or too few arguments supplied: zone <zoneid> <arg> <value>");
                return;
            }

            string[] args = new string[arg.Args.Length - 1];
            Array.Copy(arg.Args, 1, args, 0, args.Length);

            UpdateZoneDefinition(zone, args, arg.Connection?.player as BasePlayer);
            zone.Reset();
            
            Interface.CallHook("OnZoneUpdated", zoneId);
            if (!zone.definition.IsTemporary)
                SaveData();
        }
        
        [ConsoleCommand("zone_list")]
        private void ccmdChatZoneList(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg, PERMISSION_ZONE))
            {
                SendReply(arg, "You don't have access to this command");
                return;
            }

            SendReply(arg, "--- Zone list ---");
            if (zones.Count == 0)
            {
                SendReply(arg, "None...");
                return;
            }

            foreach (KeyValuePair<string, Zone> zone in zones)
                SendReply(arg, $"ID: {zone.Key} - {zone.Value.definition.Name} - {zone.Value.definition.Location}");
        }
        #endregion

        #region UI
        const string ZMUI = "zmui.editor";
        #region Helper
        public static class UI
        {
            public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = {Color = color},
                            RectTransform = {AnchorMin = min, AnchorMax = max},
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }
            public static void Panel(ref CuiElementContainer container, string panel, string color, string min, string max, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    CursorEnabled = cursor
                },
                panel);
            }
            public static void Label(ref CuiElementContainer container, string panel, string text, int size, string min, string max, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = min, AnchorMax = max }
                },
                panel);

            }
            public static void Button(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    Text = { Text = text, FontSize = size, Align = align }
                },
                panel);
            }
            public static string Color(string hexColor, float alpha)
            {
                if (hexColor.StartsWith("#"))
                    hexColor = hexColor.Substring(1);
                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }
        #endregion

        #region Creation
        private const string COLOR1 = "0.168 0.168 0.168 0.9";
        private const string COLOR2 = "0.847 0.333 0.256 1";
        private const string COLOR3 = "0.447 0.898 0.447 1";

        private SortedDictionary<string, int> orderedZoneFlags;
        
        private void OpenFlagEditor(BasePlayer player, string zoneId)
        {
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
            {
                SendReply(player, $"Error getting zone object with ID: {zoneId}");
                CuiHelper.DestroyUi(player, ZMUI);
            }

            orderedZoneFlags ??= new SortedDictionary<string, int>(ZoneFlags.NameToIndex);

            CuiElementContainer container = UI.Container(ZMUI, COLOR1, "0 0", "1 1", true);
            UI.Label(ref container, ZMUI, $"Zone Flag Editor", 18, "0 0.92", "1 1");
            UI.Label(ref container, ZMUI, $"Zone ID: {zoneId}\nName: {zone.definition.Name}\n{(zone.definition.Size != Vector3.zero ? $"Box Size: {zone.definition.Size}\nRotation: {zone.definition.Rotation}" : $"Radius: {zone.definition.Radius}\nSafe Zone: {zone.definition.SafeZone}")}", 13, "0.05 0.8", "1 0.92", TextAnchor.UpperLeft);
            UI.Label(ref container, ZMUI, $"Comfort: {zone.definition.Comfort}\nRadiation: {zone.definition.Radiation}\nTemperature: {zone.definition.Temperature}\nZone Enabled: {zone.definition.Enabled}", 13, "0.25 0.8", "1 0.92", TextAnchor.UpperLeft);
            UI.Label(ref container, ZMUI, $"Permission: {zone.definition.Permission}\nEject Spawnfile: {zone.definition.EjectSpawns}\nEnter Message: {zone.definition.EnterMessage}\nExit Message: {zone.definition.LeaveMessage}", 13, "0.5 0.8", "1 0.92", TextAnchor.UpperLeft);
            UI.Button(ref container, ZMUI, COLOR2, "Exit", 12, "0.95 0.96", "0.99 0.99", $"zmui.editflag {zoneId} exit");

            int count = 0;

            foreach(KeyValuePair<string, int> kvp in orderedZoneFlags)
            {                
                bool value = zone.definition.Flags.HasFlag(kvp.Value);

                Vector4 position = GetButtonPosition(count);

                UI.Label(ref container, ZMUI, kvp.Key, 12, $"{position[0]} {position[1]}", $"{position[0] + ((position[2] - position[0]) / 2)} {position[3]}");
                UI.Button(ref container, ZMUI, value ? COLOR3 : COLOR2, value ? "Enabled" : "Disabled", 12, $"{position[0] + ((position[2] - position[0]) / 2)} {position[1]}", $"{position[2]} {position[3]}", $"zmui.editflag {zoneId} {kvp.Value} {!value}");

                count++;
            }

            CuiHelper.DestroyUi(player, ZMUI);
            CuiHelper.AddUi(player, container);
        }

        private Vector4 GetButtonPosition(int i)
        {
            int column = i == 0 ? 0 : ColumnNumber(4, i);
            int row = i - (column * 4);

            float offsetX = 0.04f + ((0.01f + 0.21f) * row);
            float offsetY = (0.76f - (column * 0.04f));

            return new Vector4(offsetX, offsetY, offsetX + 0.21f, offsetY + 0.03f);
        }

        private int ColumnNumber(int max, int count) => Mathf.FloorToInt(count / max);
        #endregion

        #region Commands
        [ConsoleCommand("zmui.editflag")]
        private void ccmdEditFlag(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null)
                return;
            
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (!player)
                return;

            string zoneId = arg.GetString(0);
            Zone zone = GetZoneByID(zoneId);
            if (!zone)
            {
                SendReply(player, $"Error getting zone object with ID: {zoneId}");
                CuiHelper.DestroyUi(player, ZMUI);
            }

            if (arg.GetString(1) == "exit")
            {
                CuiHelper.DestroyUi(player, ZMUI);
                
                Interface.CallHook("OnZoneUpdated", zoneId);
                
                if (!zone.definition.IsTemporary)
                    SaveData();

                NextTick(() =>
                {
                    if (NeedsUpdateSubscriptions())
                    {
                        UnsubscribeAll();
                        UpdateHookSubscriptions();
                    }
                    
                    UpdateZoneEntityFlags(zone);
                });
            }
            else
            {
                if (arg.GetBool(2))
                    zone.AddFlag(arg.GetInt(1));
                else zone.RemoveFlag(arg.GetInt(1));

                OpenFlagEditor(player, zoneId);
            }
        }
        #endregion
        #endregion

        #region Config        
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Autolight Options")]
            public AutoLightOptions AutoLights { get; set; }

            [JsonProperty(PropertyName = "Notification Options")]
            public NotificationOptions Notifications { get; set; }

            [JsonProperty(PropertyName = "NPC players can deal player damage in zones with PvpGod flag")]
            public bool NPCHurtPvpGod { get; set; }

            [JsonProperty(PropertyName = "Allow decay damage in zones with Undestr flag")]
            public bool DecayDamageUndestr { get; set; }

            public class AutoLightOptions
            {
                [JsonProperty(PropertyName = "Time to turn lights on")]
                public float OnTime { get; set; }

                [JsonProperty(PropertyName = "Time to turn lights off")]
                public float OffTime { get; set; }

                [JsonProperty(PropertyName = "Lights require fuel to activate automatically")]
                public bool RequiresFuel { get; set; }
            }

            public class NotificationOptions
            {
                [JsonProperty(PropertyName = "Display notifications via PopupNotifications")]
                public bool Popups { get; set; }

                [JsonProperty(PropertyName = "Chat prefix")]
                public string Prefix { get; set; }

                [JsonProperty(PropertyName = "Chat color (hex)")]
                public string Color { get; set; }
            }

            public Oxide.Core.VersionNumber Version { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = Config.ReadObject<ConfigData>();

            if (Configuration.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(Configuration, true);
        }

        protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                AutoLights = new ConfigData.AutoLightOptions
                {
                    OnTime = 18f,
                    OffTime = 6f,
                    RequiresFuel = true
                },
                Notifications = new ConfigData.NotificationOptions
                {
                    Color = "#d85540",
                    Popups = false,
                    Prefix = "[Zone Manager] :"
                },
                NPCHurtPvpGod = false,
                DecayDamageUndestr = false,
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (Configuration.Version < new VersionNumber(3, 0, 0))
                Configuration = baseConfig;

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }
        #endregion

        #region Data Management
        private void SaveData()
        {
            storedData.definitions.Clear();

            foreach (KeyValuePair<string, Zone> zone in zones)
            {
                if (zone.Value.definition.IsTemporary)
                    continue;
                
                storedData.definitions.Add(zone.Value.definition);
            }

            data.WriteObject(storedData);
        }

        private void LoadData()
        {
            data = Interface.Oxide.DataFileSystem.GetFile("ZoneManager/zone_data");
            data.Settings.Converters = new JsonConverter[] { new StringEnumConverter(), new Vector3Converter(), new ZoneFlagsConverter() };

            storedData = data.ReadObject<StoredData>() ?? new StoredData();
        }

        private class StoredData
        {
            public HashSet<Zone.Definition> definitions = new HashSet<Zone.Definition>();
        }

        public class EntityZones
        {
            public ZoneFlags Flags { get; private set; }

            public List<Zone> Zones { get; private set; }
            

            public EntityZones()
            {
                Zones = new List<Zone>();
                Flags = new ZoneFlags();
            }

            public void AddFlags(ZoneFlags zoneFlags) => Flags.AddFlags(zoneFlags);
                        
            public void RemoveFlags(ZoneFlags zoneFlags) => Flags.RemoveFlags(zoneFlags);
                        
            public bool HasFlag(int flag) => Flags.HasFlag(flag);
            
            public void UpdateFlags()
            {
                Flags.Clear();

                foreach (Zone zone in Zones)
                {
                    if (!zone)
                        continue;

                    tempFlags.Clear();

                    tempFlags.AddFlags(zone.definition.Flags);
                    tempFlags.RemoveFlags(zone.disabledFlags);

                    AddFlags(tempFlags);
                }

                foreach (Zone zone in Zones)
                {
                    if (!zone)
                        continue;

                    if (zone.parent && Zones.Contains(zone.parent))
                        RemoveFlags(zone.parent.definition.Flags);
                }
            }

            public bool EnterZone(Zone zone)
            {
                if (Zones.Contains(zone))
                    return false;
                
                Zones.Add(zone);
                return true;
            }

            public bool LeaveZone(Zone zone) => Zones.Remove(zone);

            public bool IsInZone(Zone zone) => Zones.Contains(zone);

            public bool IsInZone(string zoneId)
            {
                foreach (Zone zone in Zones)
                {
                    if (zone && zone.definition.Id == zoneId)
                        return true;
                }

                return false;
            }

            public bool ShouldRemove() => Count == 0;

            public int Count => Zones.Count;
        }

        private class Vector3Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                Vector3 vector = (Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.String)
                {
                    string[] values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                }
                JObject o = JObject.Load(reader);
                return new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }

        private class ZoneFlagsConverter : JsonConverter
        {
            private const string SEPERATOR = ", ";

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                ZoneFlags zoneFlags = (ZoneFlags)value;

                sb.Clear();

                foreach(KeyValuePair<string, int> flag in ZoneFlags.NameToIndex)
                {
                    if (zoneFlags.HasFlag(flag.Value))
                    {
                        sb.Append(sb.Length == 0 ? flag.Key : SEPERATOR + flag.Key);
                    }
                }

                writer.WriteValue(sb.ToString());
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                ZoneFlags zoneFlags = new ZoneFlags();

                if (reader.TokenType == JsonToken.String)
                {
                    string[] values = reader.Value.ToString().Split(',');
                    for (int i = 0; i < values.Length; i++)
                    {
                        string value = values[i].Trim();

                        if (ZoneFlags.NameToIndex.TryGetValue(value, out int v))                        
                            zoneFlags.AddFlag(v);                       
                    }
                }
                return zoneFlags;
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(ZoneFlags);
            }
        }
        #endregion

        #region Localization
        private string Message(string key, string playerId = null) => lang.GetMessage(key, this, playerId);

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["noBuild"] = "You are not allowed to build in this area!",
            ["noUpgrade"] = "You are not allowed to upgrade structures in this area!",
            ["noDeploy"] = "You are not allowed to deploy items in this area!",
            ["noCup"] = "You are not allowed to deploy cupboards in this area!",
            ["noChat"] = "You are not allowed to chat in this area!",
            ["noSuicide"] = "You are not allowed to suicide in this area!",
            ["noGather"] = "You are not allowed to gather in this area!",
            ["noLoot"] = "You are not allowed loot in this area!",
            ["noSignUpdates"] = "You can not update signs in this area!",
            ["noOvenToggle"] = "You can not toggle ovens and lights in this area!",
            ["noPickup"] = "You can not pick up objects in this area!",
            ["noVending"] = "You can not use vending machines in this area!",
            ["noStash"] = "You can not hide a stash in this area!",
            ["noCraft"] = "You can not craft in this area!",
            ["eject"] = "You are not allowed in this area!",
            ["attract"] = "You are not allowed to leave this area!",
            ["kill"] = "Access to this area is restricted!",
            ["noVoice"] = "You are not allowed to voice chat in this area!",
            ["novehiclesenter"] = "Vehicles are not allowed in this area!",
            ["novehiclesleave"] = "Vehicles are not allowed to leave this area!",
            ["novehiclemounting"] = "You are not allowed to mount vehicles in this area!",
            ["novehicledismounting"] = "You are not allowed to dismount vehicles in this area!",
            ["nosprays"] = "You are not allowed to spray paint in this area!"
        };
        #endregion
    }
}
