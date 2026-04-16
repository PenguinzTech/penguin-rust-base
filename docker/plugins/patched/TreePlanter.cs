// PATCHED by penguin-rust-base: fixed removed Oxide APIs
﻿using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using Oxide.Core;
using Newtonsoft.Json;
using UnityEngine.UI;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Tree Planter", "Bazz3l", "1.2.9")]
[Description("Gives ability to plant trees, make your own wood farm or just enjoy the nature.")]
internal class TreePlanter : RustPlugin
{
    [PluginReference] private Plugin ServerRewards, ImageLibrary, Economics, Clans;

    #region Fields

    private const string PERM_USE = "treeplanter.use";
    private const string PERM_MENU = "treeplanter.menu";
                
    private readonly Dictionary<ulong, Timer> _menuPopup = new();
    private readonly Dictionary<ulong, int> _menuPages = new();
    private CuiElementContainer _menuLayout;
    private UiGrid _uiGrid = new();
    private int _totalPages;
    private PluginConfig _config;

    #endregion

    #region Lang

    private struct LangKeys
    {
        public const string MESSAGE_PREFIX = "Message.Prefix";
        public const string MESSAGE_PERMISSION = "Message.Permission";
        public const string MESSAGE_INFO = "Message.Info";
        public const string MESSAGE_ITEM = "Message.Item";
        public const string MESSAGE_AUTHED = "Message.Authed";
        public const string MESSAGE_BALANCE = "Message.Balance";
        public const string MESSAGE_PLANTER = "Message.Planter";
        public const string MESSAGE_GROUND = "Message.Ground";
        public const string MESSAGE_PLANTED = "Message.Planted";
        public const string MESSAGE_RECEIVED = "Message.Received";

        public const string UI_HEADING_TEXT = "UI.HeadingText";
        public const string UI_CLOSE_TEXT = "UI.CloseText";
        public const string UI_ITEMS_TEXT = "UI.ItemsText";
        public const string UI_EMPTY_LABEL = "UI.EmptyText";
        public const string UI_PREV_ICON = "UI.PrevIcon";
        public const string UI_NEXT_ICON = "UI.NextIcon";

        public const string ERROR_NOT_FOUND = "Error.NotFound";
    }

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            {LangKeys.MESSAGE_PREFIX, "<color=#DC143C>Tree Planter</color>: "},
            {LangKeys.MESSAGE_PERMISSION, "You do not have permission to do that."},
            {LangKeys.MESSAGE_INFO, "Please select one of the following."},
            {LangKeys.MESSAGE_ITEM, "{0} | ${1}"},
            {LangKeys.MESSAGE_AUTHED, "You must have build privilege."},
            {LangKeys.MESSAGE_BALANCE, "You don't have enough for that."},
            {LangKeys.MESSAGE_PLANTER, "Must be planted in a planter."},
            {LangKeys.MESSAGE_GROUND, "Must be planted in the ground."},
            {LangKeys.MESSAGE_PLANTED, "<color=#FFC55C>{0}</color> was successfully planted."},
            {LangKeys.MESSAGE_RECEIVED, "You've purchased <color=#FFC55C>{0}x</color> <color=#FFC55C>{1}</color>."},

            {LangKeys.UI_HEADING_TEXT, "TREE PLANTER"},
            {LangKeys.UI_CLOSE_TEXT, "CLOSE"},
            {LangKeys.UI_ITEMS_TEXT, "{0}\n{1} Cost\n{2}x"},
            {LangKeys.UI_EMPTY_LABEL, "NOTHING TO SHOW HERE"},
            {LangKeys.UI_PREV_ICON, "◀"},
            {LangKeys.UI_NEXT_ICON, "▶"},

