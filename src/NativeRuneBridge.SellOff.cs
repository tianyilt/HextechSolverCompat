using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeSellOff(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.Method(typeof(SellOffRune), "CanAutoPlayDiscardedCard"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(SellOffRune), "AutoPlayDiscardedCard"),
            Site(AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.ExhaustOnNextPlay)), nameof(SetNativeExhaustOnNextPlay)),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)),
            Site(AccessTools.Method(typeof(HextechAutoPlayHelper), "AutoPlayOrMoveToResultPile"), nameof(AutoPlayNativeCard)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SellOffRune), "AfterCardDiscarded"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(SellOffRune), "RequiresEnemyTarget"));
        RegisterStableState<SellOffRune>();
        RuneMirrors.RegisterNativeBase<SellOffRune>();
        CombatSolver.Engine.InCombat.Mirrors.Hooks.Card.AfterCardDiscardedMirrors.Registry.Register<SellOffRune>((relic, context) =>
            RequireCompleted(Invoke(relic, context.Simulator,
                model => model.AfterCardDiscarded(new ThrowingPlayerChoiceContext(), context.MutablePreviewCard)), typeof(SellOffRune)));
    }

    private static void SetNativeExhaustOnNextPlay(CardModel card, bool value)
    {
        if (_simulator is null) { card.ExhaustOnNextPlay = value; return; }
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native discard autoplay exhaust target is absent in the branch.");
        predicted.MutablePreview.ExhaustOnNextPlay = value;
    }
}
