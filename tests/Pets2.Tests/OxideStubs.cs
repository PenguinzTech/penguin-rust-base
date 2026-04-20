// Minimal Oxide / Unity / Rust API stubs — just enough for Pets2.cs to compile and be unit-tested.
// These types mirror the real Oxide signatures so no changes are needed to the plugin source.

using System.Reflection;

// ─── Newtonsoft.Json stub ────────────────────────────────────────────────────

namespace Newtonsoft.Json
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class JsonPropertyAttribute : Attribute
    {
        public JsonPropertyAttribute(string propertyName) { }
    }
}

// ─── Oxide.Core ──────────────────────────────────────────────────────────────

namespace Oxide.Core
{
    public abstract class Plugin
    {
        public string Name    { get; protected set; } = "";
        public string Author  { get; protected set; } = "";
        public string Version { get; protected set; } = "";
        public string Title   { get; protected set; } = "";
    }

    public static class Interface
    {
        public static OxideInterface Oxide { get; } = new OxideInterface();
    }

    public class OxideInterface
    {
        public DataFileSystem DataFileSystem { get; } = new DataFileSystem();
    }

    public class DataFileSystem
    {
        private readonly Dictionary<string, object?> _store = new();

        public void WriteObject<T>(string name, T data) => _store[name] = data;

        public T? ReadObject<T>(string name) where T : class
            => _store.TryGetValue(name, out var val) ? val as T : null;
    }

    public static class GetMod
    {
        // Unused in Pets2 but kept to avoid "method not found" if referenced
    }
}

// ─── Oxide.Core.Libraries ────────────────────────────────────────────────────

namespace Oxide.Core.Libraries
{
    public class Permission
    {
        private readonly HashSet<string> _granted = new();

        public void RegisterPermission(string perm, Oxide.Core.Plugin owner) { }

        public bool UserHasPermission(string userId, string perm)
            => _granted.Contains(Key(userId, perm));

        public void Grant(string userId, string perm) => _granted.Add(Key(userId, perm));
        public void Revoke(string userId, string perm) => _granted.Remove(Key(userId, perm));

        private static string Key(string userId, string perm) => $"{userId}:{perm}";
    }

    public class Lang
    {
        public void RegisterMessages(Dictionary<string, string> messages, Oxide.Core.Plugin plugin) { }
        public string GetMessage(string key, Oxide.Core.Plugin plugin, string? userId = null) => key;
    }

    public class Timer
    {
        public bool Destroyed { get; private set; }
        private readonly Action _callback;
        public Timer(Action callback) => _callback = callback;
        public void Destroy() => Destroyed = true;
        public void Fire() => _callback();
    }

    public class TimerManager
    {
        private readonly List<Timer> _timers = new();

        public Timer Once(float delay, Action callback)
        {
            var t = new Timer(callback);
            _timers.Add(t);
            return t;
        }
    }
}

// ─── DynamicConfigFile ───────────────────────────────────────────────────────

public class DynamicConfigFile
{
    private object? _stored;

    public T ReadObject<T>() where T : new()
        => _stored is T obj ? obj : new T();

    public void WriteObject<T>(T obj, bool save = false) => _stored = obj;
}

// ─── UnityEngine stubs ───────────────────────────────────────────────────────

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);

        public static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public Quaternion() { x = 0; y = 0; z = 0; w = 1; }

        public static Vector3 operator *(Quaternion q, Vector3 v) => v; // Identity approximation for tests

        public static Quaternion Euler(Vector3 angles) => new Quaternion();
    }

    public struct RaycastHit
    {
        public float distance;
        public Transform? transform;
        public BaseEntity? _entity;

        public BaseEntity? GetEntity() => _entity;
    }

    public class Transform
    {
        public Vector3 position { get; set; }
    }

    public static class Time
    {
        public static float realtimeSinceStartup { get; set; } = 0f;
    }

    public abstract class MonoBehaviour
    {
        public Transform transform { get; } = new Transform();
        public bool enabled { get; set; } = true;

        private static readonly Dictionary<Type, Dictionary<object, MonoBehaviour>> _components = new();

        public T? GetComponent<T>() where T : class
        {
            var type = typeof(T);
            if (_components.TryGetValue(type, out var map))
            {
                if (map.TryGetValue(this, out var comp))
                    return comp as T;
            }
            return null;
        }

        public T AddComponent<T>() where T : MonoBehaviour, new()
        {
            var type = typeof(T);
            if (!_components.ContainsKey(type))
                _components[type] = new Dictionary<object, MonoBehaviour>();

            var comp = new T();
            // Copy the same transform so positional checks work
            _components[type][this] = comp;
            return comp;
        }

        // Stub: destroy does nothing in tests
        public static void Destroy(MonoBehaviour? obj) { }
    }

    public static class Object
    {
        public static void Destroy(MonoBehaviour? obj) { }

        public static T[]? FindObjectsOfType<T>() where T : MonoBehaviour => null;
        public static object[]? FindObjectsOfType(Type type) => null;
    }

    public static class Physics
    {
        // Default stub: always misses. Tests override DoSphereCast on TestablePets2.
        public static bool SphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hitInfo, float maxDistance = float.MaxValue)
        {
            hitInfo = default;
            return false;
        }
    }

    public static class Mathf
    {
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
    }

    public struct Color
    {
        public float r, g, b, a;
        public static Color red    => new Color { r = 1 };
        public static Color cyan   => new Color { b = 1 };
        public static Color yellow => new Color { r = 1, g = 1 };
    }
}

