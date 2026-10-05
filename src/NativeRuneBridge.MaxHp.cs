using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private delegate bool NativeMaxHpDelta(Creature creature, ref decimal amount, int sign, out bool entered);
    private static readonly NativeMaxHpDelta OriginalNativeMaxHpDelta =
        AccessTools.Method(typeof(HextechCombatHooks), "ScaleMaxHpDelta").CreateDelegate<NativeMaxHpDelta>();
    private static readonly HextechScopedDepthGuard NativeMaxHpGuard =
        (HextechScopedDepthGuard)AccessTools.Field(typeof(HextechCombatHooks), "GoliathMaxHpGuard").GetValue(null)!;

    private static void RegisterNativeMaxHp(Harmony harmony)
    {
        // Existing Goliath/GoldenSpatula states already carry their original
        // base HP. TankEngine previously needed only its native room award.
        RegisterState<TankEngineRune>();
        foreach (var name in new[] { "GetPrimary", "GetScale" })
            PatchEventCallback(harmony, AccessTools.Method(typeof(HextechMaxHpScaling), name),
                Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Relics)), nameof(NativeMaxHpRelics)));
        foreach (var name in new[] { "ScaleMaxHpDelta" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatHooks), name));
        foreach (var name in new[] { "EnsureBaseInitialized", "GetScaledMaxHp", "CombineScales" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechMaxHpScaling), name));
        foreach (var name in new[] { "Enter", "Exit", "get_IsActive" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechScopedDepthGuard), name));
    }

    private static IReadOnlyList<RelicModel> NativeMaxHpRelics(Player player)
        => _simulator is not { } sim ? player.Relics
            : ((SimulatedCombatState)sim.State.CombatState).RelicsOf(player).Select(relic =>
                relic is IHextechMaxHpBaseHolder or IHextechPercentHpForge
                    ? (RelicModel)ModelPredictionStateMirrors.Get<NativeRuneState>(sim, relic).Model : relic).ToArray();

    private static Task GainNativeMaxHp(Creature creature, decimal amount)
    {
        if (_simulator is not { } sim) return CreatureCmd.GainMaxHp(creature, amount);
        bool entered = false;
        try
        {
            // Execute the original base-HP/scaling/rounding/zero-delta rule.
            if (!OriginalNativeMaxHpDelta(creature, ref amount, 1, out entered)) return Task.CompletedTask;
            if (amount < 0m) throw new ArgumentException("amount must be non-negative.");
            var state = sim.State.GetCreature(creature);
            int previous = state.MaxHp;
            state.SetMaxHp(Math.Max(1, (int)(previous + amount)));
            sim.Heal(creature, state.MaxHp - previous);
            PauseNativeChoice(sim);
            return Task.CompletedTask;
        }
        finally { if (entered) NativeMaxHpGuard.Exit(); }
    }

    private static void GrantNativeFeedMaxHp(SimCreatureState owner, int newMaxHp, CombatPredictionSimulator simulator)
    {
        var previous = _simulator; _simulator = simulator;
        try { RequireCompleted(GainNativeMaxHp(owner.Creature, newMaxHp - owner.MaxHp), typeof(CreatureCmd)); }
        finally { _simulator = previous; }
    }
    private static void AlreadyHealedNativeFeed(CombatPredictionSimulator simulator, Creature creature, decimal amount) { }
}
