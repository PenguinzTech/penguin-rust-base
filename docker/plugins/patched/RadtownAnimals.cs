// PATCHED by penguin-rust-base: replace removed HierarchyUtil.GetRoot("Monument") with GameObject.Find("Monument")
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Oxide.Plugins
{
    [Info("Radtown Animals", "k1lly0u", "0.3.2"), Description("Spawns various NPC types at monuments")]
    public class RadtownAnimals : RustPlugin
    {
        #region Fields
        private readonly List<BaseCombatEntity> pluginSpawnedEntities = new List<BaseCombatEntity>();

        private readonly Hash<NPC, string> prefabLookup = new Hash<NPC, string>
        {
            [NPC.Bear] = "assets/rust.ai/agents/bear/bear.prefab",
            [NPC.Boar] = "assets/rust.ai/agents/boar/boar.prefab",
            [NPC.Chicken] = "assets/rust.ai/agents/chicken/chicken.prefab",
            [NPC.Stag] = "assets/rust.ai/agents/stag/stag.prefab",
            [NPC.Wolf] = "assets/rust.ai/agents/wolf/wolf.prefab",
            [NPC.Scarecrow] = "assets/prefabs/npc/scarecrow/scarecrow.prefab",
            [NPC.Scientist] = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab",
        };

        [JsonConverter(typeof(StringEnumConverter))]
        private enum NPC { Bear, Boar, Chicken, Stag, Wolf, Scarecrow, Scientist }
        #endregion

        #region Oxide Hooks 
        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void OnServerInitialized() => InitializeSpawns();

        private void OnEntityDeath(BaseCombatEntity baseCombatEntity, HitInfo hitInfo)
        {
            if (baseCombatEntity == null) 
                return;

            if (pluginSpawnedEntities.Contains(baseCombatEntity))
            {
                string prefabName = baseCombatEntity.PrefabName;
                Vector3 homePosition = GetHomePosition(baseCombatEntity);

                timer.In(Configuration.Settings.Respawn, () => Spawn(prefabName, homePosition));

                pluginSpawnedEntities.Remove(baseCombatEntity);
            }
        }

        private void Unload()
        {
            foreach (BaseCombatEntity baseCombatEntity in pluginSpawnedEntities)
            {
                if (baseCombatEntity != null && !baseCombatEntity.IsDead())
                    baseCombatEntity.Kill(BaseNetworkable.DestroyMode.None);
            }

            pluginSpawnedEntities.Clear();

            Configuration = null;
        }
        #endregion

        #region Initial Spawning
        private void InitializeSpawns()
        {
            Hash<string, ConfigData.Monuments.MonumentSettings> monumentLookup = new Hash<string, ConfigData.Monuments.MonumentSettings>
            {
                ["lighthouse"] = Configuration.MonumentSettings.Lighthouse,
                ["powerplant_1"] = Configuration.MonumentSettings.Powerplant,
                ["military_tunnel_1"] = Configuration.MonumentSettings.Tunnels,
                ["harbor_1"] = Configuration.MonumentSettings.LargeHarbor,
                ["harbor_2"] = Configuration.MonumentSettings.SmallHarbor,
                ["airfield_1"] = Configuration.MonumentSettings.Airfield,
                ["trainyard_1"] = Configuration.MonumentSettings.Trainyard,
                ["water_treatment_plant_1"] = Configuration.MonumentSettings.WaterTreatment,
                ["warehouse"] = Configuration.MonumentSettings.Warehouse,
                ["satellite_dish"] = Configuration.MonumentSettings.Satellite,
                ["sphere_tank"] = Configuration.MonumentSettings.Dome,
                ["radtown_small_3"] = Configuration.MonumentSettings.Radtown,
                ["launch_site_1"] = Configuration.MonumentSettings.RocketFactory,
                ["gas_station_1"] = Configuration.MonumentSettings.GasStation,
                ["supermarket_1"] = Configuration.MonumentSettings.Supermarket,
                ["mining_quarry_c"] = Configuration.MonumentSettings.Quarry_HQM,
                ["mining_quarry_a"] = Configuration.MonumentSettings.Quarry_Sulfur,
                ["mining_quarry_b"] = Configuration.MonumentSettings.Quarry_Stone,
                ["junkyard_1"] = Configuration.MonumentSettings.Junkyard
            };

            Transform root = GameObject.Find("Monument").transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                foreach (KeyValuePair<string, ConfigData.Monuments.MonumentSettings> kvp in monumentLookup)
                {
                    if (child.name.Contains(kvp.Key))
                    {
                        if (kvp.Value.Enabled)
                            ServerMgr.Instance.StartCoroutine(SpawnAnimals(child.position, kvp.Value.Counts));

                        break;
                    } 
                }
            }
        }

        private IEnumerator SpawnAnimals(Vector3 position, Hash<NPC,int> spawnCounts)
        {
            foreach (KeyValuePair<NPC, int> kvp in spawnCounts)
            {
                for (int i = 0; i < kvp.Value; i++)
                {
                    if (pluginSpawnedEntities.Count >= Configuration.Settings.Total)
                    {
                        Puts(lang.GetMessage("Notification.SpawnLimit", this));
                        yield break;
                    }

                    Spawn(kvp.Key, position);

                    yield return CoroutineEx.waitForSeconds(0.5f);
                }
            }
        }
        #endregion

        #region Spawning
        private void Spawn(NPC npc, Vector3 position) => Spawn(prefabLookup[npc], position);

        private void Spawn(string prefab, Vector3 position)
        {
            Vector2 random = Random.insideUnitCircle * Configuration.Settings.Spread;
            Vector3 spawnPosition = new Vector3(position.x + random.x, position.y, position.z + random.y);

            if (!NavmeshSpawnPoint.Find(spawnPosition, 20f, out spawnPosition))
            {
                timer.In(Configuration.Settings.Respawn, () => Spawn(prefab, position));
                return;
            }

            BaseCombatEntity baseCombatEntity = InstantiateEntity(prefab, spawnPosition);
            baseCombatEntity.enableSaving = false;

            NavMeshAgent navMeshAgent = baseCombatEntity.GetComponent<NavMeshAgent>();
            if (navMeshAgent != null)
            {
                const int AREA_MASK = 1;
                const int AGENT_TYPE_ID = -1372625422;

                navMeshAgent.agentTypeID = AGENT_TYPE_ID;
                navMeshAgent.areaMask = AREA_MASK;
            }

            BaseNavigator baseNavigator = baseCombatEntity.GetComponent<BaseNavigator>();
            if (baseNavigator != null)
            {
                const string WALKABLE = "Walkable";

                baseNavigator.DefaultArea = WALKABLE;

                baseNavigator.MaxRoamDistanceFromHome = Configuration.Settings.Spread;
                baseNavigator.BestRoamPointMaxDistance = Configuration.Settings.Spread * 0.5f;

                if (baseNavigator.topologyPreference == 0)
                    baseNavigator.topologyPreference = (TerrainTopology.Enum)1673010749;

                baseNavigator.topologyPreference |= TerrainTopology.Enum.Monument | TerrainTopology.Enum.Building;
            }

            baseCombatEntity.Spawn();

            timer.In(1f, () => SetupBrain(baseCombatEntity, position));

            pluginSpawnedEntities.Add(baseCombatEntity);
        }

        private BaseCombatEntity InstantiateEntity(string type, Vector3 position)
        {
            GameObject gameObject = Instantiate.GameObject(GameManager.server.FindPrefab(type), position, Quaternion.identity);
            gameObject.name = type;

            SceneManager.MoveGameObjectToScene(gameObject, Rust.Server.EntityScene);

            UnityEngine.Object.Destroy(gameObject.GetComponent<Spawnable>());

            if (!gameObject.activeSelf) 
                gameObject.SetActive(true);

            BaseCombatEntity component = gameObject.GetComponent<BaseCombatEntity>();
            return component;
        }
        #endregion

        #region Brain Setup
        private void SetupBrain(BaseCombatEntity baseCombatEntity, Vector3 position)
        {
            if (baseCombatEntity == null)
                return;

            if (baseCombatEntity is global::HumanNPC)
                SetupBrain<global::HumanNPC>(baseCombatEntity, position);

            if (baseCombatEntity is BaseAnimalNPC)
                SetupBrain<BaseAnimalNPC>(baseCombatEntity, position);

            if (baseCombatEntity is ScarecrowNPC)
                SetupBrain<ScarecrowNPC>(baseCombatEntity, position);
        }

        private void SetupBrain<T>(BaseCombatEntity baseCombatEntity, Vector3 position) where T : BaseEntity
        {
            BaseAIBrain baseAIBrain = baseCombatEntity.GetComponent<BaseAIBrain>();
            if (baseAIBrain != null)
            {
                baseAIBrain.Events.Memory.Position.Set(position, 4);

                GenericRoamState<T> genericRoamState = new GenericRoamState<T>();
                genericRoamState.brain = baseAIBrain;
                baseAIBrain.states[AIState.Roam] = genericRoamState;
            }
        }

        private Vector3 GetHomePosition(BaseCombatEntity baseCombatEntity)
        {
            if (baseCombatEntity != null)
            {
                if (baseCombatEntity is global::HumanNPC)
                    return GetHomePosition<global::HumanNPC>(baseCombatEntity);

                if (baseCombatEntity is BaseAnimalNPC)
                    return GetHomePosition<BaseAnimalNPC>(baseCombatEntity);

                if (baseCombatEntity is ScarecrowNPC)
                    return GetHomePosition<ScarecrowNPC>(baseCombatEntity);
            }
            return Vector3.zero;
        }

        private Vector3 GetHomePosition<T>(BaseCombatEntity baseCombatEntity) where T : BaseEntity => baseCombatEntity.GetComponent<BaseAIBrain>().Events.Memory.Position.Get(4);
        #endregion

        #region NavMesh
        private static class NavmeshSpawnPoint
        {
            private static NavMeshHit navmeshHit;

            private static RaycastHit raycastHit;

            private static readonly Collider[] _buffer = new Collider[256];

            private const int WORLD_LAYER = 65536;

            public static bool Find(Vector3 targetPosition, float maxDistance, out Vector3 position)
            {
                for (int i = 0; i < 10; i++)
                {
                    position = i == 0 ? targetPosition : targetPosition + (Random.onUnitSphere * maxDistance);
                    if (NavMesh.SamplePosition(position, out navmeshHit, maxDistance, 1))
                    {
                        if (IsInRockPrefab(navmeshHit.position))
                            continue;

                        if (IsNearWorldCollider(navmeshHit.position))
                            continue;

                        if (navmeshHit.position.y < TerrainMeta.WaterMap.GetHeight(navmeshHit.position))
                            continue;

                        position = navmeshHit.position;
                        return true;
                    }
                }
                position = default(Vector3);
                return false;
            }

            private static bool IsInRockPrefab(Vector3 position)
            {
                Physics.queriesHitBackfaces = true;

                bool isInRock = Physics.Raycast(position, Vector3.up, out raycastHit, 20f, WORLD_LAYER, QueryTriggerInteraction.Ignore) &&
                                BLOCKED_COLLIDERS.Any(s => raycastHit.collider?.gameObject?.name.Contains(s, CompareOptions.OrdinalIgnoreCase) ?? false);

                Physics.queriesHitBackfaces = false;

                return isInRock;
            }

            private static bool IsNearWorldCollider(Vector3 position)
            {
                Physics.queriesHitBackfaces = true;

                int count = Physics.OverlapSphereNonAlloc(position, 2f, _buffer, WORLD_LAYER, QueryTriggerInteraction.Ignore);
                Physics.queriesHitBackfaces = false;

                int removed = 0;
                for (int i = 0; i < count; i++)
                {
                    if (ACCEPTED_COLLIDERS.Any(s => _buffer[i].gameObject.name.Contains(s, CompareOptions.OrdinalIgnoreCase) || _buffer[i].gameObject.layer == 4))
                        removed++;
                }

                return count - removed > 0;
            }

            private static readonly string[] ACCEPTED_COLLIDERS = new string[] { "road", "carpark", "rocket_factory", "range", "train_track", "runway", "_grounds", "concrete_slabs", "lighthouse", "cave", "office", "walkways", "sphere", "tunnel", "industrial", "junkyard" };

            private static readonly string[] BLOCKED_COLLIDERS = new string[] { "rock", "cliff", "junk", "range", "invisible" };
        }
        #endregion

        #region Roam State
        public class GenericRoamState<T> : BaseAIBrain.BasicAIState
        {
            private StateStatus status = StateStatus.Error;

            private static readonly Vector3[] preferedTopologySamples = new Vector3[4];

            private static readonly Vector3[] topologySamples = new Vector3[4];

            public GenericRoamState() : base(AIState.Roam) { }

            public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateLeave(brain, entity);
                brain.Navigator.Stop();
            }

            public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateEnter(brain, entity);
                status = StateStatus.Error;

                if (brain.PathFinder == null)
                    return;

                Vector3 destination = GetBestRoamPosition(brain.Navigator, brain.Events.Memory.Position.Get(4), brain.Events.Memory.Position.Get(4), 1f, Configuration.Settings.Spread);

                if (brain.Navigator.SetDestination(destination, BaseNavigator.NavigationSpeed.Slow, 0f, 0f))
                {
                    status = StateStatus.Running;
                    return;
                }

                status = StateStatus.Error;
            }

            public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
            {
                base.StateThink(delta, brain, entity);

                if (status == StateStatus.Error)
                    return status;

                if (brain.Navigator.Moving)
                    return StateStatus.Running;

                return StateStatus.Finished;
            }

            private Vector3 GetBestRoamPosition(BaseNavigator navigator, Vector3 localTo, Vector3 fallback, float minRange, float maxRange)
            {
                int topologyIndex = 0;
                int preferredTopologyIndex = 0;

                for (float degree = 0f; degree < 360f; degree += 90f)
                {
                    Vector3 position;
                    Vector3 pointOnCircle = BasePathFinder.GetPointOnCircle(localTo, Random.Range(minRange, maxRange), degree + Random.Range(0f, 90f));

                    if (navigator.GetNearestNavmeshPosition(pointOnCircle, out position, 20f) && navigator.IsAcceptableWaterDepth(position))
                    {
                        topologySamples[topologyIndex] = position;
                        topologyIndex++;
                        if (navigator.IsPositionATopologyPreference(position))
                        {
                            preferedTopologySamples[preferredTopologyIndex] = position;
                            preferredTopologyIndex++;
                        }
                    }
                }

                Vector3 chosenPosition;

                if (Random.Range(0f, 1f) <= 0.9f && preferredTopologyIndex > 0)
                    chosenPosition = preferedTopologySamples[Random.Range(0, preferredTopologyIndex)];

                else if (topologyIndex > 0)
                    chosenPosition = topologySamples[Random.Range(0, topologyIndex)];

                else chosenPosition = fallback;

                return chosenPosition;
            }
        }
        #endregion

        #region Commands
        [ChatCommand("ra_killall")]
        private void ChatCommand_KillAnimals(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) 
                return;

            foreach(BaseCombatEntity baseCombatEntity in pluginSpawnedEntities)
            {
                if (baseCombatEntity != null && !baseCombatEntity.IsDestroyed)
                    baseCombatEntity.Kill(BaseNetworkable.DestroyMode.None);
            }
            pluginSpawnedEntities.Clear();

            SendReply(player, lang.GetMessage("Message.Title", this, player.UserIDString) + lang.GetMessage("Notification.KilledAll", this, player.UserIDString));
        }

        [ConsoleCommand("ra_killall")]
        private void ConsoleCommand_KillAnimals(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null)
            {
                foreach (BaseCombatEntity baseCombatEntity in pluginSpawnedEntities)
                {
                    if (baseCombatEntity != null && !baseCombatEntity.IsDestroyed)
                        baseCombatEntity.Kill(BaseNetworkable.DestroyMode.None);
                }
                pluginSpawnedEntities.Clear();

                SendReply(arg, lang.GetMessage("Notification.KilledAll", this));
            }
        }
        #endregion

        #region Config 
        private static ConfigData Configuration;

        private class ConfigData
        {
            public Options Settings { get; set; }

            [JsonProperty(PropertyName = "Monument Settings")]
            public Monuments MonumentSettings { get; set; }

            public class Options
            {
                [JsonProperty(PropertyName = "Respawn timer (seconds)")]
                public int Respawn { get; set; }

                [JsonProperty(PropertyName = "Spawn spread distance from center of monument")]
                public float Spread { get; set; }

                [JsonProperty(PropertyName = "Maximum amount of animals to spawn")]
                public int Total { get; set; }
            }

            public class Monuments
            {
                public MonumentSettings Airfield { get; set; }

                public MonumentSettings Dome { get; set; }

                public MonumentSettings Junkyard { get; set; }

                public MonumentSettings Lighthouse { get; set; }

                public MonumentSettings LargeHarbor { get; set; }

                public MonumentSettings GasStation { get; set; }

                public MonumentSettings Powerplant { get; set; }

                [JsonProperty(PropertyName = "Stone Quarry")]
                public MonumentSettings Quarry_Stone { get; set; }

                [JsonProperty(PropertyName = "Sulfur Quarry")]
                public MonumentSettings Quarry_Sulfur { get; set; }

                [JsonProperty(PropertyName = "HQM Quarry")]
                public MonumentSettings Quarry_HQM { get; set; }

                public MonumentSettings Radtown { get; set; }

                public MonumentSettings RocketFactory { get; set; }

                public MonumentSettings Satellite { get; set; }

                public MonumentSettings SmallHarbor { get; set; }

                public MonumentSettings Supermarket { get; set; }

                public MonumentSettings Trainyard { get; set; }

                public MonumentSettings Tunnels { get; set; }

                public MonumentSettings Warehouse { get; set; }

                public MonumentSettings WaterTreatment { get; set; }

                public class MonumentSettings
                {
                    [JsonProperty(PropertyName = "Enable spawning at this monument")]
                    public bool Enabled { get; set; }

                    [JsonProperty(PropertyName = "Amount of animals to spawn at this monument")]
                    public Hash<NPC, int> Counts { get; set; } = new Hash<NPC, int>
                    {
                        [NPC.Bear] = 0,
                        [NPC.Boar] = 0,
                        [NPC.Chicken] = 0,
                        [NPC.Stag] = 0,
                        [NPC.Wolf] = 0,
                        [NPC.Scarecrow] = 0,
                        [NPC.Scientist] = 0,
                    }; 
                }
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
                Settings = new ConfigData.Options
                {
                    Respawn = 900,
                    Spread = 60,
                    Total = 40
                },
                MonumentSettings = new ConfigData.Monuments
                {
                    Airfield = new ConfigData.Monuments.MonumentSettings(),
                    Dome = new ConfigData.Monuments.MonumentSettings(),
                    GasStation = new ConfigData.Monuments.MonumentSettings(),
                    Junkyard = new ConfigData.Monuments.MonumentSettings(),
                    LargeHarbor = new ConfigData.Monuments.MonumentSettings(),
                    Lighthouse = new ConfigData.Monuments.MonumentSettings(),
                    Powerplant = new ConfigData.Monuments.MonumentSettings(),
                    Quarry_HQM = new ConfigData.Monuments.MonumentSettings(),
                    Quarry_Stone = new ConfigData.Monuments.MonumentSettings(),
                    Quarry_Sulfur = new ConfigData.Monuments.MonumentSettings(),
                    Radtown = new ConfigData.Monuments.MonumentSettings(),
                    RocketFactory = new ConfigData.Monuments.MonumentSettings(),
                    Satellite = new ConfigData.Monuments.MonumentSettings(),
                    SmallHarbor = new ConfigData.Monuments.MonumentSettings(),
                    Supermarket = new ConfigData.Monuments.MonumentSettings(),
                    Trainyard = new ConfigData.Monuments.MonumentSettings(),
                    Tunnels = new ConfigData.Monuments.MonumentSettings(),
                    Warehouse = new ConfigData.Monuments.MonumentSettings(),
                    WaterTreatment = new ConfigData.Monuments.MonumentSettings()
                },
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (Configuration.Version < new Core.VersionNumber(0, 3, 0))
                Configuration = baseConfig;

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }
        #endregion

        #region Messages
        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>()
        {
            ["Message.Title"] = "<color=orange>Radtown Animals:</color> ",
            ["Notification.KilledAll"] = "<color=#939393>Killed all animals</color>",
            ["Notification.SpawnLimit"] = "The animal spawn limit has been hit."
        };
        #endregion
    }
}
