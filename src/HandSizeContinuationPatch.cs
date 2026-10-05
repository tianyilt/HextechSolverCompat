using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

// The SDK freezes this limit at root but omits it from continuation matching.
// Keep live queries at the live boundary; workers use only the frozen value.
internal static class HandSizeContinuationPatch
{
    private static MethodInfo _nativeQuery = null!;

    internal static void Install(Harmony harmony)
    {
        _nativeQuery = AccessTools.Method(AccessTools.TypeByName("STS2RitsuLib.RitsuLibFramework"), "GetMaxHandSize", [typeof(Player)]);
        harmony.Patch(AccessTools.Method(typeof(ContinuationStamp), nameof(ContinuationStamp.CaptureLive)),
            postfix: new HarmonyMethod(typeof(HandSizeContinuationPatch), nameof(AppendLiveLimit)));
        harmony.Patch(AccessTools.Method(typeof(ContinuationStamp), nameof(ContinuationStamp.CapturePredicted)),
            postfix: new HarmonyMethod(typeof(HandSizeContinuationPatch), nameof(AppendPredictedLimit)));
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.AppendFingerprint)),
            postfix: new HarmonyMethod(typeof(HandSizeContinuationPatch), nameof(AppendLimitFingerprint)));
    }

    private static void AppendLiveLimit(CombatState state, ref ContinuationStamp __result)
    {
        Player player = LocalContext.GetMe(state) ?? throw new PredictionUnsupportedException("Missing local player for hand-size continuation.");
        int limit = (int)_nativeQuery.Invoke(null, [player])!;
        __result = new ContinuationStamp(__result.StateText + ";max_hand_size=" + limit);
    }

    private static void AppendPredictedLimit(Player player, CombatPredictionSimulator simulator, ref ContinuationStamp __result)
        => __result = new ContinuationStamp(__result.StateText + ";max_hand_size=" + simulator.GetMaxHandSize(player));

    private static void AppendLimitFingerprint(SimulatedCombatState __instance, ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add("max_hand_sizes");
        foreach (var player in __instance.Players.OrderBy(player => player.NetId))
        {
            fingerprint.Add(player.NetId);
            fingerprint.Add(__instance.GetMaxHandSize(player));
        }
    }
}
