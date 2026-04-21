// PATCHED by penguin-rust-base: Chat.ChatChannel->int in OnPlayerChat hook (type unresolvable in current Oxide build)
#define DEBUG
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
// PATCHED by penguin-rust-base: fixed removed Oxide APIs (FindByID→FindAwakeOrSleeping, net.connection→Connection, BuildingPrivlidge→BuildingPrivilege); fixed operator precedence in null-coalescing expression (line 361); fixed ulong→string for FindAwakeOrSleeping (line 440, 674); fixed OnPlayerChat hook signature (ConsoleSystem.Arg→BasePlayer+message+channel)

//AntiOfflineRaid created with PluginMerge v(1.0.6.0) by MJSU @ https://github.com/dassjosh/Plugin.Merge
namespace Oxide.Plugins
{
    [Info("Anti Offline Raid", "Calytic/Shady14u", "1.0.3")]
    [Description("Prevents/reduces offline raiding")]
    public partial class AntiOfflineRaid : RustPlugin
    {
        #region 1.AntiOfflineRaid.Config.cs
        private static Configuration _config;
        
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) LoadDefaultConfig();
                if (_config != null)
                {
                    _config.DamageScaleKeys = _config.DamageScale.Keys.Select(int.Parse).ToList();
                    _config.DamageScaleKeys.Sort();
                }
                
