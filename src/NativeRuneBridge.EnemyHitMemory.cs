using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyHitMemoryKinds =
        [MonsterHexKind.MountainSoul, MonsterHexKind.SpeedDemon, MonsterHexKind.DevilsDance,
         MonsterHexKind.Queen, MonsterHexKind.HandOfBaron];
    private sealed record NativeEnemyPlayerHitHandler(MonsterHexKind Kind, Type Type,
        Func<HextechEnemyHexContext, Creature, Creature, Task> Callback);
    private static NativeEnemyPlayerHitHandler[] NativeEnemyPlayerHits = [];
    private static Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task> NativeMountainHit = null!;
    private static Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal> NativeBaronMultiplier = null!;

    private static void RegisterNativeEnemyHitMemory(Harmony harmony)
    {
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var hitEffects = effects.Where(effect => effect is SpeedDemonEnemyHex or DevilsDanceEnemyHex or CantTouchThisEnemyHex or FinalFormEnemyHex or FeyMagicEnemyHex).ToArray();
        if (hitEffects.Length != 5 || hitEffects.Any(effect => AccessTools.GetDeclaredFields(effect.GetType()).Any(field => !field.IsStatic)))
            throw new InvalidOperationException("Original player-hit enemy catalogue changed.");
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        var fraction = AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(Creature), typeof(decimal)]);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MountainSoulEnemyHex), "AfterEnemyDamageReceived"),
            Site(tracking, nameof(NativeCapturedTracking)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MountainSoulEnemyHex), "BeforePlayerSideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking), 4),
            Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "GetAliveEnemies"), nameof(NativeContextAliveEnemies)),
            Site(fraction, nameof(NativeEnemyHpFraction)), Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SpeedDemonEnemyHex), "AfterEnemyDamageGivenPlayerHit"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(alive, nameof(NativeBranchIsAlive)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SpeedDemonEnemyHex), "BeforePlayerSideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking), 2), Site(alive, nameof(NativeBranchIsAlive)),
            Site(fraction, nameof(NativeEnemyHpFraction)), Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DevilsDanceEnemyHex), "AfterEnemyDamageGivenPlayerHit"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(alive, nameof(NativeBranchIsAlive)),
            Site(fraction, nameof(NativeEnemyHpFraction)), Site(NativeHeal, nameof(HealNative)));
        var scaledBuffer = AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(BufferPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CantTouchThisEnemyHex), "AfterEnemyDamageGivenPlayerHit"),
            Site(alive, nameof(NativeBranchIsAlive)),
            new(scaledBuffer, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeScaledHealthApply)).MakeGenericMethod(typeof(BufferPower)), 1));
        var chains = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == nameof(Creature.GetPowerAmount)
            && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(ChainsOfBindingPower));
        PatchEventCallback(harmony, NativeLambda(typeof(QueenEnemyHex), "BeforeEnemySideTurnStart"),
            new NativeCallSite(chains, AccessTools.Method(typeof(NativeRuneBridge), nameof(GetPowerAmount)).MakeGenericMethod(typeof(ChainsOfBindingPower)), 1));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(QueenEnemyHex), "BeforeEnemySideTurnStart"),
            ManyNativePowerSite<ChainsOfBindingPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HandOfBaronEnemyHex), "BeforePlayerSideTurnStart"),
            ManyNativePowerSite<ShrinkPower>());
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(QueenEnemyHex), "ApplyCombatStartPlayerDebuffs"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HandOfBaronEnemyHex), "ModifyDamageMultiplicative"));
        NativeEnemyPlayerHits = hitEffects.Select(effect => new NativeEnemyPlayerHitHandler(
            AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(), effect.GetType(),
            AccessTools.DeclaredMethod(effect.GetType(), "AfterEnemyDamageGivenPlayerHit")
                .CreateDelegate<Func<HextechEnemyHexContext, Creature, Creature, Task>>(effect))).ToArray();
        NativeMountainHit = AccessTools.DeclaredMethod(typeof(MountainSoulEnemyHex), "AfterEnemyDamageReceived")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task>>(effects.OfType<MountainSoulEnemyHex>().Single());
        NativeBaronMultiplier = AccessTools.DeclaredMethod(typeof(HandOfBaronEnemyHex), "ModifyDamageMultiplicative")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(effects.OfType<HandOfBaronEnemyHex>().Single());
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyHitMemoryKinds);
    }

    internal static void DispatchNativeEnemyPlayerHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature dealer, Creature target)
        => InvokeNativeEnemyReaction(modifier, state, simulator, async context =>
        {
            foreach (var effect in NativeEnemyPlayerHits)
            {
                if (!state.Has(effect.Kind)) continue;
                await effect.Callback(context, dealer, target);
                if (simulator.HasPendingChoice) break;
            }
        });

    internal static void RecordNativeMountainHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? source)
    {
        if (!state.Has(MonsterHexKind.MountainSoul) || target.CombatId is not { } id) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeMountainHit(context, target, id, result, dealer, source));
    }

    internal static decimal NativeEnemyBaronDamage(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? source)
    {
        decimal coefficient = 1m;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { coefficient = NativeBaronMultiplier(context, target, amount, props, dealer, source); return Task.CompletedTask; });
        return coefficient;
    }
}
