// Requires: BetterChat
// PATCHED by penguin-rust-base: null guard BetterChat reference before CallHook in OnPluginLoaded to prevent NPE when BetterChat loads after ClanTags

using System;
using Oxide.Core;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Clan Tags", "klauz24", "1.0.2"), Description("Adds clan tag support for Better Chat")]
    internal class ClanTags : CovalencePlugin
    {
        [PluginReference] readonly Plugin BetterChat, Clans, HWClans;

        private Configuration _config;

        private Dictionary<string, string> _customClanTagColors;

        private class Configuration
        {
            [JsonProperty(PropertyName = "Permission")]
            public string Permission = "clantags.color";

            [JsonProperty(PropertyName = "Default clan tag color")]
            public string DefaultClanTagColor = "#00ff00";

            [JsonProperty(PropertyName = "Formatting")]
            public string Formatting = "<color={color}>[{clanTag}]</color>";

            [JsonProperty(PropertyName = "Incompatible plugins (Plugin name, Author)")]
            public Dictionary<string, string> IncompatiblePlugins = new Dictionary<string, string>()
            {
                {"Clans", "k1lly0u"},
                {"Rust:IO Clans", "playrust.io / dcode"}
            };

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (!_config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    Puts("Configuration appears to be outdated; updating and saving.");
                    SaveConfig();
                }
            }
            catch
            {
                Puts($"Configuration file {Name}.json is invalid; using defaults.");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Syntax", "Syntax: /clancol <hex>."},
                {"Invalid", "Invalid HEX color code. (Ex: {0})" },
                {"New", "You have setup a new color for your clan tag."},
                {"NoPerm", "You do not have permission to use this command."}
            }, this);
        }

        [Command("clancol")]
        private void ClanColorCommand(IPlayer player, string command, string[] args)
        {
            if (permission.UserHasPermission(player.Id, _config.Permission))
            {
                if (args.Length == 0)
                {
                    player.Message(GetLang(player, "Syntax"));
                }
                else
                {
                    var newColor = args[0];
                    if (newColor.StartsWith("#"))
                    {
                        if (_customClanTagColors.ContainsKey(player.Id))
                        {
                            _customClanTagColors[player.Id] = newColor;
                        }
                        else
                        {
                            _customClanTagColors.Add(player.Id, newColor);
                        }
                        player.Message(GetLang(player, "New"));
                        Interface.Oxide.DataFileSystem.WriteObject("CustomClanTagColors", _customClanTagColors);
                    }
                    else
                    {
                        player.Message(string.Format(GetLang(player, "Invalid"), _config.DefaultClanTagColor));
                    }
                }
            }
            else
            {
                player.Message(GetLang(player, "NoPerm"));
            }
        }

        private void Init()
        {
            permission.RegisterPermission(_config.Permission, this);
            try
            {
                _customClanTagColors = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>("CustomClanTagColors");
            }
            catch
            {
                _customClanTagColors = new Dictionary<string, string>();
            }
        }

        private void OnPluginLoaded()
        {
            foreach(var plugin in Manager.GetPlugins().ToArray())
            {
                if (plugin != null && IsIncompatiblePlugin(plugin))
                {
                    PrintWarning($"{plugin.Name} is not compatible with this plugin as it already provides clan tags through Better Chat.");
                    Interface.Oxide.UnloadPlugin(this.Name);
                }
            }
            BetterChat?.CallHook("API_RegisterThirdPartyTitle", this, new Func<IPlayer, string>(GetClanTag));
        }

        private bool IsIncompatiblePlugin(Plugin plugin)
        {
            foreach (var kvp in _config.IncompatiblePlugins)
            {
                if ((plugin.Name == kvp.Key) && (plugin.Author == kvp.Value))
                {
                    return true;
                }
            }
            return false;
        }

        private string GetClanTag(IPlayer player)
        {
            var clansTag = GetClansTag(player.Id);
            if (clansTag != null)
            {
                return GetFormattedTag(player.Id, clansTag);
            }
#if HURTWORLD
            else
            {
                var session = player.Object as PlayerSession;
                var hwClansTag = GetHWClansTag(session);
                if (hwClansTag != null)
                {
                    return GetFormattedTag(player.Id, hwClansTag);
                }
                else
                {
                    var nativeClansTag = GetNativeClansTag(session);
                    if (nativeClansTag != null)
                    {
                        return GetFormattedTag(player.Id, nativeClansTag);
                    }
                }
            }
#endif
            return null;
        }

        private string GetClanTagColor(string id)
        {
            if (_customClanTagColors.ContainsKey(id))
            {
                return _customClanTagColors[id];
            }
            else
            {
                return _config.DefaultClanTagColor;
            }
        }

        private string GetFormattedTag(string id, string clanTag) => _config.Formatting.Replace("{color}", GetClanTagColor(id)).Replace("{clanTag}", clanTag);

        private string GetClansTag(string id) => Clans?.Call<string>("GetClanOf", id);

        private string GetLang(IPlayer player, string str) => lang.GetMessage(str, this, player.Id);

#if HURTWORLD
        private string GetHWClansTag(PlayerSession session) => HWClans?.Call<string>("getClanTag", session);
        
        private string GetNativeClansTag(PlayerSession session) => session.Identity.Clan != null ? session.Identity.Clan.ClanTag : null;
#endif
    }
}