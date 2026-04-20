using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Oxide.Plugins;

namespace MorningFog.Tests;

/// <summary>
/// Unit tests for MorningFog.cs.
///
/// GetCurrentHour() and SetFogDensity() are overridden in TestableMorningFog so tests
/// can drive time and observe fog values without the game runtime. Hooks are private
/// Oxide convention so they are invoked via reflection using <see cref="CallHook"/>.
/// </summary>
public class MorningFogTests
{
    // ─── Test harness ────────────────────────────────────────────────────────

    private class TestableMorningFog : Oxide.Plugins.MorningFog
    {
        public float         InjectedHour       { get; set; } = 8.0f;
        public float         InjectedRandomFog  { get; set; } = 0.07f;
        public List<float>   FogValues          { get; }      = new();

        protected override float GetCurrentHour()                    => InjectedHour;
        protected override float GetRandomDensity(float maxDensity)  => InjectedRandomFog;
        protected override void  SetFogDensity(float density)        => FogValues.Add(density);
    }

    private static TestableMorningFog MakePlugin(
        float windowStart   = 5.0f,
        float windowEnd     = 10.0f,
        float peakHour      = 8.0f,
        float maxDensity    = 1.0f,
        float sigma         = 1.0f,
        int   checkInterval = 30)
    {
        var plugin = new TestableMorningFog();

        var configField = typeof(Oxide.Plugins.MorningFog)
            .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var configType = configField.FieldType;
        var config     = Activator.CreateInstance(configType)!;
        SetField(config, "WindowStart",   windowStart);
        SetField(config, "WindowEnd",     windowEnd);
        SetField(config, "PeakHour",      peakHour);
        SetField(config, "MaxDensity",    maxDensity);
        SetField(config, "Sigma",         sigma);
        SetField(config, "CheckInterval", checkInterval);
        configField.SetValue(plugin, config);

        return plugin;
    }

    private static void SetField(object target, string name, object value)
    {
        var f = target.GetType().GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(target, value);
    }

    private static void CallHook(Oxide.Plugins.MorningFog plugin, string name)
    {
        var method = typeof(Oxide.Plugins.MorningFog)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(Oxide.Plugins.MorningFog), name);
        method.Invoke(plugin, null);
    }

    // ─── Bell curve: intensity at key hours ──────────────────────────────────

    [Fact]
    public void CheckAndApplyFog_AtPeak_SetsMaxDensity()
    {
        var p = MakePlugin();
        p.InjectedHour = 8.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(1.0f, p.FogValues[0], precision: 4);
    }

    [Fact]
    public void CheckAndApplyFog_BeforeWindow_SetsZero()
    {
        var p = MakePlugin();
        p.InjectedHour = 4.99f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.0f, p.FogValues[0]);
    }

    [Fact]
    public void CheckAndApplyFog_AfterWindow_SetsZero()
    {
        var p = MakePlugin();
        p.InjectedHour = 10.01f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.0f, p.FogValues[0]);
    }

    [Fact]
    public void CheckAndApplyFog_AtWindowEdgeStart_IsLow()
    {
        var p = MakePlugin();
        p.InjectedHour = 5.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.True(p.FogValues[0] < 0.2f,
            $"Expected fog at 05:00 to be < 0.2, got {p.FogValues[0]:F4}");
    }

    [Fact]
    public void CheckAndApplyFog_AtWindowEdgeEnd_IsLow()
    {
        var p = MakePlugin();
        p.InjectedHour = 10.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.True(p.FogValues[0] < 0.2f,
            $"Expected fog at 10:00 to be < 0.2, got {p.FogValues[0]:F4}");
    }

    [Fact]
    public void CheckAndApplyFog_At7h_LessThanAt8h()
    {
        var p = MakePlugin();
        p.InjectedHour = 7.0f;
        CallHook(p, "CheckAndApplyFog");
        float at7 = p.FogValues[0];

        p.FogValues.Clear();
        p.InjectedHour = 8.0f;
        CallHook(p, "CheckAndApplyFog");
        float at8 = p.FogValues[0];

        Assert.True(at7 < at8, $"Expected fog at 07:00 ({at7:F4}) < fog at 08:00 ({at8:F4})");
    }

    [Fact]
    public void CheckAndApplyFog_SymmetricAroundPeak()
    {
        var p = MakePlugin();
        p.InjectedHour = 7.0f;  // peak − 1 hour
        CallHook(p, "CheckAndApplyFog");
        float before = p.FogValues[0];

        p.FogValues.Clear();
        p.InjectedHour = 9.0f;  // peak + 1 hour
        CallHook(p, "CheckAndApplyFog");
        float after = p.FogValues[0];

        Assert.Equal(before, after, precision: 4);
    }

    [Fact]
    public void CheckAndApplyFog_MaxDensityHalved_PeakIsHalf()
    {
        var p = MakePlugin(maxDensity: 0.5f);
        p.InjectedHour = 8.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.5f, p.FogValues[0], precision: 4);
    }

    [Fact]
    public void CheckAndApplyFog_CustomPeakHour_PeaksAtConfiguredHour()
    {
        var p = MakePlugin(peakHour: 7.0f, windowStart: 4.0f, windowEnd: 10.0f);
        p.InjectedHour = 7.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(1.0f, p.FogValues[0], precision: 4);
    }

    // ─── Random fog hour ─────────────────────────────────────────────────────

    [Fact]
    public void CheckAndApplyFog_AtRandomFogHour_UsesInjectedRandom()
    {
        var p = MakePlugin();
        p.InjectedHour      = 11.0f;
        p.InjectedRandomFog = 0.09f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.09f, p.FogValues[0]);
    }

    [Fact]
    public void CheckAndApplyFog_AtRandomFogHourEnd_IsIncluded()
    {
        var p = MakePlugin();
        p.InjectedHour      = 11.99f;
        p.InjectedRandomFog = 0.05f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.05f, p.FogValues[0]);
    }

    [Fact]
    public void CheckAndApplyFog_AfterRandomFogHour_SetsZero()
    {
        var p = MakePlugin();
        p.InjectedHour = 12.0f;
        CallHook(p, "CheckAndApplyFog");
        Assert.Equal(0.0f, p.FogValues[0]);
    }

    [Fact]
    public void CheckAndApplyFog_RandomFogHour_NeverExceedsMax()
    {
        var p = MakePlugin();
        p.InjectedHour      = 11.0f;
        p.InjectedRandomFog = 0.13f;
        CallHook(p, "CheckAndApplyFog");
        Assert.True(p.FogValues[0] <= 0.13f);
    }

    // ─── Unload ──────────────────────────────────────────────────────────────

    [Fact]
    public void Unload_ClearsFogToZero()
    {
        var p = MakePlugin();
        p.InjectedHour = 8.0f;
        CallHook(p, "CheckAndApplyFog");
        p.FogValues.Clear();

        CallHook(p, "Unload");

        Assert.Single(p.FogValues);
        Assert.Equal(0.0f, p.FogValues[0]);
    }

    [Fact]
    public void Unload_WithoutPriorFog_DoesNotThrow()
    {
        var p  = MakePlugin();
        var ex = Record.Exception(() => CallHook(p, "Unload"));
        Assert.Null(ex);
    }
}
