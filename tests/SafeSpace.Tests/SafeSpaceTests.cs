using System.Reflection;
using Xunit;
using Oxide.Plugins;

namespace SafeSpace.Tests;

/// <summary>
/// Unit tests for SafeSpace.cs.
///
/// All plugin hooks are private, so tests invoke them via reflection using
/// <see cref="CallHook"/>. The Oxide permission system is accessed directly
/// through the public <c>permission</c> field on <c>RustPlugin</c>.
/// </summary>
public class SafeSpaceTests
{
    // Permission strings as declared in the plugin
    private const string PermSigns      = "safespace.signs";
    private const string PermGlobalChat = "safespace.globalchat";
    private const string PermVoice      = "safespace.voice";
    private const string PermNotes      = "safespace.notes";

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Create a plugin instance with all block flags enabled (default config).</summary>
    private static Oxide.Plugins.SafeSpace MakePlugin(
        bool blockSigns      = true,
        bool blockGlobalChat = true,
        bool blockVoice      = true,
        bool blockNotes      = true)
    {
        var plugin = new Oxide.Plugins.SafeSpace();

        // Reach into the private _config field and set flags directly.
        var configField = typeof(Oxide.Plugins.SafeSpace)
            .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var configType = configField.FieldType;
        var config     = Activator.CreateInstance(configType)!;
        SetField(config, "BlockSigns",      blockSigns);
        SetField(config, "BlockGlobalChat", blockGlobalChat);
        SetField(config, "BlockVoice",      blockVoice);
        SetField(config, "BlockNotes",      blockNotes);
        configField.SetValue(plugin, config);

        return plugin;
    }

    private static void SetField(object target, string name, object value)
    {
        var f = target.GetType().GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(target, value);
    }

