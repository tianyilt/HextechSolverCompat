using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyFlowLimitKinds = [MonsterHexKind.ShrinkEngine,
        MonsterHexKind.SingularityAI, MonsterHexKind.LivingFog];
    private static readonly LivingFogEnemyHex NativeLivingFog = new();
    private static readonly Func<HextechEnemyHexContext, CardPlay, Task> NativeLivingFogBefore = AccessTools.DeclaredMethod(typeof(LivingFogEnemyHex), "BeforeCardPlayed")
        .CreateDelegate<Func<HextechEnemyHexContext, CardPlay, Task>>(NativeLivingFog);
    private static readonly Func<HextechEnemyHexContext, CardModel, AutoPlayType, bool> NativeLivingFogMayPlay = AccessTools.DeclaredMethod(typeof(LivingFogEnemyHex), "ShouldPlay")
        .CreateDelegate<Func<HextechEnemyHexContext, CardModel, AutoPlayType, bool>>(NativeLivingFog);

    private static void RegisterNativeEnemyFlowLimits(Harmony harmony)
    {
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var getAmount = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPowerAmount" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(SlipperyPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShrinkEngineEnemyHex), "BeforePlayerSideTurnStart"),
            Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "GetAliveEnemies"), nameof(NativeContextAliveEnemies)),
            new(getAmount, AccessTools.Method(typeof(NativeRuneBridge), nameof(GetPowerAmount)).MakeGenericMethod(typeof(SlipperyPower)), 1),
            new(AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks)).Single(method => method.Name == "ApplyExact" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(SlipperyPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeExactEnemyDrawApply)).MakeGenericMethod(typeof(SlipperyPower)), 1));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShrinkEngineEnemyHex), "BeforeEnemySideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking), 2),
            Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "UpdateEnemyScale"), nameof(NativeEnemyBodyScale)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SingularityAIEnemyHex), "BeforePlayerSideTurnStart"),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(LivingFogEnemyHex), "BeforeCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(tracking, nameof(NativeCapturedTracking)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(LivingFogEnemyHex), "ShouldPlay"), Site(tracking, nameof(NativeCapturedTracking)));
        foreach (string name in new[] { "GetPlayerRuneProcsThisTurn", "TryConsumePlayerRuneProcThisTurn", "GetPlayerRuneProcKey" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatProcTracker), name));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyFlowLimitKinds);
    }
    internal static void DispatchNativeLivingFogBefore(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, CardPlay play)
    {
        if (state.Has(MonsterHexKind.LivingFog)) InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeLivingFogBefore(context, play));
    }
    private static bool NativeLivingFogShouldPlay(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, CardModel card, AutoPlayType auto)
    {
        if (!state.Has(MonsterHexKind.LivingFog)) return true;
        bool allowed = true;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => { allowed = NativeLivingFogMayPlay(context, card, auto); return Task.CompletedTask; });
        return allowed;
    }
}
