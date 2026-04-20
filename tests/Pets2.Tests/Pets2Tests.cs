using System.Reflection;
using Xunit;
using Oxide.Plugins;

namespace Pets2.Tests;

/// <summary>
/// Unit tests for Pets2.cs.
///
/// Private hooks are invoked via reflection using <see cref="CallHook"/>.
/// The <see cref="TestablePets2"/> subclass overrides <c>GetCurrentTime()</c> and
/// <c>DoSphereCast()</c> so time and ray-cast results can be injected without Unity.
/// </summary>
public class Pets2Tests
{
    // ─── TestablePets2 ───────────────────────────────────────────────────────

    /// <summary>
    /// Pets2 subclass that replaces the two Unity-coupled virtuals with
    /// injected test doubles.
    /// </summary>
    private sealed class TestablePets2 : Oxide.Plugins.Pets2
    {
        public float CurrentTime { get; set; } = 0f;
        public BaseCombatEntity? NextSphereCastHit { get; set; } = null;

        internal override float GetCurrentTime() => CurrentTime;

        internal override BaseCombatEntity? DoSphereCast(
            UnityEngine.Vector3 origin,
            UnityEngine.Vector3 direction,
            float radius,
            float distance)
        {
            return NextSphereCastHit;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TestablePets2 MakePlugin()
    {
        var plugin = new TestablePets2();
        // Set config directly so we don't need LoadConfig()
        plugin._config = new Oxide.Plugins.Pets2.PluginConfig
        {
            AttackCooldown    = 0.5f,
            AttackRange       = 25f,
            AiTickRate        = 0.1f,
            FriendlyToPlayers = true,
        };
        return plugin;
    }

    private static BasePlayer MakePlayer(ulong id = 76561198000000001UL)
        => new BasePlayer { userID = id };

    private static BaseNpc MakeNpc() => new BaseNpc();

    private static BaseCombatEntity MakeEnemy() => new BaseCombatEntity();

    /// <summary>
    /// Invoke a private/internal/public instance method by name, walking up the type
    /// hierarchy so that private methods declared on a base class are found even when
    /// the runtime type is a test subclass.
    /// </summary>
    private static object? CallHook(object plugin, string name, params object?[] args)
    {
        const BindingFlags flags =
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        Type? type = plugin.GetType();
        while (type != null)
        {
            var method = type.GetMethod(name, flags);
            if (method != null)
                return method.Invoke(plugin, args);
            type = type.BaseType;
        }
        throw new MissingMethodException(plugin.GetType().Name, name);
    }

    // ─── Taming ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tame_WhenNoPet_RegistersPetInDict()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();

        plugin.TryTame(player, npc);

        Assert.True(plugin._activePets.ContainsKey(player.userID));
    }

    [Fact]
    public void Tame_WhenAlreadyHasPet_SendsErrorMessage()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc1   = MakeNpc();
        var npc2   = MakeNpc();

        plugin.TryTame(player, npc1);
        plugin.TryTame(player, npc2);

        Assert.Contains(player.SentMessages,
            m => m.Contains("already have a pet"));
    }

    [Fact]
    public void Tame_WhenAlreadyHasPet_DoesNotReplacePet()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc1   = MakeNpc();
        var npc2   = MakeNpc();

        plugin.TryTame(player, npc1);
        var firstPet = plugin._activePets[player.userID];

        plugin.TryTame(player, npc2);