namespace UnityEngine.SceneManagement
{
    public class Scene { }
    public static class SceneManager
    {
        public static void MoveGameObjectToScene(object go, Scene scene) { }
    }
}

// ─── ConsoleSystem ───────────────────────────────────────────────────────────

namespace ConsoleSystem
{
    public class Arg
    {
        private readonly BasePlayer? _player;
        public Arg(BasePlayer? player = null) => _player = player;
        public BasePlayer? Player() => _player;
    }
}

// ─── Rust / Oxide game types ─────────────────────────────────────────────────

public static class StringPool
{
    public static string Get(uint id) => $"assets/prefab/{id}";
}

public class Spawnable : UnityEngine.MonoBehaviour { }

public class BaseEntity
{
    public UnityEngine.Transform transform { get; } = new UnityEngine.Transform();

    /// <summary>Prefab ID — used when saving/loading pet data.</summary>
    public uint prefabID { get; set; }

    public bool enableSaving { get; set; } = true;
    public bool IsDestroyed  { get; set; } = false;

    public string ShortPrefabName { get; set; } = "wolf";

    private readonly Dictionary<Type, object> _components = new();

    public T? GetComponent<T>() where T : class
    {
        return _components.TryGetValue(typeof(T), out var comp) ? comp as T : null;
    }

    public T AddComponent<T>() where T : new()
    {
        var comp = new T();
        _components[typeof(T)] = comp;
        return comp;
    }

    public void Spawn() { }

    /// <summary>Simulated game object — points back to this entity for component look-ups.</summary>
    public FakeGameObject gameObject { get; }

    public BaseEntity()
    {
        gameObject = new FakeGameObject(this);
    }

    public BasePlayer? ToPlayer() => this as BasePlayer;
}

/// <summary>
/// Fake Unity GameObject that delegates AddComponent / GetComponent to the parent entity.
/// This lets test code call <c>npc.gameObject.AddComponent&lt;NpcAI&gt;()</c> just like the plugin.
/// </summary>
public class FakeGameObject
{
    private readonly BaseEntity _owner;
    private readonly Dictionary<Type, object> _components = new();

    public FakeGameObject(BaseEntity owner) => _owner = owner;

    public T? GetComponent<T>() where T : class
    {
        return _components.TryGetValue(typeof(T), out var comp) ? comp as T : null;
    }

    public T AddComponent<T>() where T : class, new()
    {
        var comp = new T();
        _components[typeof(T)] = comp;
        // Also register it on the entity so GetComponent<T>() on the entity finds it
        _owner.AddComponent<T>();
        return comp;
    }

    public UnityEngine.Transform transform => _owner.transform;
}

public class BaseCombatEntity : BaseEntity
{
    public float health      { get; set; } = 100f;
}

public class AttackEntity : BaseEntity { }

public class BaseNpc : BaseCombatEntity
{
    public BaseCombatEntity? AttackTarget     { get; set; }
    public float             AttackRange      { get; set; } = 2f;
    public bool              IsStopped        { get; set; }
    public UnityEngine.Transform? ChaseTransform { get; set; }
    public float             TargetSpeed      { get; set; }
    public Behaviour         CurrentBehaviour { get; set; }

    public NpcStats   Stats       { get; } = new NpcStats();
    public NpcEnergy  Energy      { get; } = new NpcEnergy();
    public NpcEnergy  Hydration   { get; } = new NpcEnergy();
    public float      Sleep       { get; set; } = 1f;
    public float      AttackDamage { get; set; } = 10f;
    public BaseCombatEntity? FoodTarget { get; set; }

