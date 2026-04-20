// Minimal Oxide API stubs — just enough for SafeSpace.cs to compile and be tested.
// These types mirror the real Oxide signatures so no changes are needed to the plugin source.

using System.Reflection;

namespace Oxide.Core
{
    public abstract class Plugin
    {
        public string Name   { get; protected set; } = "";
        public string Author { get; protected set; } = "";
        public string Version { get; protected set; } = "";
    }
}

namespace Oxide.Core.Libraries
{
    /// <summary>Stub for Oxide's permission system.</summary>
    public class Permission
    {
        private readonly HashSet<string> _granted = new();

        public void RegisterPermission(string perm, Oxide.Core.Plugin owner) { }

        public bool UserHasPermission(string userId, string perm)
            => _granted.Contains(Key(userId, perm));

        /// <summary>Test helper: grant a permission to a player.</summary>
        public void Grant(string userId, string perm)
            => _granted.Add(Key(userId, perm));

        /// <summary>Test helper: revoke a permission from a player.</summary>
        public void Revoke(string userId, string perm)
            => _granted.Remove(Key(userId, perm));

        private static string Key(string userId, string perm) => $"{userId}:{perm}";
    }
}

// ─── Rust / Oxide game types ────────────────────────────────────────────────

public class ItemDefinition
{
    public string shortname { get; set; } = "";
}

public class Item
{
    public ItemDefinition? info { get; set; }
    public string text { get; set; } = "";
    public bool MarkedDirty { get; private set; }
    public void MarkDirty() => MarkedDirty = true;
}

public class ItemContainer
{
    public List<Item> itemList { get; set; } = new();
    public BasePlayer? playerOwner { get; set; }
}

public class PlayerInventory
{
    public ItemContainer containerMain { get; set; } = new();
    public ItemContainer containerBelt { get; set; } = new();
}

public class BaseEntity { }

public class BasePlayer : BaseEntity
{
    public string UserIDString { get; set; } = "";
    public PlayerInventory inventory { get; set; } = new();
    public List<string> SentMessages { get; } = new();

    public void ChatMessage(string msg) => SentMessages.Add(msg);
}

public class PlayerLoot
{
    private readonly BasePlayer? _player;
    public PlayerLoot(BasePlayer? player = null) => _player = player;
    public T? GetComponent<T>() where T : class => _player as T;
}

public static class Chat
{
    public enum ChatChannel { None, Global, Team, Local, Cards, Console, Clan }
}

// ─── Oxide plugin infrastructure ────────────────────────────────────────────

/// <summary>
/// Stub for Oxide's DynamicConfigFile. ReadObject returns a cached instance so
/// LoadConfig() / SaveConfig() round-trip correctly within a single test run.
/// </summary>
public class DynamicConfigFile
{
    private object? _stored;

    public T ReadObject<T>() where T : new()
        => _stored is T obj ? obj : new T();

    public void WriteObject<T>(T obj) => _stored = obj;
}

/// <summary>
/// Base class that mirrors the Oxide RustPlugin surface used by SafeSpace.
/// Tests access <see cref="permission"/> directly to grant/revoke permissions.
/// </summary>
public abstract class RustPlugin : Oxide.Core.Plugin
{
    // Exposed as the stub type so tests can call .Grant() without casting.
    public Oxide.Core.Libraries.Permission permission { get; }
        = new Oxide.Core.Libraries.Permission();

    protected DynamicConfigFile Config { get; } = new DynamicConfigFile();

    protected virtual void LoadDefaultConfig() { }
    protected virtual void LoadConfig() { }
    protected virtual void SaveConfig() { }

    protected void Puts(string msg) { /* no-op in tests */ }
}

// ─── Oxide plugin attribute stubs ───────────────────────────────────────────

[AttributeUsage(AttributeTargets.Class)]
public sealed class InfoAttribute : Attribute
{
    public InfoAttribute(string name, string author, string version) { }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class DescriptionAttribute : Attribute
{
    public DescriptionAttribute(string description) { }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ChatCommandAttribute : Attribute
{
    public ChatCommandAttribute(string command) { }
}
