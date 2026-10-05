using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyHitEffectKinds = [MonsterHexKind.ShrinkRay, MonsterHexKind.BloodArmor,
        MonsterHexKind.FinalForm, MonsterHexKind.FeyMagic, MonsterHexKind.Porcupine, MonsterHexKind.Goldrend, MonsterHexKind.BackToBasics];
    private static readonly BloodArmorEnemyHex NativeBloodArmor = new();
    private static readonly PorcupineEnemyHex NativePorcupine = new();
    private static readonly BackToBasicsEnemyHex NativeBackToBasics = new();
    private static readonly GoldrendEnemyHex NativeGoldrend = new();
    private static readonly Func<HextechEnemyHexContext, Creature, decimal, Task> NativeBloodHp = AccessTools.DeclaredMethod(typeof(BloodArmorEnemyHex), "AfterCurrentHpChanged").CreateDelegate<Func<HextechEnemyHexContext, Creature, decimal, Task>>(NativeBloodArmor);
    private static readonly Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task> NativePorcupineReceived = AccessTools.DeclaredMethod(typeof(PorcupineEnemyHex), "AfterEnemyDamageReceived").CreateDelegate<Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task>>(NativePorcupine);
    private static readonly Func<HextechEnemyHexContext, CardModel, AutoPlayType, bool> NativeBackShouldPlay = AccessTools.DeclaredMethod(typeof(BackToBasicsEnemyHex), "ShouldPlay").CreateDelegate<Func<HextechEnemyHexContext, CardModel, AutoPlayType, bool>>(NativeBackToBasics);
    private static readonly Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task> NativeGoldImmediate = AccessTools.DeclaredMethod(typeof(GoldrendEnemyHex), "AfterEnemyDamageGivenImmediate").CreateDelegate<Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task>>(NativeGoldrend);
    private static readonly Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal> NativeGoldMultiplier = AccessTools.DeclaredMethod(typeof(GoldrendEnemyHex), "ModifyDamageMultiplicative").CreateDelegate<Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(NativeGoldrend);
    private static readonly Func<HextechEnemyHexContext, IReadOnlyList<Creature>, Task> NativePorcupineCleanup =
        AccessTools.DeclaredMethod(typeof(PorcupineEnemyHex), "RemoveTemporaryThorns")
            .CreateDelegate<Func<HextechEnemyHexContext, IReadOnlyList<Creature>, Task>>();

    private static void RegisterNativeEnemyHitEffects(Harmony harmony)
    {
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var alive = AccessTools.PropertyGetter(typeof(Creature), "IsAlive");
        var dead = AccessTools.PropertyGetter(typeof(Creature), "IsDead");
        var scaled = AccessTools.GetDeclaredMethods(typeof(HextechEnemyPowerScalingHooks))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(PlatingPower));
        var scaledRelay = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeScaledHealthApply)).MakeGenericMethod(typeof(PlatingPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BloodArmorEnemyHex), "AfterCurrentHpChanged"),
            Site(tracking, nameof(NativeCapturedTracking), 2), Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)), new(scaled, scaledRelay, 1));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FinalFormEnemyHex), "AfterEnemyDamageGivenPlayerHit"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(alive, nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "MaxHp"), nameof(NativeBranchMaxHp)), new(scaled, scaledRelay, 1));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(FinalFormEnemyHex), "ResolvePlating"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeyMagicEnemyHex), "AfterEnemyDamageGivenPlayerHit"),
            Site(tracking, nameof(NativeCapturedTracking), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeyMagicEnemyHex), "BeforePlayerSideTurnStart"),
            Site(tracking, nameof(NativeCapturedTracking), 2), Site(alive, nameof(NativeBranchIsAlive), 2),
            SingleNativePowerSite<ShrinkPower>(), SingleNativePowerSite<NoDrawPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PorcupineEnemyHex), "AfterEnemyDamageReceived"),
            Site(tracking, nameof(NativeCapturedTracking), 3), Site(alive, nameof(NativeBranchIsAlive)), SingleNativePowerSite<ThornsPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PorcupineEnemyHex), "RemoveTemporaryThorns"),
            Site(tracking, nameof(NativeCapturedTracking), 2), Site(alive, nameof(NativeBranchIsAlive)), SingleNativePowerSite<ThornsPower>());
        foreach (string name in new[] { "BeforeTurnEnd", "BeforeSideTurnStart", "GetTemporaryThornsToRemove" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PorcupineEnemyHex), name));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BackToBasicsEnemyHex), "AfterCardPlayed"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(dead, nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BackToBasicsEnemyHex), "ShouldPlay"),
            Site(tracking, nameof(NativeCapturedTracking)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GoldrendEnemyHex), "AfterEnemyDamageGivenImmediate"),
            Site(AccessTools.DeclaredMethod(typeof(HextechGoldrendSync), "HandleEnemyGoldrendHit"), nameof(NativeEnemyGoldLoss)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(GoldrendEnemyHex), "ModifyDamageMultiplicative"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ShrinkRayEnemyHex), "AfterEnemyDamageGivenImmediate"));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyHitEffectKinds);
    }
    internal static void DispatchNativeEnemyHpLoss(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature creature, decimal delta)
    {
        if (!state.Has(MonsterHexKind.BloodArmor)) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeBloodHp(context, creature, delta));
    }
    internal static void DispatchNativePorcupineHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? card)
    {
        if (!state.Has(MonsterHexKind.Porcupine) || target.CombatId is not { } id) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => NativePorcupineReceived(context, target, id, result, dealer, card));
    }
    internal static void ClearNativePorcupine(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator)
    {
        if (!state.Has(MonsterHexKind.Porcupine)) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => NativePorcupineCleanup(context, simulator.State.CombatState.Enemies));
    }
    internal static bool NativeEnemyMayPlay(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, CardModel card, AutoPlayType autoPlay)
    {
        if (!NativeLivingFogShouldPlay(modifier, state, simulator, card, autoPlay)) return false;
        if (!state.Has(MonsterHexKind.BackToBasics)) return true;
        bool result = true;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { result = NativeBackShouldPlay(context, card, autoPlay); return Task.CompletedTask; });
        return result;
    }
    internal static void DispatchNativeEnemyGoldHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature dealer, DamageResult result, Creature target, CardModel? card)
    {
        if (!state.Has(MonsterHexKind.Goldrend)) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeGoldImmediate(context, dealer, result, target, card));
    }
    private static Task NativeEnemyGoldLoss(Player player)
    {
        if (_simulator is null) return HextechGoldrendSync.HandleEnemyGoldrendHit(player);
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        int maximum = (int)AccessTools.DeclaredField(typeof(HextechGoldrendSync), "GoldrendStealAmount").GetRawConstantValue()!;
        combat.LosePlayerGold(player, Math.Min(maximum, Math.Max(0, combat.GetPlayerGold(player))));
        return Task.CompletedTask;
    }
    internal static decimal NativeEnemyGoldrendDamage(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? card)
    {
        decimal result = 1;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { result = NativeGoldMultiplier(context, target, amount, props, dealer, card); return Task.CompletedTask; });
        return result;
    }
}
