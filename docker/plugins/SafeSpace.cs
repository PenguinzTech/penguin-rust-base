// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("SafeSpace", "PenguinzPlays", "1.0.0")]
    [Description("Kid-friendly server: blocks signs, global chat, voice, and notes unless player has permission")]
    class SafeSpace : RustPlugin
    {
        [PluginReference] Plugin GUIAnnouncements;

        private const string PermSigns      = "safespace.signs";
        private const string PermGlobalChat = "safespace.globalchat";
        private const string PermVoice      = "safespace.voice";
        private const string PermNotes      = "safespace.notes";

        private PluginConfig _config;

        #region Configuration

        private class PluginConfig
        {
            public bool BlockSigns      = true;
            public bool BlockGlobalChat = true;
            public bool BlockVoice      = true;
            public bool BlockNotes      = true;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        void Init()
        {
            permission.RegisterPermission(PermSigns, this);
            permission.RegisterPermission(PermGlobalChat, this);
            permission.RegisterPermission(PermVoice, this);
            permission.RegisterPermission(PermNotes, this);
        }

        void OnServerInitialized()
        {
            Puts($"SafeSpace active — Signs:{_config.BlockSigns} GlobalChat:{_config.BlockGlobalChat} Voice:{_config.BlockVoice} Notes:{_config.BlockNotes}");
        }

        // ─── Login announcement ─────────────────────────────────────────────
        // Voice is suppressed silently (spamming a message on every voice packet
        // is not viable), so we instead notify the player once on connect if
        // GUIAnnouncements is present. Falls back to ChatMessage if it isn't.
        void OnPlayerConnected(BasePlayer player)
        {
            if (!_config.BlockVoice) return;
            if (permission.UserHasPermission(player.UserIDString, PermVoice)) return;

            if (GUIAnnouncements != null)
                GUIAnnouncements.Call("CreateAnnouncement",
                    "Voice chat is disabled on this server.", "grey", "white", player);
            else
                player.ChatMessage("[SafeSpace] Voice chat is disabled on this server.");
        }

        // ─── Signs / photo frames ───────────────────────────────────────────
        // Fires when a player paints or applies an image to any Signage or
        // PhotoFrame entity. Return non-null to block the update.
        // Players can still place empty signs as building items.
        private object OnSignUpdate(BaseEntity sign, BasePlayer player,
            int textureIndex, byte[] stream, uint crc, string msg)
        {
            if (!_config.BlockSigns) return null;
            if (permission.UserHasPermission(player.UserIDString, PermSigns))
                return null;

            player.ChatMessage("[SafeSpace] Sign painting is disabled on this server.");
            return false;
        }

        // ─── Global chat ────────────────────────────────────────────────────
        // Fires before a chat message is broadcast. Only blocks Global channel;
        // Team, Local, Cards, and Clan channels are always allowed.
        private object OnPlayerChat(BasePlayer player, string message,
            Chat.ChatChannel channel)
        {
            if (!_config.BlockGlobalChat) return null;
            if (channel != Chat.ChatChannel.Global) return null;
            if (permission.UserHasPermission(player.UserIDString, PermGlobalChat))
                return null;

            player.ChatMessage("[SafeSpace] Global chat is disabled. Use team chat instead.");
            return false;
        }

        // ─── Voice chat ─────────────────────────────────────────────────────
        // Fires when voice data is received from a player. Return non-null to
        // suppress transmission. No chat message — would spam on every packet.
        private object OnPlayerVoice(BasePlayer player, Byte[] data)
        {
            if (!_config.BlockVoice) return null;
            if (permission.UserHasPermission(player.UserIDString, PermVoice))
                return null;

            return false;
        }

        // ─── Notes (workaround — no direct pre-hook exists) ─────────────────
        // Oxide has no pre-hook for note text editing (client-side UI). Instead
        // we clear item.text on item actions and loot-end events so anything
        // written by an unauthorized player is wiped before it can be shared.

        private void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (!_config.BlockNotes) return;
            if (item?.info == null || player == null) return;
            if (item.info.shortname != "note") return;
            if (permission.UserHasPermission(player.UserIDString, PermNotes))
                return;

            if (!string.IsNullOrEmpty(item.text))
            {
                item.text = string.Empty;
                item.MarkDirty();
                player.ChatMessage("[SafeSpace] Notes are disabled on this server.");
            }
        }

        private void OnPlayerLootEnd(PlayerLoot loot)
        {
            if (!_config.BlockNotes) return;
            var player = loot?.GetComponent<BasePlayer>();
            if (player == null) return;
            if (permission.UserHasPermission(player.UserIDString, PermNotes))
                return;

            ClearNoteText(player);
        }

        // Also clear notes when a player picks up items containing text
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (!_config.BlockNotes) return;
            if (item?.info == null || item.info.shortname != "note") return;
            if (string.IsNullOrEmpty(item.text)) return;

            var player = container?.playerOwner;
            if (player == null) return;
            if (permission.UserHasPermission(player.UserIDString, PermNotes))
                return;

            item.text = string.Empty;
            item.MarkDirty();
        }

        private void ClearNoteText(BasePlayer player)
        {
            if (player.inventory?.containerMain == null) return;
            foreach (var item in player.inventory.containerMain.itemList)
            {
                if (item?.info == null) continue;
                if (item.info.shortname != "note") continue;
                if (string.IsNullOrEmpty(item.text)) continue;
                item.text = string.Empty;
                item.MarkDirty();
            }
            foreach (var item in player.inventory.containerBelt.itemList)
            {
                if (item?.info == null) continue;
                if (item.info.shortname != "note") continue;
                if (string.IsNullOrEmpty(item.text)) continue;
                item.text = string.Empty;
                item.MarkDirty();
            }
        }

        // ─── Status command ─────────────────────────────────────────────────
        [ChatCommand("safespace")]
        private void CmdSafeSpace(BasePlayer player, string command, string[] args)
        {
            var signs = !_config.BlockSigns || permission.UserHasPermission(player.UserIDString, PermSigns);
            var chat  = !_config.BlockGlobalChat || permission.UserHasPermission(player.UserIDString, PermGlobalChat);
            var voice = !_config.BlockVoice || permission.UserHasPermission(player.UserIDString, PermVoice);
            var notes = !_config.BlockNotes || permission.UserHasPermission(player.UserIDString, PermNotes);

            player.ChatMessage(
                "[SafeSpace] Your permissions:\n" +
                $"  Signs:       {(signs ? "allowed" : "blocked")}\n" +
                $"  Global chat: {(chat  ? "allowed" : "blocked")}\n" +
                $"  Voice chat:  {(voice ? "allowed" : "blocked")}\n" +
                $"  Notes:       {(notes ? "allowed" : "blocked")}");
        }
    }
}
