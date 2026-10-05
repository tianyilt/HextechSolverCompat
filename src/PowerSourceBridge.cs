using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

// Capture both the source-presence predicate and the exact branch card. Unknown
// pre-capture metadata remains unknown; it cannot be used by identity callbacks.
internal static class PowerSourceBridge
{
    private static readonly ConditionalWeakTable<SimulatedCombatState, EnemyState> States = new();
    private static readonly PropertyInfo CurrentSource = AccessTools.Property(typeof(SimulatedCombatState), "CurrentPowerCardSource");
    private static readonly FieldInfo PendingChanges = AccessTools.Field(typeof(SimulatedCombatState), "_pendingPowerAmountChanges");
    private static int PendingCount(SimulatedCombatState combat)
        => (PendingChanges.GetValue(combat) as System.Collections.ICollection)?.Count ?? 0;

    internal static bool HasCapturedHex(SimulatedCombatState combat, MonsterHexKind kind)
        => States.TryGetValue(combat, out var state) && state.Has(kind);
    internal static EnemyState? CapturedEnemyState(SimulatedCombatState combat)
        => States.TryGetValue(combat, out var state) ? state : null;

    internal static void Bind(SimulatedCombatState combat, EnemyState state)
    {
        States.Add(combat, state);
        // Normally empty at materialization. Never invent a source for changes
        // queued before the adapter state was captured.
        for (int i = 0; i < PendingCount(combat); i++)
        {
            state.PendingPowerCardSources.Add(null);
            state.PendingPowerCards.Add(null);
        }
    }

    internal static void Register(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.RecordPowerAmountChange)),
            postfix: new HarmonyMethod(typeof(PowerSourceBridge), nameof(Record)));
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.DrainPowerAmountChanges)),
            postfix: new HarmonyMethod(typeof(PowerSourceBridge), nameof(ClearDrained)));
        // This solver applies powers directly and does not dispatch the native
        // received-amount hook. The pinned native listener order visits powers
        // before modifiers: Artifact must consume the negative request first.
        // With no Artifact, zero it before temporary-stat callbacks, including
        // the internal Strength application of a temporary power.
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.ModifyPowerAmountForRelics)),
            postfix: new HarmonyMethod(typeof(PowerSourceBridge), nameof(ModifyReceivedAmount)));
    }

    private static void ModifyReceivedAmount(SimulatedCombatState __instance, PowerModel power, Creature target, ref int __result)
    {
        if (__result < 0 && power is StrengthPower && target.Side == CombatSide.Enemy
            && States.TryGetValue(__instance, out var state) && state.Has(MonsterHexKind.ReforgedHelmet)
            && __instance.Creatures.Contains(target) && __instance.GetAmount<ArtifactPower>(target) <= 0)
            __result = 0;
    }

    private static void Record(SimulatedCombatState __instance, PowerModel power, int delta)
    {
        if (delta != 0 && States.TryGetValue(__instance, out var state))
        {
            var source = (CardModel?)CurrentSource.GetValue(__instance);
            state.PendingPowerCardSources.Add(source is not null);
            state.PendingPowerCards.Add(NativeRuneBridge.CapturePowerSource(__instance, source));
            if (state.PendingPowerCardSources.Count != PendingCount(__instance)
                || state.PendingPowerCards.Count != state.PendingPowerCardSources.Count)
                throw new PredictionUnsupportedException("Hextech power source queue lost alignment.");
        }
        NativeRuneBridge.AfterNativePowerReceived(__instance, power);
    }

    internal static bool?[] CapturePending(SimulatedCombatState combat)
    {
        if (!States.TryGetValue(combat, out var state)) return new bool?[PendingCount(combat)];
        if (state.PendingPowerCardSources.Count != PendingCount(combat))
            throw new PredictionUnsupportedException("Hextech power source queue lost alignment before drain.");
        return state.PendingPowerCardSources.ToArray();
    }

    internal static PredictedCard?[] CapturePendingCards(SimulatedCombatState combat)
    {
        if (!States.TryGetValue(combat, out var state)) return new PredictedCard?[PendingCount(combat)];
        if (state.PendingPowerCards.Count != PendingCount(combat))
            throw new PredictionUnsupportedException("Hextech power source identities lost alignment before drain.");
        return state.PendingPowerCards.ToArray();
    }

    private static void ClearDrained(SimulatedCombatState __instance)
    {
        if (States.TryGetValue(__instance, out var state))
        {
            state.PendingPowerCardSources.Clear();
            state.PendingPowerCards.Clear();
        }
    }
}
