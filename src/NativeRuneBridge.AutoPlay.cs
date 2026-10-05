using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeAutoPlayFamily(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var ending = AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding));
        var pile = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile));
        var cards = AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards));
        var auto = AccessTools.Method(typeof(HextechAutoPlayHelper), "AutoPlayOrMoveToResultPile");
        NativeCallbackContracts.Add(auto);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RallyingCallRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead), 2), Site(combat, nameof(NativeBranchCombat), 4),
            Site(ending, nameof(NativeBranchEnding), 2), Site(pile, nameof(NativeBranchCardPile)),
            Site(cards, nameof(NativeBranchPileCards)), Site(auto, nameof(AutoPlayNativeCard)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(RallyingCallRune), "SnapshotMatches"));
        RegisterAfterCardPlayed<RallyingCallRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ScaredStiffRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(pile, nameof(NativeBranchCardPile)), Site(cards, nameof(NativeBranchPileCards)),
            Site(auto, nameof(AutoPlayNativeCard)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRuneTargeting), "PickRandomHittableEnemy"));
        RegisterStableState<ScaredStiffRune>();
        RegisterNativeEndTurn<ScaredStiffRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GroundedRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Block)), nameof(NativeBranchBlock), 2),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        RegisterState<GroundedRune>();
        RegisterNativeEndTurn<GroundedRune>();
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition
                && method.GetParameters()[0].ParameterType == typeof(Creature)).MakeGenericMethod(typeof(EchoFormPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DoubleExistenceRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)), new(apply,
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(typeof(EchoFormPower)), 1));
        RegisterState<DoubleExistenceRune>();
        RegisterNativeEndTurn<DoubleExistenceRune>();
    }

    private static void RegisterNativeEndTurn<T>() where T : HextechRelicBase
        => RuneMirrors.RegisterNativeBase<T>(beforeEnd: (rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeTurnEnd(new ThrowingPlayerChoiceContext(), context.Side)), typeof(T)));

    private static int NativeBranchBlock(Creature creature)
        => _simulator is { } sim ? sim.State.GetCreature(creature).Block : creature.Block;

    private static IReadOnlyList<CardModel> NativeBranchPileCards(CardPile pile)
    {
        if (_simulator is null) return pile.Cards;
        IReadOnlyList<CardModel> cards = null!;
        if (PileCardsGetter(pile, ref cards))
            throw new PredictionUnsupportedException("Native autoplay requested an uncaptured deck pile.");
        return cards;
    }

    private static Task AutoPlayNativeCard(PlayerChoiceContext context, CardModel card, Creature? target,
        AutoPlayType type, bool skipXCapture, bool skipCardPileVisuals)
    {
        if (_simulator is null)
            return HextechAutoPlayHelper.AutoPlayOrMoveToResultPile(context, card, target, type, skipXCapture, skipCardPileVisuals);
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native autoplay card is absent from the branch.");
        // Use the original callback's target and order. Vanilla solver autoplay
        // handles free resources, unplayability and the native result pile.
        _simulator.AutoPlay(predicted, target, type, skipXCapture, nestedChoiceSourceId: card.Id.Entry);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }
}