            {LangKeys.ERROR_NOT_FOUND, "No item found by that name."},
        }, this);
    }
        
    private string Lang(string key, string id = null, params object[] args)
    {
        return args?.Length > 0 ? string.Format(lang.GetMessage(key, this, id), args) : lang.GetMessage(key, this, id);
    }

    private static void MessagePlayer(BasePlayer player, string message)
    {
        if (player?.net == null) return;
        player.ChatMessage(message);
    }
        
    #endregion

    #region Config

    protected override void LoadDefaultConfig() => _config = PluginConfig.DefaultConfig();

    protected override void LoadConfig()
    {
        base.LoadConfig();

        try
        {
            _config = Config.ReadObject<PluginConfig>();
            if (_config == null) throw new JsonException();
        }
        catch
        {
            LoadDefaultConfig();
            PrintWarning("Loaded default config.");
        }

        UpdateConfig();
    }

    protected override void SaveConfig() => Config.WriteObject(_config, true);

    private void UpdateConfig()
    {
        string[] forbiddenPrefabs = { "dead", "trumpet", "vine" };
        
        if (_config.AgriBlocked.Count == 0)
        {
            ItemManager.Initialize();
            
            foreach (ItemDefinition iDef in ItemManager.itemList)
            {
                if (iDef?.shortname == null || !iDef.shortname.Contains("seed")) 
                    continue;
            
                _config.AgriBlocked[iDef.shortname] = true;
            }
        }

        if (_config.TreeConfigs.Count == 0)
        {
            foreach (KeyValuePair<string, GameObject> valuePair in Prefab.DefaultManager.preProcessed.prefabList)
            {
                if (valuePair.Value != null && valuePair.Value.TryGetComponent(out TreeEntity entity))
                {
                    if (string.IsNullOrEmpty(entity.ShortPrefabName) || forbiddenPrefabs.Any(needle => entity.PrefabName.Contains(needle)))
                        continue;
                
                    string prefabName = entity.ShortPrefabName.Replace("_", "-");
                    if (string.IsNullOrEmpty(prefabName))
                        continue;
                
                    if (_config.TreeConfigs.Exists(config => config.Name == prefabName)) 
                        continue;
                
                    _config.TreeConfigs.Add(new TreeConfig(prefabName, entity.PrefabName));
                }
            }
        }
        
        _config.TreeConfigs.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        
        SaveConfig();
    }

    private class PluginConfig
    {
        [JsonProperty("UseServerRewards (use server rewards as currency)")]
        public bool UseServerRewards;
        [JsonProperty("UseEconomics (use economics as currency)")]
        public bool UseEconomics;
        [JsonProperty("UseCurrency (use custom items as currency, by specifying the CurrencyItem)")]
        public bool UseCurrency;
        
        [JsonProperty("CurrencyItem (set an item id to use as currency, default is set to scrap)")]
        public int CurrencyItem;
        [JsonProperty("CurrencySkinID (set an skin id to use as currency, default is set to 0)")]
        public ulong CurrencySkinID;
        
        [JsonProperty("EnableGrowing (enables growing from saplings to adult trees over a period of time)")]
        public bool EnableGrowing;
        
        [JsonProperty("EnableOwner (enables owners to chop down trees)")]
        public bool EnableOwner;
        [JsonProperty("EnableClan (enables clan members to chop down trees)")]
        public bool EnableClan;
        [JsonProperty("EnableTeam (enables team members to chop down trees)")]
        public bool EnableTeam;
        
        [JsonProperty("BlockedAgriItems (specify which items should only be placed in a planter box)")]
        public Dictionary<string, bool> AgriBlocked;
        
        [JsonProperty("TreeConfigs (list of available trees to purchase)")]
        public List<TreeConfig> TreeConfigs;
            
        public static PluginConfig DefaultConfig()
        {
            return new PluginConfig
            {
                UseServerRewards = false,
                UseEconomics = false,
                UseCurrency = true,
                EnableOwner = false,
                EnableClan = false,
                CurrencyItem = -932201673,
                AgriBlocked = new Dictionary<string, bool>
                {
                    {"seed.black.berry", true},
                    {"seed.blue.berry", true},
                    {"seed.green.berry", true},
                    {"seed.yellow.berry", true},
                    {"seed.white.berry", true},
                    {"seed.red.berry", true},
                    {"seed.corn", true},
                    {"clone.corn", true},
                    {"seed.pumpkin", true},
                    {"clone.pumpkin", true},
                    {"seed.hemp", true},
                    {"clone.hemp", true}
                },
                TreeConfigs = new List<TreeConfig>()
            };
        }
            
        public TreeConfig FindTreeConfig(string name)
        {
            if (string.IsNullOrEmpty(name)) 
                return null;

            foreach (TreeConfig treeConfig in TreeConfigs)
            {
                if (treeConfig.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || treeConfig.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return treeConfig;
            }
                
            return null;
        }
    }

    private class TreeConfig
    {
        public string Prefab;
        public string Name;
        public ulong Skin;
        public int Cost;
        public int Amount;
        [JsonProperty("ImageUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string ImageUrl;

        [JsonIgnore]
        private string _displayName;

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(_displayName))
                {
                    _displayName = Name.Replace("-", " ")
                        .TitleCase();
                }
                    
                return _displayName;
            }
        }

        public TreeConfig(string name, string prefab)
        {
            Name = name;
            Skin = 0UL;
            Cost = 10;
            Amount = 1;
            Prefab = prefab;
            ImageUrl = "";
        }

        public void GiveItem(BasePlayer player, int amount)
        {
            Item item = ItemManager.CreateByItemID(-886280491, amount, Skin);
            item.text = item.name = Name;
            item.info.stackable = 1;
            item.MarkDirty();
            
            player.GiveItem(item);
        }

        public static void RefundItem(BasePlayer player, string name, string shortname, ulong skin, GrowableGenes growableGenes)
        {
            Item item = ItemManager.CreateByName(shortname, 1, skin);
            item.text = item.name = name;
            
            if (growableGenes != null)
            {
                item.instanceData = new ProtoBuf.Item.InstanceData();
                item.instanceData.dataInt = GrowableGeneEncoding.EncodeGenesToInt(growableGenes);                    
            }
                
            item.MarkDirty();
            
            player.GiveItem(item);
        }
    }

    #endregion

    #region Oxide Hooks

    private void OnServerInitialized()
    {
        CachedMenuUI();
        GrowableManager.Initialize();
        NextFrame(CacheMenuImages);
    }

    private void Init()
    {
        permission.RegisterPermission(PERM_USE, this);
        permission.RegisterPermission(PERM_MENU, this);
    }

    private void Unload()
    {
        DestroyMenuUI();
        GrowableManager.OnUnload();
    }

    private void OnPluginLoaded(Plugin plugin)
    {
        if (plugin?.Name == "ImageLibrary")
            NextFrame(CacheMenuImages);
    }

    private void OnEntityBuilt(Planner planner, GameObject gameObject)
    {
        BasePlayer player = planner?.GetOwnerPlayer();
        if (player == null || !HasPermission(player, PERM_USE))
            return;
        
        GrowableEntity entity = gameObject?.GetComponent<GrowableEntity>();
        if (entity == null)
            return;
        
        Item item = player.GetActiveItem();
        if (item == null)
            return;
        
        string shortname = item.info.shortname;
        string name = item.name;
        ulong skin = item.skin;
        
        NextFrame(() =>
        {
            TreeConfig treeConfig = _config.FindTreeConfig(name);
            if (treeConfig != null && entity != null) TryPlantTree(player, entity, treeConfig);
            else TryPlantSeed(player, entity, name, shortname, skin);
        });
    }

    private object OnMeleeAttack(BasePlayer player, HitInfo info)
    {
        if (player == null || info.HitEntity is not TreeEntity entity || entity.OwnerID == 0UL)
            return null;
            
        if (!IsEntityOwner(entity.OwnerID, player.userID)) 
            return true;
            
        return null;
    }

    private void OnPlayerDisconnected(BasePlayer player) => DestroyMenuUI(player);
        
    #endregion

    #region Core

    private class GrowableManager
    {
        public static GrowableManager Instance { get; private set; }
        public static readonly Vector3 GrowScale = new(0.01f, 0.01f, 0.01f);
        public static readonly Vector3 FullScale = new(1.0f, 1.0f, 1.0f);
        
        private readonly List<BaseEntity> _entities = new();
        private readonly Stopwatch _stopwatch = new();
        private Coroutine _routine;
        private const float TICK_INTERVAL = 10f;

        public static void Initialize()
        {
            Instance = new GrowableManager();
            Instance.GatherEntities();
        }

        public static void OnUnload()
        {
            if (Rust.Application.isQuitting)
                return;
                
            Instance.Cancel();
            Instance = null;
        }

        public void Enqueue(BaseEntity entity)
        {
            _entities.Add(entity);
                
            if (_routine != null)
                return;

            _routine = ServerMgr.Instance.StartCoroutine(CycleEntities());
        }

        private void Cancel()
        {
            if (_routine == null)
                return;

            ServerMgr.Instance.StopCoroutine(_routine);
            _routine = null;
        }

        private void GatherEntities()
        {
            foreach (BaseNetworkable nEntity in BaseNetworkable.serverEntities)
            {
                if (nEntity is TreeEntity { IsDestroyed: false, networkEntityScale: true } entity)
                    Enqueue(entity);
            }
        }

        private IEnumerator CycleEntities()
        {
            while (true)
            {
                yield return CoroutineEx.waitForSeconds(TICK_INTERVAL);
                    
                _stopwatch.Restart();
                    
                for (int index = _entities.Count - 1; index >= 0; index--)
                {
                    BaseEntity entity = _entities[index];
                    if (entity == null || entity.IsDestroyed || !entity.networkEntityScale)
                    {
                        _entities.RemoveAt(index);
                    }
                    else
                    {
                        if (entity.transform.localScale.y < FullScale.y)
                        {
                            entity.transform.localScale += GrowableManager.GrowScale;
                        }
                        else
                        {
                            if (entity is TreeEntity { IsDestroyed: false, networkEntityScale: true } treeEntity)
                            {
                                treeEntity.networkEntityScale = false;
                                treeEntity.transform.localScale = Vector3.one;
                                treeEntity.spawnTreeAddition = true;
                                treeEntity.Invoke("TryAddTreeAddition", 0.25f);
                            }
                        }

                        entity.SendNetworkUpdateImmediate();
                    }

                    if (_stopwatch.Elapsed.TotalMilliseconds >= GrowableEntity.framebudgetms)
                        yield return CoroutineEx.waitForEndOfFrame;
                }
            }
        }
    }

    private bool CanPlantTree(BasePlayer player, GrowableEntity growableEntity, out string message)
    {
        if (growableEntity.GetParentEntity() is PlanterBox)
        {
            message = Lang(LangKeys.MESSAGE_GROUND, player.UserIDString);
            return false;
        }
            
        if (!player.IsBuildingAuthed())
        {
            message = Lang(LangKeys.MESSAGE_AUTHED, player.UserIDString);
            return false;
        }
            
        message = string.Empty;
        return true;
    }

    private void TryPlantTree(BasePlayer player, GrowableEntity growableEntity, TreeConfig treeConfig)
    {
        if (player == null || treeConfig == null) 
            return;

        if (!CanPlantTree(player, growableEntity, out string message))
        {
            treeConfig.GiveItem(player, 1);
            MessagePlayer(player, message);
        }
        else
        {
            CreateTreeEntity(player, growableEntity, treeConfig.Prefab);
            MessagePlayer(player, Lang(LangKeys.MESSAGE_PLANTED, player.UserIDString, treeConfig.DisplayName));
        }
            
        RemoveGrowableEntity(growableEntity);
    }

    private void TryPlantSeed(BasePlayer player, GrowableEntity growableEntity, string name, string shortname, ulong skin)
    {
        if (growableEntity?.GetParentEntity() is PlanterBox)
            return;
            
        if (!IsAgrilBlocked(shortname)) 
            return;
            
        TreeConfig.RefundItem(player, name, shortname, skin, growableEntity?.Genes);
        RemoveGrowableEntity(growableEntity);
        MessagePlayer(player, Lang(LangKeys.MESSAGE_PLANTER, player.UserIDString));
    }

    private void CreateTreeEntity(BasePlayer player, GrowableEntity growableEntity, string prefabName)
    {
        BaseEntity entity = GameManager.server.CreateEntity(prefabName, growableEntity.ServerPosition, Quaternion.identity);
        if (entity == null)
        {
            Interface.Oxide.LogError("TreePlanter::CreateTreeEntity: failed to create entity, invalid prefab path: {0}", prefabName);
            return;
        }
            
        entity.OwnerID = player.userID;
            
        if (_config.EnableGrowing && entity is TreeEntity { IsDestroyed: false } treeEntity)
        {
            treeEntity.spawnTreeAddition = false;
            treeEntity.networkEntityScale = true;
            treeEntity.transform.localScale = GrowableManager.GrowScale; 
        }
            
        entity.Spawn();
        entity.SendNetworkUpdateImmediate();
            
        if (!_config.EnableGrowing) 
            return;
            
        GrowableManager.Instance.Enqueue(entity);
    }
        
    private void DisplayTrees(BasePlayer player, int chunkSize = 25)
    {
        StringBuilder sb = Facepunch.Pool.Get<StringBuilder>();
        
        try
        {
            sb.Append(Lang(LangKeys.MESSAGE_PREFIX, player.UserIDString));
            sb.Append(Lang(LangKeys.MESSAGE_INFO, player.UserIDString));
            
            string message = sb.ToString();
            sb.Clear();
            MessagePlayer(player, message);
            
            int treeCount = _config.TreeConfigs.Count;
            for (int i = 0; i < treeCount; i++)
            {
                TreeConfig treeConfig = _config.TreeConfigs[i];
                sb.Append(Lang(LangKeys.MESSAGE_ITEM, player.UserIDString, treeConfig.DisplayName, treeConfig.Cost)).Append(",");
                
                if ((i + 1) % chunkSize == 0 || i == treeCount - 1)
                {
                    message = sb.ToString();
                    sb.Clear();
                    MessagePlayer(player, message.TrimEnd(','));
                }
            }

            if (sb.Length > 0)
            {
                message = sb.ToString();
                sb.Clear();
                MessagePlayer(player, message.TrimEnd(','));
            }
        }
        finally
        {
            sb.Clear();
            Facepunch.Pool.FreeUnmanaged(ref sb);
        }
    }

    private void RemoveGrowableEntity(GrowableEntity growableEntity)
    {
        if (growableEntity != null && !growableEntity.IsDestroyed) 
            growableEntity.Invoke(growableEntity.AdminKill, 1.25f);
    }

    #endregion

    #region UI

    private const string UI_PLANT_CONTAINER = "UI_PLANT_CONTAINER";
    private const string UI_POPUP_CONTAINER = "UI_POPUP_CONTAINER";
    private const string UI_HEADING_CLOSE = "UI_HEADING_CLOSE";
    private const string UI_HEADING_LABEL = "UI_HEADING_LABEL";
    private const string UI_ITEMS_CONTAINER = "UI_ITEMS_CONTAINER";
    private const string UI_PREV_BUTTON = "UI_PREV_BUTTON";
    private const string UI_NEXT_BUTTON = "UI_NEXT_BUTTON";
    private const string UI_PURCHASE_CMD = "treeplanter.purchase {0}";
    private const string UI_PAGINATE_CMD = "treeplanter.page {0}";
        
    private const int UI_ITEMS_PER_PAGE = 72;

    private void CacheMenuImages()
    {
        if (ImageLibrary is not { IsLoaded: true })
            return;
            
        Dictionary<string, string> images = new Dictionary<string, string>();
            
        foreach (TreeConfig treeConfig in _config.TreeConfigs)
        {
            if (!string.IsNullOrEmpty(treeConfig.ImageUrl)) 
                images.Add(treeConfig.Name, treeConfig.ImageUrl);
        }
            
        if (images.Count == 0)
            return;

        ImageLibrary.Call("ImportImageList", Title, images, 0, true);
    }
        
    private void CachedMenuUI()
    {
        CuiElementContainer container = new CuiElementContainer();
            
        UiBuilder.AddPanel(container, parentName: "Overlay", 
            elementName: UI_PLANT_CONTAINER, 
            color: UiBuilder.Color3, 
            anchorMin: "0 0", 
            anchorMax: "1 1", 
            cursorEnabled: true, 
            keyboardEnabled: true);
            
        UiBuilder.AddPanel(container, parentName: UI_PLANT_CONTAINER, 
            color: UiBuilder.Color4, 
            anchorMin: "0 0.94", 
            anchorMax: "1 1");
            
        _menuLayout = container;
        _totalPages = Math.Max(1, _config.TreeConfigs.Count - 1) / UI_ITEMS_PER_PAGE;
    }

    private void DestroyMenuUI()
    {
        _menuPopup.Clear();
        _menuPages.Clear();
        _menuLayout = null;
        _uiGrid = null;
            
        foreach (var activePlayer in BasePlayer.activePlayerList)
        {
            CuiHelper.DestroyUi(activePlayer, UI_POPUP_CONTAINER);
            CuiHelper.DestroyUi(activePlayer, UI_PLANT_CONTAINER);
        }
    }
        
    private void DisplayMenuUI(BasePlayer player, int currentPage, bool isFirst = false)
    {
        _menuPages[player.userID] = currentPage;
        
        CuiElementContainer container = new CuiElementContainer();
        if (isFirst) CreateStaticElements(container, player);
        CreateItemsContainer(container);
        CreateItemsGrid(container, currentPage, player);
        UiBuilder.AddAndClear(container, player);
    }

    private void DestroyMenuUI(BasePlayer player)
    {
        if (player == null) 
            return;
            
        _menuPages.Remove(player.userID);
        CuiHelper.DestroyUi(player, UI_PLANT_CONTAINER);
    }

    private void DisplayPopupUI(BasePlayer player, UiBuilder.PopupStyle style, string message, params object[] args)
    {
        const string popupAnchorMin = "0.18 0.88";
        const string popupAnchorMax = "0.798 0.92";
            
        CuiElementContainer container = new CuiElementContainer();
        ulong userID = player.userID;
            
        UiBuilder.AddButton(container, parentName: "Overlay",
            elementName: UI_POPUP_CONTAINER, 
            fontBold: true, 
            text: (args?.Length > 0 ? string.Format(message, args) : message), 
            textColor: UiBuilder.Color2, 
            buttonColor: UiBuilder.GetPopupColor(style), 
            anchorMin: popupAnchorMin, 
            anchorMax: popupAnchorMax, 
            close: UI_POPUP_CONTAINER, 
            destroyUi: true);
            
        UiBuilder.AddAndClear(container, player);
            
        if (_menuPopup.TryGetValue(userID, out Timer popupTimer) && !popupTimer.Destroyed)
            popupTimer.DestroyToPool();
            
        _menuPopup[userID] = timer.Once(2.5f, () =>
        {
            _menuPopup.Remove(userID);
            CuiHelper.DestroyUi(player, UI_POPUP_CONTAINER);
        });
    }

    private void CreateStaticElements(CuiElementContainer container, BasePlayer player)
    {
        const string labelAnchorMin = "0 0.94";
        const string labelAnchorMax = "1 1";
        const string closeAnchorMin = "0.9 0.94";
        const string closeAnchorMax = "1 1";
        const string nextAnchorMin = "0.505 0.02";
        const string nextAnchorMax = "0.7 0.08";
        const string prevAnchorMin = "0.3 0.02";
        const string prevAnchorMax = "0.495 0.08";
            
        CuiHelper.AddUi(player, _menuLayout);
                
        UiBuilder.AddText(container, parentName: UI_PLANT_CONTAINER,
            elementName: UI_HEADING_LABEL,
            fontBold: true, 
            text: Lang(LangKeys.UI_HEADING_TEXT, player.UserIDString), 
            textColor: UiBuilder.Color2, 
            textAnchor: TextAnchor.MiddleCenter, 
            anchorMin: labelAnchorMin, 
            anchorMax: labelAnchorMax, 
            destroyUi: true);
                
        UiBuilder.AddButton(container, parentName: UI_PLANT_CONTAINER, 
            elementName: UI_HEADING_CLOSE,
            fontBold: true, 
            text: Lang(LangKeys.UI_CLOSE_TEXT, player.UserIDString), 
            textColor: UiBuilder.Color1,
            buttonColor: UiBuilder.Color5, 
            anchorMin: closeAnchorMin, 
            anchorMax: closeAnchorMax, 
            close: UI_PLANT_CONTAINER, 
            command: "treeplanter.close",
            destroyUi: true);
            
        UiBuilder.AddButton(container, parentName: UI_PLANT_CONTAINER, 
            elementName: UI_PREV_BUTTON, 
            fontBold: true, 
            text: Lang(LangKeys.UI_PREV_ICON, player.UserIDString), 
            textColor: UiBuilder.Color1,
            buttonColor: UiBuilder.Color4, 
            anchorMin: prevAnchorMin, 
            anchorMax: prevAnchorMax, 
            command: string.Format(UI_PAGINATE_CMD, "prev"),
            destroyUi: true);
            
        UiBuilder.AddButton(container, parentName: UI_PLANT_CONTAINER, 
            elementName: UI_NEXT_BUTTON, 
            fontBold: true, 
            text: Lang(LangKeys.UI_NEXT_ICON, player.UserIDString), 
            textColor: UiBuilder.Color1,
            buttonColor: UiBuilder.Color4, 
            anchorMin: nextAnchorMin, 
            anchorMax: nextAnchorMax,  
            command: string.Format(UI_PAGINATE_CMD, "next"),
            destroyUi: true);
    }

    private void CreateItemsContainer(CuiElementContainer container)
    {
        const string itemAnchorMin = "0 0.1";
        const string itemAnchorMax = "1 0.93";
            
        UiBuilder.AddPanel(container, parentName: UI_PLANT_CONTAINER, 
            elementName: UI_ITEMS_CONTAINER, 
            color: UiBuilder.Color7, 
            anchorMin: itemAnchorMin, 
            anchorMax: itemAnchorMax, 
            destroyUi: true);
    }

    private void CreateItemsGrid(CuiElementContainer container, int currentPage, BasePlayer player)
    {
        const string textAnchorMin = "0 0";
        const string textAnchorMax = "1 1";
            
        int totalItems = _config.TreeConfigs.Count;
        if (totalItems == 0)
        {
            UiBuilder.AddText(container, parentName: UI_ITEMS_CONTAINER,
                fontBold: true, 
                text: Lang(LangKeys.UI_EMPTY_LABEL, player.UserIDString), 
                textColor: UiBuilder.Color2, 
                textAnchor: TextAnchor.MiddleCenter, 
                anchorMin: textAnchorMin, 
                anchorMax: textAnchorMax);
        }
        else
        {
            int limit = Mathf.Min(totalItems, (currentPage + 1) * UI_ITEMS_PER_PAGE);
            int index = 0;
                
            for (int i = currentPage * UI_ITEMS_PER_PAGE; i < limit; i++)
            {
                Dictionary<string, string[]> uiPosition = _uiGrid.GetAnchors(index);
                TreeConfig treeConfig = _config.TreeConfigs[i];
                bool hasImage = !string.IsNullOrEmpty(treeConfig.ImageUrl);
                string[] iconAnchors = uiPosition["icon"];
                string[] textAnchors = hasImage ? uiPosition["text"] : uiPosition["full"];
                    
                if (hasImage)
                {
                    UiBuilder.AddImage(container, parentName: UI_ITEMS_CONTAINER, 
                        png: (string)ImageLibrary.Call("GetImage", treeConfig.Name),
                        anchorMin: iconAnchors[0], 
                        anchorMax: iconAnchors[1]);
                }
                else if (treeConfig.Skin != 0UL)
                {
                    UiBuilder.AddIcon(container, parentName: UI_ITEMS_CONTAINER, 
                        itemID: -886280491,
                        skinID: treeConfig.Skin,
                        anchorMin: iconAnchors[0], 
                        anchorMax: iconAnchors[1]);
                }
                    
                UiBuilder.AddButton(container, parentName: UI_ITEMS_CONTAINER,
                    fontBold: true, 
                    fontSize: 10,
                    text: Lang(LangKeys.UI_ITEMS_TEXT, player.UserIDString, treeConfig.DisplayName, treeConfig.Cost, treeConfig.Amount), 
                    textColor: UiBuilder.Color1,
                    buttonColor: UiBuilder.Color4,
                    command: string.Format(UI_PURCHASE_CMD, treeConfig.Name),
                    anchorMin: textAnchors[0], 
                    anchorMax: textAnchors[1]);
                    
                ++index;
            }
        }
    }
        
    private class UiGrid
    {
        private const int COLS = 9;
        private const float WIDTH = 0.1f;
        private const float HEIGHT = 0.1f;
        private const float X_OFFSET = 0.01f;
        private const float Y_OFFSET = 0.92f;
        private const float X_SPACING = 0.01f;
        private const float Y_SPACING = 0.0172f;
        private readonly Dictionary<int, Dictionary<string, string[]>> _cachedAnchors = new();

        public Dictionary<string, string[]> GetAnchors(int index)
        {
            if (_cachedAnchors.TryGetValue(index, out Dictionary<string, string[]> anchors))
                return anchors;
                
            int row = index / COLS;
            int col = index % COLS;
            float xStart = X_OFFSET + col * (WIDTH + X_SPACING);
            float yTop = Y_OFFSET - row * (HEIGHT + Y_SPACING);
            float iconWidth = WIDTH * 0.3f;
            float spacing = WIDTH * 0.05f;
            float textWidth = WIDTH - iconWidth - spacing;

            anchors = new Dictionary<string, string[]>
            {
                ["icon"] = new[]{ $"{xStart} {yTop - HEIGHT}", $"{xStart + iconWidth} {yTop}" },
                ["text"] = new[]{ $"{xStart + iconWidth + spacing} {yTop - HEIGHT}", $"{xStart + iconWidth + spacing + textWidth} {yTop}" },
                ["full"] = new[]{ $"{xStart} {yTop - HEIGHT}", $"{xStart + WIDTH} {yTop}" }
            };

            _cachedAnchors[index] = anchors;
                
            return anchors;
        }
    }

    private static class UiBuilder
    {
        public const string Color1 = "1 1 1 1";             // Pure white
        public const string Color2 = "0.91 0.87 0.83 1";    // Light paper tone
        public const string Color3 = "0.145 0.135 0.12 1";  // Deep charcoal
        public const string Color4 = "0.187 0.179 0.172 1"; // Card/Panel surface
        public const string Color5 = "0.8 0.28 0.2 1";      // Alert/CTA
        public const string Color6 = "0.23 0.46 0.31 1";    // Confirm/Success
        public const string Color7 = "0 0 0 0";             // Clear/None
        public const string Color8 = "0.25 0.50 0.75 0.7";  // Selection/Hover effect

        public enum PopupStyle { Error, Info, Success }
            
        public static string GetPopupColor(PopupStyle style)
        {
            return style switch
            {
                PopupStyle.Success => Color6,
                PopupStyle.Error => Color5,
                PopupStyle.Info => Color4,
                _ => Color2
            };
        }
            
        public static void AddPanel(CuiElementContainer container, 
            string parentName, 
            string elementName = null, 
            string color = "1 1 1 1",
            string sprite = null,
            string material = "assets/content/ui/namefontmaterial.mat",
            Image.Type imageType = Image.Type.Tiled,
            string anchorMin = "0 0",
            string anchorMax = "1 1",
            string offsetMin = null,
            string offsetMax = null,
            float fadeOut = 0f, 
            float fadeIn = 0f, 
            bool destroyUi = false,
            bool cursorEnabled = false,
            bool keyboardEnabled = false) 
        {
            container.Add(new CuiPanel
            {
                Image =
                {
                    Color = color,
                    Sprite = sprite,
                    Material = material,
                    ImageType = imageType,
                    FadeIn = fadeIn
                },
                RectTransform =
                {
                    AnchorMin = anchorMin, AnchorMax = anchorMax,
                    OffsetMin = offsetMin, OffsetMax = offsetMax,
                },
                FadeOut = fadeOut,
                CursorEnabled = cursorEnabled,
                KeyboardEnabled = keyboardEnabled
            }, parentName, elementName, (destroyUi ? elementName : null));
        }

        public static void AddText(CuiElementContainer container, 
            string parentName, 
            string elementName = null, 
            string textColor = "0 0 0 1",
            string text = "",
            TextAnchor textAnchor = TextAnchor.MiddleCenter, 
            int fontSize = 14,
            bool fontBold = false,
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null, 
            bool destroyUi = false) 
        {
            container.Add(new CuiElement
            {
                Parent = parentName,
                Name = elementName,
                DestroyUi = (destroyUi ? elementName : null),
                Components =
                {
                    new CuiTextComponent
                    {
                        Font = (fontBold ? "RobotoCondensed-Bold.ttf" : "RobotoCondensed-Regular.ttf"),
                        FontSize = fontSize,
                        Text = text,
                        Color = textColor,
                        Align = textAnchor,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin, AnchorMax = anchorMax,
                        OffsetMin = offsetMin, OffsetMax = offsetMax,
                    }
                }
            });
        }

        public static void AddButton(CuiElementContainer container, 
            string parentName, 
            string elementName = null,
            string text = "",
            string textColor = "0 0 0 1",
            TextAnchor textAnchor = TextAnchor.MiddleCenter, 
            int fontSize = 12,
            bool fontBold = true,
            string buttonColor = "1 1 1 1", 
            string command = null,
            string close = null,
            string sprite = null,
            string material = "assets/content/ui/namefontmaterial.mat",
            Image.Type imageType = Image.Type.Tiled,
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null,
            bool destroyUi = false) 
        {
            container.Add(new CuiButton
            {
                Text =
                {
                    Font = (fontBold ? "RobotoCondensed-Bold.ttf" : "RobotoCondensed-Regular.ttf"),
                    FontSize = fontSize,
                    Text = text,
                    Color = textColor,
                    Align = textAnchor,
                },
                Button =
                {
                    Color = buttonColor,
                    Close = close,
                    Command = command,
                    Sprite = sprite,
                    Material = material, 
                    ImageType = imageType,
                },
                RectTransform =
                {
                    AnchorMin = anchorMin, AnchorMax = anchorMax,
                    OffsetMin = offsetMin, OffsetMax = offsetMax,
                }
            }, parentName, elementName, (destroyUi ? elementName : null));
        }
            
        public static void AddInput(CuiElementContainer container, 
            string parentName, 
            string elementName = null,
            string text = "", 
            string textColor = "0 0 0 1", 
            TextAnchor textAnchor = TextAnchor.MiddleCenter, 
            int fontSize = 14,
            bool fontBold = false,
            int charLimit = 32,
            bool hudMenuInput = false,
            string command = null, 
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null,
            bool destroyUi = false) 
        {
            container.Add(new CuiElement
            {
                Parent = parentName,
                Name = elementName,
                DestroyUi = (destroyUi ? elementName : null),
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Font = (fontBold ? "RobotoCondensed-Bold.ttf" : "RobotoCondensed-Regular.ttf"),
                        FontSize = fontSize,
                        Align = textAnchor, 
                        Color = textColor,
                        Text = text,
                        CharsLimit = charLimit,
                        HudMenuInput = hudMenuInput,
                        Command = command
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin, AnchorMax = anchorMax,
                        OffsetMin = offsetMin, OffsetMax = offsetMax
                    }
                }
            });
        }
            
        public static void AddImage(CuiElementContainer container, 
            string parentName, 
            string elementName = null,
            string png = null, 
            string url = null, 
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null,
            float fadeIn = 0.5f,
            bool destroyUi = false) 
        {
            if (!string.IsNullOrEmpty(url))
            {
                container.Add(new CuiElement 
                {
                    Parent = parentName,
                    Name = elementName,
                    DestroyUi = (destroyUi ? elementName : null),
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Url = url,
                            FadeIn = fadeIn,
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = anchorMin, AnchorMax = anchorMax,
                            OffsetMin = offsetMin, OffsetMax = offsetMax
                        }
                    }
                });
                    
                return;
            }
                
            if (!string.IsNullOrEmpty(png))
            {
                container.Add(new CuiElement 
                {
                    Parent = parentName,
                    Name = elementName,
                    DestroyUi = (destroyUi ? elementName : null),
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Png = png,
                            FadeIn = fadeIn,
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = anchorMin, AnchorMax = anchorMax,
                            OffsetMin = offsetMin, OffsetMax = offsetMax
                        }
                    }
                });
            }
        }
            
        public static void AddIcon(CuiElementContainer container, 
            string parentName, 
            string elementName = null,
            int itemID = 0,
            ulong skinID = 0UL,
            string imageColor = "0 0 0 0", 
            string material = null,
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null,
            float fadeIn = 0.5f,
            bool destroyUi = false) 
        {
            container.Add(new CuiElement
            {
                Parent = parentName,
                Name = elementName,
                DestroyUi = (destroyUi ? elementName : null),
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = imageColor,
                        ItemId = itemID,
                        SkinId = skinID,
                        FadeIn = fadeIn,
                        Material = material,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin, AnchorMax = anchorMax,
                        OffsetMin = offsetMin, OffsetMax = offsetMax
                    }
                }
            });
        }
            
        public static void AddSprite(CuiElementContainer container, 
            string parentName, 
            string elementName = null,
            string sprite = null, 
            string imageColor = "0 0 0 0",
            string material = null,
            string anchorMin = "0 0", 
            string anchorMax = "1 1",
            string offsetMin = null, 
            string offsetMax = null,
            float fadeIn = 0.5f,
            bool destroyUi = false) 
        {
            if (string.IsNullOrEmpty(sprite))
                return;
                
            container.Add(new CuiElement
            {
                Parent = parentName,
                Name = elementName,
                DestroyUi = (destroyUi ? elementName : null),
                Components =
                {
                    new CuiRawImageComponent
                    {
                        Color = imageColor,
                        Sprite = sprite,
                        FadeIn = fadeIn,
                        Material = material,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin, AnchorMax = anchorMax,
                        OffsetMin = offsetMin, OffsetMax = offsetMax
                    }
                }
            });
        }
            
        public static void AddAndClear(CuiElementContainer container, BasePlayer player) 
        {
            CuiHelper.AddUi(player, CuiHelper.ToJson(container));
                
            foreach (CuiElement element in container)
                element.Components?.Clear();
                
            container.Clear();
            container = null;
        }
    }
        
    #endregion

    #region Chat Command

    [ChatCommand("tree")]
    private void TreeCommand(BasePlayer player, string command, string[] args)
    {
        if (!HasPermission(player, PERM_USE))
        {
            MessagePlayer(player, Lang(LangKeys.MESSAGE_PERMISSION, player.UserIDString));
            return;
        }
        
        if (args.Length == 0)
        {
            if (HasPermission(player, PERM_MENU))
            {
                _menuPages.TryGetValue(player.userID, out int currentPage);
                
                DisplayMenuUI(player, currentPage, true);
            }
            else
            {
                DisplayTrees(player);
            }
                
            return;
        }
        
        StringBuilder sb = Facepunch.Pool.Get<StringBuilder>();
        
        try
        {
            string name = FormattedName(ref sb, args);
            if (string.IsNullOrEmpty(name))
            {
                MessagePlayer(player, Lang(LangKeys.ERROR_NOT_FOUND, player.UserIDString));
                return;
            }
            
            TreeConfig treeConfig = _config.FindTreeConfig(name);
            if (treeConfig == null)
            {
                DisplayTrees(player);
                MessagePlayer(player, Lang(LangKeys.ERROR_NOT_FOUND, player.UserIDString));
                return;
            }
            
            if (!int.TryParse(args[args.Length - 1], out int multiplier) || multiplier < 1 || multiplier >= int.MaxValue)
                multiplier = 1;
            
            if (!TakeCurrency(player, treeConfig.Cost * multiplier))
            {
                MessagePlayer(player, Lang(LangKeys.MESSAGE_BALANCE, player.UserIDString));
                return;
            }
            
            int amount = treeConfig.Amount * multiplier;
            treeConfig.GiveItem(player, amount);
            MessagePlayer(player, Lang(LangKeys.MESSAGE_RECEIVED, player.UserIDString, amount, treeConfig.DisplayName));
        }
        finally
        {
            Facepunch.Pool.FreeUnmanaged(ref sb);
        }
    }

    #endregion

    #region Console Command
    
    [ConsoleCommand("treeplanter.close")]
    private void CloseConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (player == null)
            return;

        DestroyMenuUI(player);
    }

    [ConsoleCommand("treeplanter.page")]
    private void TreeConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (player == null || !HasPermission(player, PERM_MENU))
            return;
            
        if (!arg.HasArgs())
            return;

        _menuPages.TryGetValue(player.userID, out int currentPage);

        currentPage = arg.GetString(0) switch
        {
            "prev" => Math.Clamp((currentPage - 1), 0, _totalPages),
            "next" => Math.Clamp((currentPage + 1), 0, _totalPages),
            _ => currentPage
        };

        DisplayMenuUI(player, currentPage, false);
    }
        
    [ConsoleCommand("treeplanter.purchase")]
    private void TreePurchase(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (player == null || !HasPermission(player, PERM_MENU))
            return;
            
        if (!arg.HasArgs())
            return;

        TreeConfig treeConfig = _config.FindTreeConfig(arg.GetString(0));
        if (treeConfig == null)
        {
            DisplayPopupUI(player, UiBuilder.PopupStyle.Error, Lang(LangKeys.ERROR_NOT_FOUND, player.UserIDString));
            return;
        }

        int multiplier = arg.GetInt(1, 1);
        multiplier = Math.Clamp(multiplier, 1, int.MaxValue);
        if (!TakeCurrency(player, treeConfig.Cost * multiplier))
        {
            DisplayPopupUI(player, UiBuilder.PopupStyle.Error, Lang(LangKeys.MESSAGE_BALANCE, player.UserIDString));
            return;
        }

        int amount = treeConfig.Amount * multiplier;
        treeConfig.GiveItem(player, amount);
        DisplayPopupUI(player, UiBuilder.PopupStyle.Success, Lang(LangKeys.MESSAGE_RECEIVED, player.UserIDString, amount, treeConfig.DisplayName));
    }

    #endregion

    #region Helpers

    private bool HasPermission(BasePlayer player, string permName)
    {
        return permission.UserHasPermission(player.UserIDString, permName);
    }

    private bool SameClan(ulong userID, ulong ownerID)
    {
        string playerClan = Clans?.Call<string>("GetClanOf", userID);
        if (string.IsNullOrEmpty(playerClan)) 
            return false;
        
        string targetClan = Clans?.Call<string>("GetClanOf", ownerID);
        if (string.IsNullOrEmpty(targetClan)) 
            return false;
        
        return playerClan == targetClan;
    }
        
    private bool SameTeam(ulong userID, ulong ownerID)
    {
        RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance?.FindPlayersTeam(ownerID);
        return playerTeam != null && playerTeam.members.Contains(userID);
    }

    private bool IsAgrilBlocked(string shortname)
    {
        _config.AgriBlocked.TryGetValue(shortname, out bool blockedAgri);
        return blockedAgri;
    }
        
    private bool TakeCurrency(BasePlayer player, int cost)
    {
        if (cost == 0)
            return true;

        if (_config.UseServerRewards && ServerRewards is { IsLoaded: true })
        {
            if (ServerRewards.Call<object>("TakePoints", player.userID, cost) != null)
                return true;
        }

        if (_config.UseEconomics && Economics is { IsLoaded: true })
        {
            if (Economics.Call<bool>("Withdraw", player.userID, (double)cost))
                return true;
        }

        if (_config.UseCurrency && HasAmount(player, cost))
        {
            TakeAmount(player, cost);
            return true;
        }

        return false;
    }

    private bool IsEntityOwner(ulong userID, ulong ownerID)
    {
        if (_config.EnableOwner && userID == ownerID) 
            return true;
            
        if (_config.EnableTeam && SameTeam(userID, ownerID)) 
            return true;
            
        return !_config.EnableClan || SameClan(userID, ownerID);
    }
    
    private static string FormattedName(ref StringBuilder sb, string[] args)
    {
        int length = args.Length;
        if (length <= 0) return string.Empty;
        
        for (int i = 0; i < length; i++)
        {
            string strPart = args[i];
            if (strPart.Length <= 0) 
                continue;
            
            foreach (char c in strPart)
            {
                if (char.IsLetter(c))
                    sb.Append(c);
            }
                
            if (i < length - 1)
                sb.Append(' ');
        }
            
        return sb.ToString().Trim();
    }
        
    #endregion

    #region Inventory Methods | Needed To Check For Skinned Scrap

    private bool HasAmount(BasePlayer player, int amount) => GetAmount(player.inventory, _config.CurrencyItem, _config.CurrencySkinID) >= amount;

    private void TakeAmount(BasePlayer player, int amount) => TakeAmount(player.inventory, _config.CurrencyItem, _config.CurrencySkinID, amount);

    private static int GetAmount(PlayerInventory inventory, int itemId, ulong skinId = 0UL)
    {
        if (itemId == 0) 
            return 0;

        int totalAmount = GetAmountFromContainer(inventory.containerMain, itemId, skinId);
        totalAmount += GetAmountFromContainer(inventory.containerBelt, itemId, skinId);
            
        return totalAmount;
    }

    private static int GetAmountFromContainer(ItemContainer container, int itemId, ulong skinId = 0UL, bool usable = false)
    {
        if (container?.itemList == null) 
            return 0;

        int num = 0;
        foreach (Item obj in container.itemList)
        {
            if (obj.info.itemid == itemId && obj.skin == skinId && (!usable || !obj.IsBusy()))
                num += obj.amount;
        }
            
        return num;
    }

    private static int TakeAmount(PlayerInventory inventory, int itemId, ulong skinId, int amount)
    {
        int totalTaken = TakeAmountFromContainer(inventory.containerMain, itemId, skinId, amount);
        amount -= totalTaken;
            
        if (amount <= 0) 
            return totalTaken;
            
        totalTaken += TakeAmountFromContainer(inventory.containerBelt, itemId, skinId, amount);
        return totalTaken;
    }

    private static int TakeAmountFromContainer(ItemContainer container, int itemId, ulong skinId, int amount)
    {
        if (container?.itemList == null || amount == 0) 
            return 0;
            
        List<Item> itemsToRemove = Facepunch.Pool.Get<List<Item>>();
        int totalTaken = 0;
            
        foreach (Item obj in container.itemList)
        {
            if (obj.info.itemid != itemId || obj.skin != skinId) 
                continue;
                
            int remainingAmount = amount - totalTaken;
            if (remainingAmount <= 0) 
                break;
                
            int takenFromItem = Math.Min(obj.amount, remainingAmount);
            obj.MarkDirty();
            obj.amount -= takenFromItem;
            totalTaken += takenFromItem;
                
            if (obj.amount == 0)
                itemsToRemove.Add(obj);
                
            if (totalTaken == amount) 
                break;
        }
            
        foreach (Item obj in itemsToRemove)
        {
            obj.RemoveFromContainer();
            obj.Remove();
        }
            
        Facepunch.Pool.FreeUnmanaged(ref itemsToRemove);
        return totalTaken;
    }

    #endregion
}