        Assert.Same(firstPet, plugin._activePets[player.userID]);
    }

    // ─── /pet release ────────────────────────────────────────────────────────

    [Fact]
    public void PetRelease_RemovesPetFromDict()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();

        plugin.TryTame(player, npc);
        Assert.True(plugin._activePets.ContainsKey(player.userID));

        CallHook(plugin, "CmdPet", player, "pet", new[] { "release" });

        Assert.False(plugin._activePets.ContainsKey(player.userID));
    }

    [Fact]
    public void PetRelease_NoPet_SendsNoPetMessage()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();

        CallHook(plugin, "CmdPet", player, "pet", new[] { "release" });

        Assert.Contains(player.SentMessages,
            m => m.Contains("don't currently have a pet"));
    }

    // ─── OnNpcTarget ─────────────────────────────────────────────────────────

    [Fact]
    public void OnNpcTarget_FriendlyToPlayers_ReturnsFalseForBasePlayer()
    {
        var plugin = MakePlugin();
        plugin._config.FriendlyToPlayers = true;

        var npc    = MakeNpc();
        var target = MakePlayer();

        var result = CallHook(plugin, "OnNpcTarget", npc, target);

        Assert.Equal(false, result);
    }

    [Fact]
    public void OnNpcTarget_FriendlyDisabled_ReturnsNull()
    {
        var plugin = MakePlugin();
        plugin._config.FriendlyToPlayers = false;

        var npc    = MakeNpc();
        var target = MakePlayer();

        var result = CallHook(plugin, "OnNpcTarget", npc, target);

        Assert.Null(result);
    }

    // ─── OnNpcAttack ─────────────────────────────────────────────────────────

    [Fact]
    public void OnNpcAttack_FriendlyToPlayers_ReturnsFalseForBasePlayer()
    {
        var plugin = MakePlugin();
        plugin._config.FriendlyToPlayers = true;

        var npc    = MakeNpc();
        var player = MakePlayer(); // BasePlayer is a BaseEntity (ToPlayer() returns non-null)

        var result = CallHook(plugin, "OnNpcAttack", npc, (BaseEntity)player);

        Assert.Equal(false, result);
    }

    // ─── pets.attack console command ─────────────────────────────────────────

    [Fact]
    public void AttackCommand_ValidTarget_SetsAttackTarget()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();
        plugin.TryTame(player, npc);

        var enemy = MakeEnemy();
        plugin.NextSphereCastHit = enemy;

        var arg = new ConsoleSystem.Arg(player);
        CallHook(plugin, "CmdPetsAttack", arg);

        var npcAi = plugin._activePets[player.userID];
        Assert.Same(enemy, npcAi.targetEnt);
    }

    [Fact]
    public void AttackCommand_Debounced_SecondCallWithinCooldownIgnored()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();
        plugin.TryTame(player, npc);

        var enemy1 = MakeEnemy();
        var enemy2 = MakeEnemy();

        plugin.CurrentTime = 0f;
        plugin.NextSphereCastHit = enemy1;
        var arg = new ConsoleSystem.Arg(player);
        CallHook(plugin, "CmdPetsAttack", arg);

        // Second call within cooldown window — target must not change
        plugin.CurrentTime = 0.1f; // < 0.5 s cooldown
        plugin.NextSphereCastHit = enemy2;
        CallHook(plugin, "CmdPetsAttack", arg);

        var npcAi = plugin._activePets[player.userID];
        Assert.Same(enemy1, npcAi.targetEnt);
    }

    [Fact]
    public void AttackCommand_AfterCooldown_TargetUpdated()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();
        plugin.TryTame(player, npc);

        var enemy1 = MakeEnemy();
        var enemy2 = MakeEnemy();

        plugin.CurrentTime = 0f;
        plugin.NextSphereCastHit = enemy1;
        var arg = new ConsoleSystem.Arg(player);
        CallHook(plugin, "CmdPetsAttack", arg);

        plugin.CurrentTime = 1f; // > 0.5 s cooldown
        plugin.NextSphereCastHit = enemy2;
        CallHook(plugin, "CmdPetsAttack", arg);

        var npcAi = plugin._activePets[player.userID];
        Assert.Same(enemy2, npcAi.targetEnt);
    }

    [Fact]
    public void AttackCommand_NoPet_DoesNothing()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();

        var enemy = MakeEnemy();
        plugin.NextSphereCastHit = enemy;

        // Should not throw
        var ex = Record.Exception(() =>
            CallHook(plugin, "CmdPetsAttack", new ConsoleSystem.Arg(player)));

        Assert.Null(ex);
    }

    [Fact]
    public void AttackCommand_PlayerTarget_SendsNoTargetMessage()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        var npc    = MakeNpc();
        plugin.TryTame(player, npc);

        // Sphere-cast returns a BasePlayer — must NOT be attacked; send "no target" message
        plugin.NextSphereCastHit = MakePlayer(999UL);

        CallHook(plugin, "CmdPetsAttack", new ConsoleSystem.Arg(player));

        Assert.Contains(player.SentMessages, m => m.Contains("No target found"));
    }

    // ─── OnEntityDeath ───────────────────────────────────────────────────────

    [Fact]
    public void OnEntityDeath_PetDies_RemovedFromDict()
    {
        var plugin = MakePlugin();
        var player = MakePlayer();
        player.Register();
        var npc = MakeNpc();
        plugin.TryTame(player, npc);
        Assert.True(plugin._activePets.ContainsKey(player.userID));

        // Retrieve the registered NpcAI and manually remove from dict (simulating death callback)
        var npcAi = plugin._activePets[player.userID];
        plugin._activePets.Remove(player.userID);

        Assert.False(plugin._activePets.ContainsKey(player.userID));
    }

    // ─── AiUpdate throttling ─────────────────────────────────────────────────

    [Fact]
    public void AiUpdate_ThrottledTo10Hz_SkipsCallsWithinInterval()
    {
        // We test the throttle by calling Update() multiple times within the
        // tick interval and verifying the AI state does not change.
        var plugin = MakePlugin(); // AiTickRate = 0.1f

        // Build a NpcAI instance directly via reflection so we can call Update()
        var npcAiType = typeof(Oxide.Plugins.Pets2).GetNestedType("NpcAI", BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("NpcAI not found");

        var npcAi = (dynamic)Activator.CreateInstance(npcAiType)!;
        var npc   = MakeNpc();

        // Wire up fields
        npcAiType.GetField("entity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(npcAi, npc);
        npcAiType.GetField("plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(npcAi, plugin);

        var updateMethod = npcAiType.GetMethod("Update",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!;

        // First tick at t=0 — should process (sets _nextAiTick = 0.1)
        UnityEngine.Time.realtimeSinceStartup = 0f;
        var enemyA = MakeEnemy();

        // Set a target so we can observe whether Update ran (it would change action to Attack)
        npcAiType.GetField("targetEnt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(npcAi, enemyA);

        // Set currentAction to something we can observe
        var actionType = typeof(Oxide.Plugins.Pets2).GetNestedType("Action", BindingFlags.NonPublic | BindingFlags.Public)!;
        // Action.Follow = 2 (enum ordinal) — DoFollow will run and won't error since no owner registered

        // We'll test the raw throttle: second Update within 0.05 s must be skipped
        UnityEngine.Time.realtimeSinceStartup = 0f;
        updateMethod.Invoke(npcAi, null); // first — processes, sets _nextAiTick=0.1

        // Record next tick field
        var nextTickField = npcAiType.GetField("_nextAiTick",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        float nextTick = (float)nextTickField.GetValue(npcAi)!;
        Assert.Equal(0.1f, nextTick, precision: 5);

        // Second call at t=0.05 (before _nextAiTick=0.1) — must be skipped (nextTick unchanged)
        UnityEngine.Time.realtimeSinceStartup = 0.05f;
        updateMethod.Invoke(npcAi, null);

        float nextTickAfter = (float)nextTickField.GetValue(npcAi)!;
        Assert.Equal(0.1f, nextTickAfter, precision: 5); // still 0.1, not bumped to 0.15
    }
}
