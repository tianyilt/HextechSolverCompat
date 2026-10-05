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
    private static readonly MonsterHexKind[] NativeHealthThresholdKinds =
        [MonsterHexKind.EscapePlan, MonsterHexKind.Repulsor, MonsterHexKind.DawnbringersResolve, MonsterHexKind.FeelTheBurn, MonsterHexKind.MikaelsBlessing];
    private sealed record NativeThresholdHandler(MonsterHexKind Kind, Type Type,
        Func<HextechEnemyHexContext, Creature, uint, Task> Callback);
    private static NativeThresholdHandler[] NativeThresholdEffects = [];

    private static void RegisterNativeEnemyHealthThresholds(Harmony harmony)
    {
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var reviewed = effects.Where(effect => effect is EscapePlanEnemyHex or RepulsorEnemyHex
            or DawnbringersResolveEnemyHex or FeelTheBurnEnemyHex or MikaelsBlessingEnemyHex).ToArray();
        if (reviewed.Length != 5 || reviewed.Any(effect => AccessTools.GetDeclaredFields(effect.GetType()).Any(field => !field.IsStatic)))
            throw new InvalidOperationException("Native health threshold catalogue changed.");
        NativeThresholdEffects = reviewed.Select(effect => new NativeThresholdHandler(
            AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(), effect.GetType(),
            AccessTools.DeclaredMethod(effect.GetType(), "AfterEnemyHealthThreshold")
                .CreateDelegate<Func<HextechEnemyHexContext, Creature, uint, Task>>(effect))).ToArray();
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var hp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
        var maxHp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EscapePlanEnemyHex), "BeforePlayerSideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(hp, nameof(NativeEnemyCurrentHp)),
            Site(maxHp, nameof(NativeEnemyMaxHp)),
            Site(AccessTools.Method(typeof(HextechEnemyHexContext), "GetAliveEnemies"), nameof(NativeContextAliveEnemies)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(EscapePlanEnemyHex), "AfterEnemyHealthThreshold"));
        PatchEventCallback(harmony, AccessTools.Method(typeof(EscapePlanEnemyHex), "ApplyEscapePlan"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(maxHp, nameof(NativeEnemyMaxHp)),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)), SingleNativePowerSite<ShrinkPower>());
        var scaledApply = AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks)).Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition);
        NativeCallSite Scaled<T>() where T : PowerModel => new(scaledApply.MakeGenericMethod(typeof(T)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeScaledHealthApply)).MakeGenericMethod(typeof(T)), 1);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RepulsorEnemyHex), "AfterEnemyHealthThreshold"),
            Site(tracking, nameof(NativeCapturedTracking)), Scaled<SlipperyPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DawnbringersResolveEnemyHex), "AfterEnemyHealthThreshold"),
            Site(tracking, nameof(NativeCapturedTracking)), Scaled<RegenPower>(),
            Site(AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(Creature), typeof(decimal)]), nameof(NativeEnemyHpFraction)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeelTheBurnEnemyHex), "AfterEnemyHealthThreshold"),
            Site(tracking, nameof(NativeCapturedTracking), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeelTheBurnEnemyHex), "BeforeEnemySideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking), 3),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            ManyNativePowerSite<WeakPower>(), ManyNativePowerSite<VulnerablePower>(), ManyNativePowerSite<HextechBurnPower>());
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechMayhemModifier), "IsBelowEnemyHealthThreshold"),
            Site(hp, nameof(NativeEnemyCurrentHp)), Site(maxHp, nameof(NativeEnemyMaxHp)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechEnemyTriggerGuard), "ShouldSuppressDuplicateEnemyThresholdTrigger"),
            Site(hp, nameof(NativeEnemyCurrentHp)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechStableRandom), "CardActionKey"));
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechStableRandom), "GetPileKey"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        // Retain the original scaling and offset clamp; only branch-dependent
        // creature/power reads are redirected. No multiplayer claim is made.
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechEnemyPowerScalingHooks), "GetPlayerCount"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2));
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechEnemyPowerScalingHooks), "ClampPowerOffsetForApply",
                [typeof(PowerModel), typeof(Creature), typeof(decimal)]),
            Site(AccessTools.Method(typeof(Creature), nameof(Creature.GetPower), [typeof(ModelId)]), nameof(NativeBranchPowerById)));
        foreach (string name in new[] { "GetScalingOverride", "CalculateFinalAmount", "MultiplyByPlayerCount", "ClampPowerAmount", "IsInstancedPower", "ShouldClearSelfApplier" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechEnemyPowerScalingHooks), name));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechEnemyHexContext), "ClampScalingPlayerCount"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MikaelsBlessingEnemyHex), "AfterEnemyHealthThreshold"),
            Site(tracking, nameof(NativeCapturedTracking), 3),
            Site(AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(Creature), typeof(decimal)]), nameof(NativeEnemyHpFraction)),
            Site(NativeHeal, nameof(HealNative)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Powers)), nameof(NativeRemovalPowers)),
            Site(AccessTools.Method(typeof(HextechMikaelsBlessingVfx), "Play"), nameof(NativeCleanseVfx)),
            Site(AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]), nameof(RemoveNativePower)));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeHealthThresholdKinds);
    }

    private static PowerModel? NativeBranchPowerById(Creature creature, ModelId id)
        => _simulator is { } sim
            ? ((SimulatedCombatState)sim.State.CombatState).EffectivePowers()
                .FirstOrDefault(power => ReferenceEquals(power.Owner, creature) && power.Id == id)
            : creature.GetPower(id);

    private static Task<T?> NativeScaledHealthApply<T>(Creature target, decimal amount, Creature? applier, CardModel? source, bool silent) where T : PowerModel
    {
        if (_simulator is null) return HextechEnemyPowerScalingHooks.Apply<T>(target, amount, applier, source, silent);
        if (typeof(T) == typeof(SlipperyPower)) _simulator.SynchronizePowerAmountPredictionStates();
        var scaling = HextechEnemyPowerScalingHooks.GetScalingOverride(typeof(T));
        if (scaling is null) return ApplyPowerOne<T>(target, amount, applier, source, silent);
        decimal final = HextechEnemyPowerScalingHooks.CalculateFinalAmount(target, amount, applier, scaling.Value);
        final = HextechEnemyPowerScalingHooks.ClampPowerOffsetForApply(ModelDb.Power<T>(), target, final);
        if (final == 0m) return Task.FromResult(((SimulatedCombatState)_simulator.State.CombatState).GetPower<T>(target));
        var effectiveApplier = HextechEnemyPowerScalingHooks.ShouldClearSelfApplier(target, applier) ? null : applier;
        return ApplyPowerOne<T>(target, final, effectiveApplier, source, silent);
    }

    internal static void DispatchNativeEnemyHealthThresholds(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? source)
    {
        // The native guard records every unblocked enemy hit, even when no
        // threshold effect is selected. Preserve that field for all captured
        // periodic/debuff tracking states as well.
        if (state.NativeTurns is null || target.CombatId is not { } id) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        {
            var tracking = state.NativeTurns?.Model ?? throw new InvalidOperationException("Native health threshold state was not captured.");
            if (HextechEnemyTriggerGuard.ShouldSuppressDuplicateEnemyThresholdTrigger(tracking, target, result, dealer, source)
                || !HextechMayhemModifier.IsBelowEnemyHealthThreshold(target)) return Task.CompletedTask;
            foreach (var effect in NativeThresholdEffects)
            {
                if (!state.Has(effect.Kind)) continue;
                RequireCompleted(effect.Callback(context, target, id), effect.Type);
                if (simulator.HasPendingChoice) break;
            }
            return Task.CompletedTask;
        });
    }
}
