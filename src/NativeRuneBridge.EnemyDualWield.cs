using CombatSolver;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private delegate bool NativeDualIntentFactory(AbstractIntent intent, Creature owner, out DualWieldAttackIntent? transformed);
    private static readonly NativeDualIntentFactory OriginalDualIntentFactory =
        AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "TryCreateDualWieldIntent").CreateDelegate<NativeDualIntentFactory>();
    private static void RegisterNativeEnemyDualWield(Harmony harmony)
    {
        // Pinned game audit: all 211 Monster DamageCmd.Attack sites use decimal
        // white damage; the only Hextech calculated site is the player BodySlam.
        // Preserve raw BaseDamage until dynamic monster damage is adjusted.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "CurrentMonsterMove"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(DuplicateOwnedDualHits)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "AdjustMonsterMoveDamage"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(HalveOwnedDualWhiteDamage)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(IntentForecaster), "GetAttackHits"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(BuildLiveDualForecast)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "TryCreateDualWieldIntent"));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.DualWield);
    }
    private static void DuplicateOwnedDualHits(SimulatedCombatState __instance, ref ForecastMove __result)
    {
        if (!PowerSourceBridge.HasCapturedHex(__instance, MonsterHexKind.DualWield)) return;
        __result = __result with { AttackHits = __result.AttackHits.SelectMany(hit => new[] { hit, hit }).ToArray() };
    }
    private static void HalveOwnedDualWhiteDamage(SimulatedCombatState __instance, Creature owner, ref int __result)
    {
        if (owner.Side == CombatSide.Enemy && PowerSourceBridge.HasCapturedHex(__instance, MonsterHexKind.DualWield) && __result >= 1)
            __result = (int)Math.Ceiling(__result / 2m);
    }
    private static void BuildLiveDualForecast(MonsterModel monster, MoveState move, CombatState state,
        ref IReadOnlyList<ForecastAttackHit> __result)
    {
        if (HextechMayhemModifier.FindIn(state.RunState) is not { } modifier || !modifier.HasActiveMonsterHex(MonsterHexKind.DualWield)) return;
        var hits = new List<ForecastAttackHit>();
        int offset = 0;
        foreach (var intent in move.Intents.OfType<AttackIntent>())
        {
            int repeats = Math.Max(1, intent.Repeats);
            if (!OriginalDualIntentFactory(intent, monster.Creature, out var transformed))
                throw new InvalidOperationException("Pinned native DualWield intent could not be projected.");
            int damage = transformed!.GetSingleDamage(state.PlayerCreatures, monster.Creature);
            for (int index = 0; index < repeats; index++)
            {
                var raw = __result[offset++];
                hits.Add(raw with { Damage = damage });
                hits.Add(raw with { Damage = damage });
            }
        }
        if (offset != __result.Count) throw new InvalidOperationException("Pinned native DualWield forecast layout changed.");
        __result = hits;
    }
}

