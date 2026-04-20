// Minimal Oxide/game API stubs — just enough for MorningFog.cs to compile and be tested.
// These types mirror the real Oxide/Rust signatures so no changes are needed to the plugin source.

namespace Oxide.Core
{
    public abstract class Plugin
    {
        public string Name    { get; protected set; } = "";
        public string Author  { get; protected set; } = "";
        public string Version { get; protected set; } = "";
    }
}

// ─── Oxide plugin infrastructure ────────────────────────────────────────────

/// <summary>Stub for Oxide's DynamicConfigFile.</summary>
public class DynamicConfigFile
{
    private object? _stored;
    public T ReadObject<T>() where T : new() => _stored is T obj ? obj : new T();
    public void WriteObject<T>(T obj) => _stored = obj;
}

/// <summary>Stub return value for timer.Once() calls.</summary>
public class Timer { }

/// <summary>Stub for Oxide's timer manager (the `timer` property on RustPlugin).</summary>
public class TimerManager
{
    public Timer Once(float delay, System.Action callback) => new Timer();
    public void Destroy(ref Timer? t) { t = null; }
}

/// <summary>Base class mirroring the Oxide RustPlugin surface used by MorningFog.</summary>
public abstract class RustPlugin : Oxide.Core.Plugin
{
    protected DynamicConfigFile Config { get; } = new DynamicConfigFile();
    public    TimerManager      timer  { get; } = new TimerManager();

    protected virtual void LoadDefaultConfig() { }
    protected virtual void LoadConfig() { }
    protected virtual void SaveConfig() { }
    protected void Puts(string msg) { }
}

// ─── Oxide plugin attribute stubs ───────────────────────────────────────────

[System.AttributeUsage(System.AttributeTargets.Class)]
public sealed class InfoAttribute : System.Attribute
{
    public InfoAttribute(string name, string author, string version) { }
}

[System.AttributeUsage(System.AttributeTargets.Class)]
public sealed class DescriptionAttribute : System.Attribute
{
    public DescriptionAttribute(string description) { }
}

// ─── Game runtime types ──────────────────────────────────────────────────────

/// <summary>
/// Stub for Rust's time-of-day system.
/// The plugin reads TOD_Sky.Instance.Cycle.Hour but overrides GetCurrentHour() in tests,
/// so this stub only needs to compile — it is never called during test execution.
/// </summary>
public class TOD_CycleParameters
{
    public float Hour { get; set; }
}

public class TOD_Sky
{
    public static TOD_Sky Instance { get; } = new TOD_Sky();
    public TOD_CycleParameters Cycle { get; } = new TOD_CycleParameters();
}

// ─── ConsoleSystem stub ──────────────────────────────────────────────────────

/// <summary>
/// Stub for Oxide's ConsoleSystem.
/// The plugin calls ConsoleSystem.Run(...) inside SetFogDensity(), which is overridden
/// in tests, so this stub only needs to compile — it is never called during test execution.
/// </summary>
public class ConsoleOption
{
    public ConsoleOption Quiet() => this;
}

public static class ConsoleSystem
{
    public static class Option
    {
        public static ConsoleOption Server { get; } = new ConsoleOption();
    }
    public static void Run(ConsoleOption opt, string cmd, params object[] args) { }
}