                SaveConfig();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                PrintWarning("Creating new config file.");
                LoadDefaultConfig();
            }
        }
        
        protected override void LoadDefaultConfig() => _config = Configuration.DefaultConfig();
        protected override void SaveConfig() => Config.WriteObject(_config);
        
        public class Configuration
        {
            [JsonProperty(PropertyName = "AbsoluteTimeScale")]
            public Dictionary<string, object> AbsoluteTimeScale = new();
            
            [JsonProperty(PropertyName = "DamageScale")]
            public Dictionary<string, object> DamageScale = new();
            
            public List<string> Activities { get; set; }
            
            public int AfkMinutes { get; set; }
            
            public bool ClanFirstOffline { get; set; }
            
            public bool ClanShare { get; set; }
            
            public int CooldownMinutes { get; set; }
            
            public float InterimDamage { get; set; }
            
            public int MinMembers { get; set; }
            
            public int MinutesSinceLastAttackToProtect { get; set; }
            
            public bool PlaySound { get; set; }
            
            public List<string> Prefabs { get; set; }
            
            public bool ProtectBaseWhenAway { get; set; }
            
            public int ServerTimeOffset { get; set; }
            
            public bool ShowMessage { get; set; }
            
            public string SoundFile { get; set; }
            public List<int> DamageScaleKeys { get; set; }
            
            
            public static Configuration DefaultConfig()
            {
                return new Configuration
                {
                    DamageScale = new Dictionary<string, object>
                    {{"1", 0.2}, {"3", 0.35f}, {"6", 0.5f}, {"12", 0.8f}, {"48", 1}},
                    AbsoluteTimeScale = new Dictionary<string, object> {{"03", 0.1}},
                    AfkMinutes = 5,
                    CooldownMinutes = 10,
                    InterimDamage = 0,
                    MinMembers = 1,
                    ClanShare = false,
                    ClanFirstOffline = false,
                    ShowMessage = true,
                    PlaySound = false,
                    Prefabs = new List<string>
                    {
                        "door.hinged", "door.double.hinged", "window.bars", "floor.ladder.hatch",
                        "floor.frame", "wall.frame", "shutter", "wall.external", "gates.external", "box", "locker"
                    },
                    Activities = GetDefaultActivities(),
                    ServerTimeOffset = 0,
                    SoundFile = "assets/prefabs/weapon mods/silencers/effects/silencer_attach.fx.prefab",
                    ProtectBaseWhenAway = false,
                    MinutesSinceLastAttackToProtect = 10,
                    DamageScaleKeys = new List<int>{1,3,6,12,48}
                };
            }
        }
        
        private static List<string> GetDefaultActivities()
        {
            return new List<string> { "input", "loot", "respawn", "chat", "wakeup" };
        }
        #endregion

        #region 2.AntiOfflineRaid.Localization.cs
        private static class PluginMessages
        {
            public const string ProtectionMessage = "Protection Message";
            public const string DeniedPermission = "Denied: Permission";
            
            
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [PluginMessages.ProtectionMessage] = "This building is protected: {amount}%",
                [PluginMessages.DeniedPermission] = "You lack permission to do that"
            }, this);
            
        }
        
        private string GetMsg(string key, object userId = null)
        {
            return lang.GetMessage(key, this, userId?.ToString());
        }
        
        void SendMessage(HitInfo hitInfo, int amt = 100)
        {
            if (hitInfo.Initiator is BasePlayer player)
            ShowMessage(player, amt);
        }
        #endregion

        #region 3.AntiOfflineRaid.Permissions.cs
        private static class PluginPermissions
        {
            public const string Protect = "antiofflineraid.protect";
            public const string Check = "antiofflineraid.check";
            
            
        }
        private void LoadPermissions()
        {
            permission.RegisterPermission(PluginPermissions.Protect, this);
            permission.RegisterPermission(PluginPermissions.Check, this);
        }
        
        bool HasPerm(BasePlayer p, string pe)
        {
            return permission.UserHasPermission(p.userID.ToString(), pe);
        }
        
        bool HasPerm(string userid, string pe)
        {
            return permission.UserHasPermission(userid, pe);
        }
        #endregion

        #region 4.AntiOfflineRaid.Data.cs
        private StoredData _storedData;
        
        public class StoredData
        {
            public Dictionary<ulong, LastOnline> LastOnlines { get; set; } = new Dictionary<ulong, LastOnline>();
            public Dictionary<string, List<string>> MembersCache { get; set; } = new Dictionary<string, List<string>>();
        }
        
        #region BoilerPlate
        private void LoadData()
        {
            try
            {
                _storedData = Interface.GetMod().DataFileSystem.ReadObject<StoredData>("AntiOfflineRaid");
            }
            catch (Exception e)
            {
                Puts(e.Message);
                Puts(e.StackTrace);
                _storedData = new StoredData();
            }
        }
        
        private void SaveData()
        {
            Interface.GetMod().DataFileSystem.WriteObject("AntiOfflineRaid", _storedData);
        }
        
        #endregion
        #endregion

        #region 5.AntiOfflineRaid.Hooks.cs
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (hitInfo == null || entity == null)
            return null;
            
            if (hitInfo.damageTypes.Has(DamageType.Decay))
            {
                return null;
            }
            
            return IsBlocked(entity)
            ? OnStructureAttack(entity, hitInfo)
            : null;
        }
        
        void OnLootItem(BasePlayer player, Item item) => UpdateLastOnline(player.userID);
        
        void OnLootPlayer(BasePlayer player, BasePlayer target) => UpdateLastOnline(player.userID);
        
        void OnPlayerChat(BasePlayer player, string message, int channel) => UpdateLastOnline(player.userID);
        
        void OnPlayerConnected(BasePlayer player) => UpdateLastOnline(player.userID);
        
        void OnPlayerDisconnected(BasePlayer player) => UpdateLastOnline(player.userID);
        
        void OnPlayerRespawn(BasePlayer player) => UpdateLastOnline(player.userID);
        
        void OnPlayerSleepEnded(BasePlayer player) => UpdateLastOnline(player.userID);
        
        void OnServerInitialized()
        {
            LoadPermissions();
            LoadData();
            
            if (_config.ClanShare)
            {
                if (!plugins.Exists("Clans"))
                {
                    _config.ClanShare = false;
                    PrintWarning("Clans not found! clanShare disabled. Cannot use clanShare without a clans plugin.");
                }
            }
            
            if (!_config.Activities.Any())
            {
                PrintWarning("At least one activity trigger must be configured for this plugin to operate.");
                ToggleHooks(new List<string>());
            }
            else
            {
                ToggleHooks(_config.Activities);
                UpdateLastOnlineAll(false);
                _lastOnlineTimer = timer.Repeat(TickRate * 60, 0, () => UpdateLastOnlineAll());
            }
        }
        
        private void OnServerSave()
        {
            SaveData();
        }
        
        void OnClanCreate(string tag)
        {
            CacheClan(tag);
        }
        
        void OnClanUpdate(string tag)
        {
            CacheClan(tag);
        }
        
        void OnClanDestroy(string tag)
        {
            if (_storedData.MembersCache.ContainsKey(tag))
            {
                _storedData.MembersCache.Remove(tag);
            }
        }
        
        protected void ToggleHook(List<string> wakeupTriggers, string trigger, string hook)
        {
            if (wakeupTriggers.Contains(trigger))
            {
                Subscribe(hook);
            }
            else
            {
                Unsubscribe(hook);
            }
        }
        
        protected void ToggleHooks(List<string> wakeupTriggers)
        {
            var defaultWakeupTriggers = GetDefaultActivities();
            
            foreach (var trigger in defaultWakeupTriggers)
            {
                switch (trigger)
                {
                    case "input":
                    ToggleHook(wakeupTriggers, trigger, "OnPlayerInput");
                    break;
                    case "loot":
                    ToggleHook(wakeupTriggers, trigger, "OnLootPlayer");
                    ToggleHook(wakeupTriggers, trigger, "OnLootItem");
                    break;
                    case "respawn":
                    ToggleHook(wakeupTriggers, trigger, "OnPlayerRespawn");
                    break;
                    case "chat":
                    ToggleHook(wakeupTriggers, trigger, "OnPlayerChat");
                    break;
                    case "wakeup":
                    ToggleHook(wakeupTriggers, trigger, "OnPlayerSleepEnded");
                    break;
                }
            }
        }
        
        private void Unload()
        {
            UpdateLastOnlineAll();
            SaveData();
        }
        #endregion

        #region 6.AntiOfflineRaid.Commands.cs
        [ConsoleCommand("ao")]
        void CheckOfflineStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null) return;
            if (arg.Connection.player is not BasePlayer player) return;
            
            if (!HasPerm(player, PluginPermissions.Check) && arg.Connection.authLevel < 1)
            {
                SendReply(arg, GetMsg("Denied: Permission", arg.Connection.userid));
                return;
            }
            SendReply(arg, SendStatus(arg.Args[0]));
            
        }
        
        [ChatCommand("ao")]
        void CheckOfflineStatus(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PluginPermissions.Check) && (player.net?.connection?.authLevel ?? 0) < 1)
            {
                SendReply(player, GetMsg("Denied: Permission", player.UserIDString));
                return;
            }
            
            SendReply(player, SendStatus(args[0]));
        }
        
        [ChatCommand("ao.fill.onlineTimes")]
        void CmdFillOnlines(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            return;
            
            foreach (var basePlayer in covalence.Players.All)
            {
                UpdateLastOnline(Convert.ToUInt64(basePlayer.Id));
            }
            
        }
        
        void SendHelpText(BasePlayer player)
        {
            var stringBuilder = new StringBuilder("AntiOfflineRaid by <color=#ce422b>Shady14u</color>\n");
            
            if (_config.CooldownMinutes > 0)
            {
                stringBuilder.Append($"  <color=\"#ffd479\">First {_config.CooldownMinutes} minutes</color>: 100%\n");
                stringBuilder.Append($"  <color=\"#ffd479\">Between {_config.CooldownMinutes} minutes and 1 hour</color>: {_config.InterimDamage * 100}%\n");
            }
            else
            {
                stringBuilder.Append($"  <color=\"#ffd479\">First hour</color>: {_config.InterimDamage * 100}%\n");
            }
            
            foreach (var key in _config.DamageScaleKeys)
            {
                var scale = Math.Round(Convert.ToDouble(_config.DamageScale[key.ToString()]) * 100, 0);
                var hours = Math.Round(Convert.ToDouble(key), 1);
                if (hours >= 24)
                {
                    var days = Math.Round(hours / 24, 1);
                    stringBuilder.Append($"  <color=\"#ffd479\">After {days} days(s)</color>: {scale}%\n");
                }
                else
                {
                    stringBuilder.Append($"  <color=\"#ffd479\">After {hours} hour(s)</color>: {scale}%\n");
                }
            }
            
            player.ChatMessage(stringBuilder.ToString());
        }
        #endregion

        #region 7.AntiOfflineRaid.Classes.cs
        public class LastOnline
        {
            [JsonIgnore] public float AfkMinutes = 0;
            
            public long LastOnlineLong;
            
            [JsonIgnore] public Vector3 LastPosition;
            
            public ulong UserId;
            
            [JsonConstructor]
            public LastOnline(ulong userid, long lastOnlineLong)
            {
                UserId = userid;
                LastOnlineLong = lastOnlineLong;
            }
            
            public LastOnline(ulong userId, DateTime lastOnline)
            {
                UserId = userId;
                LastOnlineTime = lastOnline;
            }
            
            [JsonIgnore] public BasePlayer AarPlayer => BasePlayer.FindAwakeOrSleeping(UserId.ToString());
            
            [JsonIgnore] public double Days => (DateTime.Now - LastOnlineTime).TotalDays;
            
            [JsonIgnore] public double Hours => (DateTime.Now - LastOnlineTime).TotalHours;
            
            [JsonIgnore]
            public DateTime LastOnlineTime
            {
                get => DateTime.FromBinary(LastOnlineLong);
                
                set => LastOnlineLong = value.ToBinary();
            }
            
            [JsonIgnore] public double Minutes => (DateTime.Now - LastOnlineTime).TotalMinutes;
            
            public bool HasMoved(Vector3 position)
            {
                if (LastPosition.Equals(position)) return true;
                LastPosition = new Vector3(position.x, position.y, position.z);
                return false;
            }
            
            public bool IsAfk()
            {
                return AfkMinutes >= _config.AfkMinutes;
            }
            
            public bool IsConnected()
            {
                var player = AarPlayer;
                return player != null && player.IsConnected;
            }
            
            public bool IsOffline()
            {
                return !IsConnected() && Minutes >= _config.CooldownMinutes;
            }
        }
        
        class ScaleCacheItem
        {
            public DateTime Expires;
            public float Scale;
            
            public ScaleCacheItem(DateTime expires, float scale)
            {
                Expires = expires;
                Scale = scale;
            }
        }
        #endregion

        #region 0.AntiOfflineRaid.cs
        [PluginReference] Plugin Clans;
        private const string JsonMessage =
        @"[{""name"":""AntiOfflineRaidMsg"",""parent"":""Overlay"",""components"":[{""type"":""UnityEngine.UI.Image"",""color"":""0 0 0 0.78""},{""type"":""RectTransform"",""anchormax"":""0.64 0.88"",""anchormin"":""0.38 0.79""}]},{""name"":""MessageLabel{1}"",""parent"":""AntiOfflineRaidMsg"",""components"":[{""type"":""UnityEngine.UI.Text"",""align"":""MiddleCenter"",""fontSize"":""19"",""text"":""{protection_message}""},{""type"":""RectTransform"",""anchormax"":""1 1"",""anchormin"":""0 0""}]}]";
        
        private const float TickRate = 5f;
        private readonly Dictionary<ulong, ScaleCacheItem> _cachedScales = new();
        private Timer _lastOnlineTimer;
        
        public List<string> CacheClan(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            
            var clan = Clans.Call<JObject>("GetClan", tag);
            var members = new List<string>();
            
            if (clan?["members"] == null) return members;
            
            members.AddRange(clan["members"].Select(m => (string) m));
            
            _storedData.MembersCache[tag] = members;
            return members;
        }
        
        float CacheDamageScale(ulong targetId, float scale, ScaleCacheItem kvp = null)
        {
            if (kvp == null)
            {
                kvp = new ScaleCacheItem(DateTime.Now.AddMinutes(1), scale);
                _cachedScales.Add(targetId, kvp);
            }
            else
            {
                kvp.Expires = DateTime.Now.AddMinutes(1);
                kvp.Scale = scale;
            }
            
            return scale;
        }
        
        protected IPlayer FindPlayerByPartialName(string nameOrIdOrIp)
        {
            return string.IsNullOrEmpty(nameOrIdOrIp) ? null : covalence.Players.FindPlayer(nameOrIdOrIp);
        }
        
        public List<string> GetClanMembers(string tag)
        {
            return _storedData.MembersCache.TryGetValue(tag, out var memberList) ? memberList : CacheClan(tag);
        }
        
        public int GetClanMembersOnline(ulong targetId)
        {
            var player = covalence.Players.FindPlayerById(targetId.ToString());
            if (player == null) return 0;
            
            if (Clans == null)
            {
                PrintWarning("Clans plugin not installed");
                _config.ClanShare = false;
                return player.IsConnected ? 1 : 0;
            }
            
            var tag = Clans.Call<string>("GetClanOf", targetId);
            if (tag == null)
            {
                return player.IsConnected ? 1 : 0;
            }
            
            var members = GetClanMembers(tag);
            var mCount = 0;
            foreach (var memberId in members)
            {
                if (string.IsNullOrEmpty(memberId)) continue;
                var mid = Convert.ToUInt64(memberId);
                if (mid == targetId) continue;
                if (!IsOffline(mid)) mCount++;
            }
            
            return mCount;
        }
        
        public ulong GetClanOffline(string tag)
        {
            var clanMembers = GetClanMembers(tag);
            if (clanMembers == null || clanMembers.Count == 0) return 0;
            
            var members = new Dictionary<string, double>();
            foreach (var memberId in clanMembers)
            {
                var mid = Convert.ToUInt64(memberId);
                if (_storedData.LastOnlines.TryGetValue(mid, out var lastOnlineMember))
                {
                    members.Add(memberId, lastOnlineMember.Minutes);
                }
            }
            
            return _config.ClanFirstOffline
            ?
            // loop through all offline members, sorted by who is most offline
            members.OrderByDescending(p => p.Value).Select(kvp => Convert.ToUInt64(kvp.Key)).FirstOrDefault()
            :
            // loop through all offline members, sorted by who is least offline
            members.OrderBy(p => p.Value).Select(kvp => Convert.ToUInt64(kvp.Key)).FirstOrDefault();
        }
        
        private DateTime GetPlayersLastOnline(ulong memberId)
        {
            if (_storedData.LastOnlines.TryGetValue(memberId, out var playerLastOnline))
            {
                return playerLastOnline.IsConnected() ? DateTime.Now.AddMinutes(1) : playerLastOnline.LastOnlineTime;
            }
            
            return DateTime.Now.AddHours(-1);
        }
        
        void HideMessage(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "AntiOfflineRaidMsg");
        }
        
        private bool IsAuthorizedOnline(BaseEntity entity)
        {
            var privilege = entity.GetBuildingPrivilege();
            if (privilege == null) return true;
            if (!privilege.authorizedPlayers.Any()) return true;
            foreach (var privilegePlayer in privilege.authorizedPlayers)
            {
                if (!IsOffline(privilegePlayer))
                {
                    return true;
                }
            }
            return false;
        }
        
        public bool IsBlocked(BaseCombatEntity entity)
        {
            if (entity is BuildingBlock) return true;
            var prefabName = entity?.ShortPrefabName;
            return !string.IsNullOrEmpty(prefabName) && _config.Prefabs.Any(x => prefabName.Contains(x));
        }
        
        private bool IsClanInRange(ulong targetId)
        {
            var tag = Clans?.Call<string>("GetClanOf", targetId);
            if (tag == null)
            {
                return IsPlayerInRange(targetId);
            }
            
            var members = GetClanMembers(tag);
            
            foreach (var memberId in members)
            {
                if (string.IsNullOrEmpty(memberId))
                continue;
                var mid = Convert.ToUInt64(memberId);
                if (IsPlayerInRange(mid)) return true;
            }
            
            return false;
        }
        
        public bool IsClanOffline(ulong targetId)
        {
            return GetClanMembersOnline(targetId) < _config.MinMembers;
        }
        
        public bool IsOffline(ulong playerId)
        {
            if (_storedData.LastOnlines.TryGetValue(playerId, out var lastOnlinePlayer))
            {
                return lastOnlinePlayer.IsOffline();
            }
            
            var player = covalence.Players.FindPlayerById(playerId.ToString());
            return player is not {IsConnected: true};
        }
        
        private static bool IsPlayerInRange(ulong targetId)
        {
            var player = BasePlayer.FindAwakeOrSleeping(targetId.ToString());
            return player != null && player.IsBuildingAuthed();
        }
        
        public object MitigateDamage(HitInfo hitInfo, float scale)
        {
            if (scale < 0 ) return null;
            
            var isFire = hitInfo.damageTypes.GetMajorityDamageType() == DamageType.Heat;
            
            if (scale == 0)
            {
                if (_config.ShowMessage && ((isFire && hitInfo.WeaponPrefab != null) || !isFire))
                SendMessage(hitInfo);
                
                if (_config.PlaySound && hitInfo.Initiator is BasePlayer && !isFire)
                Effect.server.Run(_config.SoundFile, hitInfo.Initiator.transform.position);
                return true;
            }
            
            hitInfo.damageTypes.ScaleAll(scale);
            if (!(scale < 1)) return null;
            
            if (_config.ShowMessage && ((isFire && hitInfo.WeaponPrefab != null) || (!isFire)))
            SendMessage(hitInfo, 100 - Convert.ToInt32(scale * 100));
            
            if (_config.PlaySound && hitInfo.Initiator is BasePlayer && !isFire)
            Effect.server.Run(_config.SoundFile, hitInfo.Initiator.transform.position);
            
            return null;
        }
        
        object OnStructureAttack(BaseEntity entity, HitInfo hitInfo)
        {
            var targetId = entity.OwnerID;
            if (!targetId.IsSteamId() || !HasPerm(targetId.ToString(), PluginPermissions.Protect) ||
            !_storedData.LastOnlines.TryGetValue(targetId, out var lastOnline)) return null;
            
            var buildBlock = entity as BuildingBlock;
            if (buildBlock != null && buildBlock.grade == BuildingGrade.Enum.Twigs) return null;
            
            var clanLastOnline = lastOnline.IsConnected()
            ? lastOnline
            : _storedData.LastOnlines[RecentActiveClanMember(targetId)];
            
            if ((DateTime.Now - clanLastOnline.LastOnlineTime).Minutes <= _config.MinutesSinceLastAttackToProtect)
            {
                UpdateLastOnline(targetId);
            }
            
            var scale = ScaleDamageCached(clanLastOnline);
            
            if (!IsAuthorizedOnline(entity)) return MitigateDamage(hitInfo, scale);
            
            if (_config.ProtectBaseWhenAway)
            {
                return IsClanInRange(targetId) ? null : MitigateDamage(hitInfo, 0);
            }
            
            return null;
        }
        
        private ulong RecentActiveClanMember(ulong targetId)
        {
            var tag = Clans?.Call<string>("GetClanOf", targetId);
            if (tag == null)
            {
                return targetId;
            }
            
            var members = GetClanMembers(tag);
            var lastActive = targetId;
            var memberOnline = GetPlayersLastOnline(targetId);
            foreach (var memberId in members.Where(x => !string.IsNullOrEmpty(x)))
            {
                var memberLast = GetPlayersLastOnline(ulong.Parse(memberId));
                if (memberLast <= memberOnline) continue;
                memberOnline = memberLast;
                lastActive = ulong.Parse(memberId);
            }
            
            return lastActive;
        }
        
        public float ScaleDamage(LastOnline lastOnline)
        {
            if (lastOnline == null || !lastOnline.IsOffline())
            {
                return -1;
            }
            
            // if absolute scale is configured, override relative scaling
            if (_config.AbsoluteTimeScale.Count > 0)
            {
                var hour = DateTime.Now.ToString("HH", DateTimeFormatInfo.InvariantInfo);
                if (_config.AbsoluteTimeScale.TryGetValue(hour, out var scaleObj))
                {
                    return Convert.ToSingle(scaleObj);
                }
            }
            
            float scale = -1;
            if (lastOnline.Minutes >= 60)
            {
                var scaleKey = _config.DamageScaleKeys.FirstOrDefault(x => x <= lastOnline.Hours);
                if (_config.DamageScale.TryGetValue(scaleKey.ToString(), out var scaleValue))
                {
                    scale = Convert.ToSingle(scaleValue);
                }
            }
            else
            {
                scale = _config.InterimDamage;
            }
            
            return scale;
        }
        
        float ScaleDamageCached(LastOnline lastOnline)
        {
            if (_cachedScales.TryGetValue(lastOnline.UserId, out var kvp) && DateTime.Now < kvp.Expires)
            {
                return kvp.Scale;
            }
            
            return CacheDamageScale(lastOnline.UserId, ScaleDamage(lastOnline), kvp);
        }
        
        string SendStatus(string playerSearch)
        {
            if (string.IsNullOrEmpty(playerSearch)) return "Invalid Syntax. ao <PlayerName>";
            
            var target = FindPlayerByPartialName(playerSearch);
            if (target == null || !_storedData.LastOnlines.TryGetValue(Convert.ToUInt64(target.Id), out var lo)) return "No player found.";
            
            var stringBuilder = new StringBuilder();
            if (IsOffline(Convert.ToUInt64(target.Id)))
            {
                stringBuilder.AppendLine(
                "<color=#F31D07><size=15>AntiOfflineRaid Status</size></color>: " + target.Name);
                stringBuilder.AppendLine(target.IsConnected
                ? $"<color=#56C5D5>Player Status</color>: <color=#E49C63>AFK</color>: {lo.LastOnlineTime.AddHours(_config.ServerTimeOffset)} EST"
                : $"<color=#56C5D5>Player Status</color>: <color=#F31D07>Offline</color>: {lo.LastOnlineTime.AddHours(_config.ServerTimeOffset)} EST");
            }
            else
            {
                stringBuilder.AppendLine(
                $"<color=#3CF348><size=15>AntiOfflineRaid Status</size></color>: {target.Name}");
                stringBuilder.AppendLine("<color=#56C5D5>Player Status</color>: <color=#3CF348>Online</color>");
            }
            
            stringBuilder.AppendLine($"<color=#56C5D5>AFK</color>: {lo.AfkMinutes} minutes");
            if (_config.ClanShare)
            {
                stringBuilder.AppendLine(
                $"<color=#56C5D5>Clan Status</color>: {(IsClanOffline(Convert.ToUInt64(target.Id)) ? "<color=#F31D07>Offline</color>" : "<color=#3CF348>Online</color>")} ({GetClanMembersOnline(Convert.ToUInt64(target.Id))})");
                var tag = Clans.Call<string>("GetClanOf", target.Id);
                if (!string.IsNullOrEmpty(tag))
                {
                    var msg = _config.ClanFirstOffline ? "First Offline" : "Last Offline";
                    if (_storedData.LastOnlines.TryGetValue(GetClanOffline(tag), out var lastOfflinePlayer))
                    {
                        var p = covalence.Players.FindPlayerById(lastOfflinePlayer.UserId.ToString());
                        stringBuilder.AppendLine(
                        $"<color=#56C5D5>Clan {msg}</color>: {p.Name} - {lastOfflinePlayer.LastOnlineTime.AddHours(_config.ServerTimeOffset)} EST");
                    }
                }
            }
            
            var scale = ScaleDamage(lo);
            if (scale > -1)
            {
                stringBuilder.AppendLine($"<color=#56C5D5>Scale</color>: {scale*100}%");
            }
            
            return stringBuilder.ToString();
        }
        
        void ShowMessage(BasePlayer player, int amount = 100)
        {
            HideMessage(player);
            var sb = new StringBuilder();
            sb.Clear();
            sb.Append(JsonMessage);
            sb.Replace("{1}", Core.Random.Range(1, 99999).ToString());
            sb.Replace("{protection_message}", GetMsg("Protection Message", player.UserIDString));
            sb.Replace("{amount}", amount.ToString());
            
            CuiHelper.AddUi(player, sb.ToString());
            
            timer.In(3f, () => HideMessage(player));
        }
        
        void UpdateLastOnline(ulong playerId, bool hasMoved = true)
        {
            if (!_storedData.LastOnlines.TryGetValue(playerId, out var lastOnlinePlayer))
            {
                _storedData.LastOnlines.Add(playerId, new LastOnline(playerId, DateTime.Now));
            }
            else
            {
                lastOnlinePlayer.LastOnlineTime = DateTime.Now;
                if (hasMoved)
                lastOnlinePlayer.AfkMinutes = 0;
            }
        }
        
        void UpdateLastOnlineAll(bool afkCheck = true)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!player.IsConnected)
                continue;
                
                var hasMoved = true;
                
                if (afkCheck)
                {
                    if (_storedData.LastOnlines.TryGetValue(player.userID, out var lastOnlinePlayer))
                    {
                        if (!lastOnlinePlayer.HasMoved(player.transform.position))
                        {
                            hasMoved = false;
                            lastOnlinePlayer.AfkMinutes += TickRate;
                        }
                        
                        if (lastOnlinePlayer.IsAfk())
                        {
                            continue;
                        }
                    }
                }
                
                UpdateLastOnline(player.userID, hasMoved);
            }
        }
        #endregion

    }

}
