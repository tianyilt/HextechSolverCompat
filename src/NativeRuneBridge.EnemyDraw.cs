using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyDrawKinds = [MonsterHexKind.Nightstalking,
        MonsterHexKind.WarmogsSpirit, MonsterHexKind.SwiftAndSafe, MonsterHexKind.DizzySpinning,
        MonsterHexKind.SoulFysh, MonsterHexKind.EndlessRotation, MonsterHexKind.MindOverMatter];
    private sealed record NativeEnemyDrawHandler(MonsterHexKind Kind, Type Type,
        Func<HextechEnemyHexContext, PlayerChoiceContext, CardModel, bool, Task> Draw,
        Func<HextechEnemyHexContext, PlayerChoiceContext, Player, Task> Shuffle);
    private static NativeEnemyDrawHandler[] NativeEnemyDrawEffects = [];

    private static void RegisterNativeEnemyDraw(Harmony harmony)
    {
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var reviewed = effects.Where(effect => effect is NightstalkingEnemyHex or WarmogsSpiritEnemyHex
            or SwiftAndSafeEnemyHex or DizzySpinningEnemyHex or SoulFyshEnemyHex
            or EndlessRotationEnemyHex or MindOverMatterEnemyHex or AeonglassEnemyHex).ToArray();
        if (reviewed.Length != 8) throw new InvalidOperationException("Native enemy draw catalogue changed.");
        NativeEnemyDrawEffects = reviewed.Select(effect => new NativeEnemyDrawHandler(
            AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(), effect.GetType(),
            AccessTools.Method(effect.GetType(), "AfterCardDrawn")
                .CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CardModel, bool, Task>>(effect),
            AccessTools.Method(effect.GetType(), "AfterShuffle")
                .CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, Player, Task>>(effect))).ToArray();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawProgressEnemyHexBase), "AfterCardDrawn"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)));
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var exact = AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks))
            .Single(m => m.Name == "ApplyExact" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(SlipperyPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NightstalkingEnemyHex), "AfterLocalCardDrawn"),
            Site(tracking, nameof(NativeCapturedTracking)),
            new NativeCallSite(exact, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeExactEnemyDrawApply)).MakeGenericMethod(typeof(SlipperyPower)), 1));
        var scaled = AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks))
            .Single(m => m.Name == "Apply" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(PlatingPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(WarmogsSpiritEnemyHex), "AfterLocalCardDrawn"),
            Site(tracking, nameof(NativeCapturedTracking)),
            new NativeCallSite(scaled, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeScaledHealthApply)).MakeGenericMethod(typeof(PlatingPower)), 1));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(WarmogsSpiritEnemyHex), "GetCardsPerPlating"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SwiftAndSafeEnemyHex), "AfterLocalCardDrawn"),
            Site(tracking, nameof(NativeCapturedTracking), 2), SingleNativePowerSite<ArtifactPower>());
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SwiftAndSafeEnemyHex), "GetCardsPerArtifact"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DizzySpinningEnemyHex), "AfterShuffle"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechEnemyDrawProgress), "RecordDraw"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechEnemyDrawProgress), "RecordTotal"));
        RegisterNativeEnemyCardFlow(harmony);
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyDrawKinds);
    }

    private static Task<T?> NativeExactEnemyDrawApply<T>(Creature target, decimal amount, Creature? applier,
        CardModel? source, bool silent) where T : PowerModel
    {
        if (_simulator is null) return HextechEnemyPowerScalingHooks.ApplyExact<T>(target, amount, applier, source, silent);
        if (typeof(T) == typeof(SlipperyPower)) _simulator.SynchronizePowerAmountPredictionStates();
        decimal final = HextechEnemyPowerScalingHooks.ClampPowerOffsetForApply(ModelDb.Power<T>(), target, amount);
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        if (final == 0m) return Task.FromResult(combat.GetPower<T>(target));
        var effectiveApplier = HextechEnemyPowerScalingHooks.ShouldClearSelfApplier(target, applier) ? null : applier;
        return ApplyPowerOne<T>(target, final, effectiveApplier, source, silent);
    }

    internal static void DispatchNativeEnemyDraw(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, CardModel card, bool fromHandDraw)
        => InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        {
            foreach (var effect in NativeEnemyDrawEffects)
                if (state.Has(effect.Kind))
                    RequireCompleted(effect.Draw(context, new ThrowingPlayerChoiceContext(), card, fromHandDraw), effect.Type);
            return Task.CompletedTask;
        });

    internal static void DispatchNativeEnemyShuffle(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Player player)
        => InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        {
            foreach (var effect in NativeEnemyDrawEffects)
                if (state.Has(effect.Kind))
                    RequireCompleted(effect.Shuffle(context, new ThrowingPlayerChoiceContext(), player), effect.Type);
            return Task.CompletedTask;
        });
}