    /// <summary>Invoke a private instance method by name and return its result.</summary>
    private static object? CallHook(Oxide.Plugins.SafeSpace plugin, string name, params object?[] args)
    {
        var method = typeof(Oxide.Plugins.SafeSpace)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(Oxide.Plugins.SafeSpace), name);
        return method.Invoke(plugin, args);
    }

    private static BasePlayer MakePlayer(string id = "76561198000000001")
        => new BasePlayer { UserIDString = id };

    private static Item MakeNote(string text = "")
        => new Item
        {
            info = new ItemDefinition { shortname = "note" },
            text = text,
        };

    // ─── OnSignUpdate ────────────────────────────────────────────────────────

    [Fact]
    public void OnSignUpdate_BlockEnabled_NoPermission_ReturnsNonNull()
    {
        var plugin = MakePlugin(blockSigns: true);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnSignUpdate",
            new BaseEntity(), player, 0, Array.Empty<byte>(), 0u, "");

        Assert.NotNull(result);
    }

    [Fact]
    public void OnSignUpdate_BlockEnabled_NoPermission_SendsChatMessage()
    {
        var plugin = MakePlugin(blockSigns: true);
        var player = MakePlayer();

        CallHook(plugin, "OnSignUpdate",
            new BaseEntity(), player, 0, Array.Empty<byte>(), 0u, "");

        Assert.Contains(player.SentMessages, m => m.Contains("SafeSpace"));
    }

    [Fact]
    public void OnSignUpdate_BlockEnabled_PlayerHasPermission_ReturnsNull()
    {
        var plugin = MakePlugin(blockSigns: true);
        var player = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermSigns);

        var result = CallHook(plugin, "OnSignUpdate",
            new BaseEntity(), player, 0, Array.Empty<byte>(), 0u, "");

        Assert.Null(result);
    }

    [Fact]
    public void OnSignUpdate_BlockDisabled_ReturnsNull()
    {
        var plugin = MakePlugin(blockSigns: false);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnSignUpdate",
            new BaseEntity(), player, 0, Array.Empty<byte>(), 0u, "");

        Assert.Null(result);
    }

    // ─── OnPlayerChat ────────────────────────────────────────────────────────

    [Fact]
    public void OnPlayerChat_GlobalChannel_NoPermission_ReturnsNonNull()
    {
        var plugin = MakePlugin(blockGlobalChat: true);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerChat",
            player, "hello world", Chat.ChatChannel.Global);

        Assert.NotNull(result);
    }

    [Fact]
    public void OnPlayerChat_GlobalChannel_NoPermission_SendsChatMessage()
    {
        var plugin = MakePlugin(blockGlobalChat: true);
        var player = MakePlayer();

        CallHook(plugin, "OnPlayerChat",
            player, "hello world", Chat.ChatChannel.Global);

        Assert.Contains(player.SentMessages, m => m.Contains("SafeSpace"));
    }

    [Fact]
    public void OnPlayerChat_TeamChannel_AlwaysAllowed()
    {
        var plugin = MakePlugin(blockGlobalChat: true);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerChat",
            player, "team chat", Chat.ChatChannel.Team);

        Assert.Null(result);
    }

    [Fact]
    public void OnPlayerChat_LocalChannel_AlwaysAllowed()
    {
        var plugin = MakePlugin(blockGlobalChat: true);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerChat",
            player, "local chat", Chat.ChatChannel.Local);

        Assert.Null(result);
    }

    [Fact]
    public void OnPlayerChat_GlobalChannel_PlayerHasPermission_ReturnsNull()
    {
        var plugin = MakePlugin(blockGlobalChat: true);
        var player = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermGlobalChat);

        var result = CallHook(plugin, "OnPlayerChat",
            player, "hello", Chat.ChatChannel.Global);

        Assert.Null(result);
    }

    [Fact]
    public void OnPlayerChat_BlockDisabled_ReturnsNull()
    {
        var plugin = MakePlugin(blockGlobalChat: false);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerChat",
            player, "hello", Chat.ChatChannel.Global);

        Assert.Null(result);
    }

    // ─── OnPlayerVoice ───────────────────────────────────────────────────────

    [Fact]
    public void OnPlayerVoice_BlockEnabled_NoPermission_ReturnsNonNull()
    {
        var plugin = MakePlugin(blockVoice: true);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerVoice",
            player, Array.Empty<byte>());

        Assert.NotNull(result);
    }

    [Fact]
    public void OnPlayerVoice_BlockEnabled_PlayerHasPermission_ReturnsNull()
    {
        var plugin = MakePlugin(blockVoice: true);
        var player = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermVoice);

        var result = CallHook(plugin, "OnPlayerVoice",
            player, Array.Empty<byte>());

        Assert.Null(result);
    }

    [Fact]
    public void OnPlayerVoice_BlockDisabled_ReturnsNull()
    {
        var plugin = MakePlugin(blockVoice: false);
        var player = MakePlayer();

        var result = CallHook(plugin, "OnPlayerVoice",
            player, Array.Empty<byte>());

        Assert.Null(result);
    }

    // ─── OnItemAction (notes) ────────────────────────────────────────────────

    [Fact]
    public void OnItemAction_Note_BlockEnabled_NoPermission_ClearsText()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("secret message");

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.Equal("", note.text);
    }

    [Fact]
    public void OnItemAction_Note_BlockEnabled_NoPermission_MarksItemDirty()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("secret message");

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.True(note.MarkedDirty);
    }

    [Fact]
    public void OnItemAction_Note_BlockEnabled_NoPermission_SendsChatMessage()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("secret message");

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.Contains(player.SentMessages, m => m.Contains("SafeSpace"));
    }

    [Fact]
    public void OnItemAction_Note_EmptyText_DoesNotSendMessage()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("");  // already empty — nothing to clear

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.Empty(player.SentMessages);
    }

    [Fact]
    public void OnItemAction_Note_PlayerHasPermission_TextUnchanged()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermNotes);
        var note = MakeNote("allowed text");

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.Equal("allowed text", note.text);
    }

    [Fact]
    public void OnItemAction_NonNoteItem_Ignored()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var rock   = new Item { info = new ItemDefinition { shortname = "rock" }, text = "text" };

        CallHook(plugin, "OnItemAction", rock, "some_action", player);

        Assert.Equal("text", rock.text);
        Assert.Empty(player.SentMessages);
    }

    [Fact]
    public void OnItemAction_BlockDisabled_TextUnchanged()
    {
        var plugin = MakePlugin(blockNotes: false);
        var player = MakePlayer();
        var note   = MakeNote("some text");

        CallHook(plugin, "OnItemAction", note, "some_action", player);

        Assert.Equal("some text", note.text);
    }

    // ─── OnItemAddedToContainer ──────────────────────────────────────────────

    [Fact]
    public void OnItemAddedToContainer_Note_WithText_NoPermission_ClearsText()
    {
        var plugin    = MakePlugin(blockNotes: true);
        var player    = MakePlayer();
        var container = new ItemContainer { playerOwner = player };
        var note      = MakeNote("forbidden text");

        CallHook(plugin, "OnItemAddedToContainer", container, note);

        Assert.Equal("", note.text);
        Assert.True(note.MarkedDirty);
    }

    [Fact]
    public void OnItemAddedToContainer_Note_NoOwner_DoesNotThrow()
    {
        var plugin    = MakePlugin(blockNotes: true);
        var container = new ItemContainer { playerOwner = null };
        var note      = MakeNote("text");

        // Should not throw — null player owner is a valid Oxide scenario
        var ex = Record.Exception(() =>
            CallHook(plugin, "OnItemAddedToContainer", container, note));

        Assert.Null(ex);
    }

    [Fact]
    public void OnItemAddedToContainer_Note_EmptyText_LeftAlone()
    {
        var plugin    = MakePlugin(blockNotes: true);
        var player    = MakePlayer();
        var container = new ItemContainer { playerOwner = player };
        var note      = MakeNote("");

        CallHook(plugin, "OnItemAddedToContainer", container, note);

        Assert.False(note.MarkedDirty);
    }

    [Fact]
    public void OnItemAddedToContainer_Note_PlayerHasPermission_TextUnchanged()
    {
        var plugin    = MakePlugin(blockNotes: true);
        var player    = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermNotes);
        var container = new ItemContainer { playerOwner = player };
        var note      = MakeNote("allowed");

        CallHook(plugin, "OnItemAddedToContainer", container, note);

        Assert.Equal("allowed", note.text);
    }

    // ─── OnPlayerLootEnd ─────────────────────────────────────────────────────

    [Fact]
    public void OnPlayerLootEnd_BlockEnabled_NoPermission_ClearsNotesInMain()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("secret");
        player.inventory.containerMain.itemList.Add(note);

        var loot = new PlayerLoot(player);
        CallHook(plugin, "OnPlayerLootEnd", loot);

        Assert.Equal("", note.text);
        Assert.True(note.MarkedDirty);
    }

    [Fact]
    public void OnPlayerLootEnd_BlockEnabled_NoPermission_ClearsNotesInBelt()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var note   = MakeNote("secret");
        player.inventory.containerBelt.itemList.Add(note);

        var loot = new PlayerLoot(player);
        CallHook(plugin, "OnPlayerLootEnd", loot);

        Assert.Equal("", note.text);
        Assert.True(note.MarkedDirty);
    }

    [Fact]
    public void OnPlayerLootEnd_BlockEnabled_PlayerHasPermission_TextUnchanged()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        plugin.permission.Grant(player.UserIDString, PermNotes);
        var note = MakeNote("keep this");
        player.inventory.containerMain.itemList.Add(note);

        var loot = new PlayerLoot(player);
        CallHook(plugin, "OnPlayerLootEnd", loot);

        Assert.Equal("keep this", note.text);
    }

    [Fact]
    public void OnPlayerLootEnd_NullPlayer_DoesNotThrow()
    {
        var plugin = MakePlugin(blockNotes: true);
        var loot   = new PlayerLoot(null);

        var ex = Record.Exception(() =>
            CallHook(plugin, "OnPlayerLootEnd", loot));

        Assert.Null(ex);
    }

    [Fact]
    public void OnPlayerLootEnd_NonNoteItems_LeftAlone()
    {
        var plugin = MakePlugin(blockNotes: true);
        var player = MakePlayer();
        var stone  = new Item { info = new ItemDefinition { shortname = "stones" }, text = "" };
        player.inventory.containerMain.itemList.Add(stone);

        var loot = new PlayerLoot(player);
        CallHook(plugin, "OnPlayerLootEnd", loot);

        Assert.False(stone.MarkedDirty);
    }
}
