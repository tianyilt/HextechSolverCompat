using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyCardFlowKinds = [MonsterHexKind.SoulFysh,
        MonsterHexKind.EndlessRotation, MonsterHexKind.CorruptedBranch, MonsterHexKind.MindOverMatter,
        MonsterHexKind.TwilightVeil, MonsterHexKind.HundredRefinements];
    private static readonly Func<HextechEnemyHexContext, PlayerChoiceContext, CardModel, bool, Task> NativeEnemyCorruptedExhaust =
        AccessTools.DeclaredMethod(typeof(CorruptedBranchEnemyHex), "AfterCardExhausted")
            .CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CardModel, bool, Task>>(new CorruptedBranchEnemyHex());
    private static readonly Func<HextechEnemyHexContext, Creature, decimal, ValueProp, CardModel?, Task> NativeEnemyTwilightBlock =
        AccessTools.DeclaredMethod(typeof(TwilightVeilEnemyHex), "AfterBlockGained")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, decimal, ValueProp, CardModel?, Task>>(new TwilightVeilEnemyHex());
    private static readonly Func<HextechEnemyHexContext, Creature, DamageResult, Creature?, CardModel?, Task> NativeEnemyHundredHit =
        AccessTools.DeclaredMethod(typeof(HundredRefinementsEnemyHex), "AfterEnemyDamageReceivedAny")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, DamageResult, Creature?, CardModel?, Task>>(new HundredRefinementsEnemyHex());

    private static void RegisterNativeEnemyCardFlow(Harmony harmony)
    {
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var add = AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SoulFyshEnemyHex), "AfterShuffle"),
            Site(combat, nameof(NativeBranchCombat)), Site(add, nameof(AddNativeGeneratedCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EndlessRotationEnemyHex), "AfterShuffle"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CorruptedBranchEnemyHex), "AfterCardExhausted"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(tracking, nameof(NativeCapturedTracking)), Site(add, nameof(AddNativeGeneratedCard)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechEnemyStatusCards), "Create"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MindOverMatterEnemyHex), "AfterCardDrawn"),
            Site(dead, nameof(NativeBranchIsDead)), Site(tracking, nameof(NativeCapturedTracking)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(MindOverMatterEnemyHex), "TryConsumeFirstDraw"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TwilightVeilEnemyHex), "AfterBlockGained"),
            Site(combat, nameof(NativeBranchCombat), 2),
            Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "GetAliveEnemies"), nameof(NativeContextAliveEnemies)),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(TwilightVeilEnemyHex), "ShouldMirrorBlock"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HundredRefinementsEnemyHex), "AfterEnemyDamageReceivedAny"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            Site(tracking, nameof(NativeCapturedTracking)), SingleNativePowerSite<HextechTemporarySlowPower>());
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HundredRefinementsEnemyHex), "ResolveSlowReduction"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechEnemyHexEffect), "ReachesHitThreshold"));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyCardFlowKinds);
    }

    internal static void DispatchNativeEnemyExhaust(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, CardModel card, bool ethereal)
    {
        if (state.Has(MonsterHexKind.CorruptedBranch)) InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyCorruptedExhaust(context, new ThrowingPlayerChoiceContext(), card, ethereal));
    }
    internal static void DispatchNativeEnemyBlock(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature creature, decimal amount, ValueProp props, CardModel? source)
    {
        if (state.Has(MonsterHexKind.TwilightVeil)) InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyTwilightBlock(context, creature, amount, props, source));
    }
    internal static void DispatchNativeEnemyHundredHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? source)
    {
        if (state.Has(MonsterHexKind.HundredRefinements)) InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyHundredHit(context, target, result, dealer, source));
    }
}