    public enum Behaviour { Idle, Wander, Attack, Eat, Sleep }
    public enum Facts     { PathToTargetStatus }

    public void StartAttack() { }
    public void UpdateDestination(UnityEngine.Vector3 pos) { }
    public void Eat() { }
    public void SetFact(Facts fact, int value, bool b1, bool b2) { }

    public float Health()    => health;
    public float MaxHealth() => 100f;
    public void  InitializeHealth(float hp, float maxHp) => health = hp;
}

public class NpcStats
{
    public float Speed     { get; set; } = 5f;
    public float TurnSpeed { get; set; } = 90f;
}

public class NpcEnergy
{
    public float Level { get; set; } = 1f;
}

public class BasePlayer : BaseCombatEntity
{
    public ulong  userID      { get; set; }
    public string UserIDString => userID.ToString();

    public List<string> SentMessages { get; } = new();
    public void ChatMessage(string msg) => SentMessages.Add(msg);

    public bool IsDead() => IsDestroyed;

    public PlayerEyes   eyes        { get; } = new PlayerEyes();
    public ServerInput  serverInput { get; } = new ServerInput();

    private static readonly Dictionary<ulong, BasePlayer> _registry = new();

    public static List<BasePlayer> activePlayerList { get; } = new();

    public static BasePlayer? FindByID(ulong id)
        => _registry.TryGetValue(id, out var p) ? p : null;

    /// <summary>Test helper — register a player so FindByID works.</summary>
    public void Register() => _registry[userID] = this;

    public enum PlayerFlags { ReceivingSnapshot, IsAdmin }

    private HashSet<PlayerFlags> _flags = new();
    public bool HasPlayerFlag(PlayerFlags flag) => _flags.Contains(flag);
    public void SetPlayerFlag(PlayerFlags flag, bool val)
    {
        if (val) _flags.Add(flag);
        else     _flags.Remove(flag);
    }
}

public class PlayerEyes
{
    public UnityEngine.Vector3 position { get; set; }
    public UnityEngine.Vector3 HeadForward() => UnityEngine.Vector3.forward;
    public UnityEngine.Vector3 BodyForward() => UnityEngine.Vector3.forward;
}

public class ServerInput
{
    public InputState current { get; } = new InputState();
}

public class InputState
{
    public UnityEngine.Vector3 aimAngles { get; set; }
}

public class HitInfo
{
    public BaseEntity? Initiator { get; set; }
}

// ─── Oxide RustPlugin base ───────────────────────────────────────────────────

public abstract class RustPlugin : Oxide.Core.Plugin
{
    public Oxide.Core.Libraries.Permission permission { get; } = new();
    public Oxide.Core.Libraries.Lang       lang       { get; } = new();
    public Oxide.Core.Libraries.TimerManager timer    { get; } = new();

    protected DynamicConfigFile Config { get; } = new DynamicConfigFile();

    protected virtual void LoadDefaultConfig() { }
    protected virtual void LoadConfig() { }
    protected virtual void SaveConfig()  { }

    public void Puts(string msg) { /* no-op */ }
}

// ─── Oxide attribute stubs ───────────────────────────────────────────────────

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

[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsoleCommandAttribute : Attribute
{
    public ConsoleCommandAttribute(string command) { }
}

// ─── Facepunch / GameManager / Rust stubs ────────────────────────────────────

/// <summary>
/// Fake Unity GameObject returned by Facepunch.Instantiate.GameObject in tests.
/// Provides just enough surface for InstantiateEntity() to compile.
/// </summary>
public class FacepunchGameObject
{
    public string name { get; set; } = "";
    public bool activeSelf { get; set; } = true;

    private readonly Dictionary<Type, object> _components = new();

    public T? GetComponent<T>() where T : class
        => _components.TryGetValue(typeof(T), out var c) ? c as T : null;

    public void SetActive(bool active) => activeSelf = active;
}

namespace Facepunch
{
    public static class Instantiate
    {
        public static FacepunchGameObject? GameObject(object? prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)
            => null; // Tests never exercise real spawning — SpawnPetForPlayer returns early on null
    }
}

public static class GameManager
{
    public static GameManagerServer server { get; } = new GameManagerServer();
}

public class GameManagerServer
{
    public object? FindPrefab(string name) => null;
}

namespace Rust
{
    public static class Server
    {
        public static UnityEngine.SceneManagement.Scene EntityScene { get; } = new UnityEngine.SceneManagement.Scene();
    }
}
