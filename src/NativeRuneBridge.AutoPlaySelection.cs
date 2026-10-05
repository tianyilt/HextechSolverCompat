using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeAutoPlaySelection(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MyriadSwordsRune), "AfterShuffle"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat), 3),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeBranchPileCards)),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "Pile"), nameof(NativeBranchCardPile)),
            Site(AccessTools.DeclaredMethod(typeof(HextechAutoPlayHelper), "AutoPlayOrMoveToResultPile"), nameof(AutoPlayNativeCard)),
            Site(AccessTools.Method(typeof(CardPileCmd), "Add", [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)),
            Site(AccessTools.DeclaredMethod(typeof(HextechMyriadSwordsVfx), "Play"), nameof(NativeMyriadVfx)),
            Site(AccessTools.DeclaredMethod(typeof(HextechSovereignBladeVfxSync), "Reconcile"), nameof(NativeMyriadReconcile), 2));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(MyriadSwordsRune), "ModifyCardPlayResultPileTypeAndPositionCompat"));
        RegisterState<MyriadSwordsRune>(); RuneMirrors.RegisterNativeBase<MyriadSwordsRune>();
        RegisterNativeResultLocation<MyriadSwordsRune>();
        AfterShuffleMirrors.Registry.Register<MyriadSwordsRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterShuffle(new ThrowingPlayerChoiceContext(), context.Player)), typeof(MyriadSwordsRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DecisionsDecisionsUpgradeRune), "PlayUpgraded"),
            Site(AccessTools.Method(typeof(CreatureCmd), "TriggerAnim", [typeof(Creature), typeof(string), typeof(float)]), nameof(QuantumAnim)),
            Site(NativeDraw, nameof(Draw)),
            Site(AccessTools.Method(typeof(CardSelectCmd), "FromHand", [typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel)]), nameof(SelectNativeDecisions)),
            Site(AccessTools.Method(typeof(CardCmd), "AutoPlay", [typeof(PlayerChoiceContext), typeof(CardModel), typeof(Creature), typeof(AutoPlayType), typeof(bool), typeof(bool)]), nameof(NativeCommandAutoPlay)));
        foreach (string name in new[] { "CanSelectCard", "AddRequestedPlayCount", "ModifyCardPlayCount", "AfterModifyingCardPlayCount" })
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(DecisionsDecisionsUpgradeRune)).Where(method => method.Name == name)) NativeCallbackContracts.Add(method);
        RegisterConditionalPlay<DecisionsDecisions, DecisionsDecisionsUpgradeRune>((model, context) => model.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (DecisionsDecisions)context.Card.MutablePreview));
        ModifyCardPlayCountMirrors.Registry.Register<DecisionsDecisionsUpgradeRune>((rune, context) => Invoke(rune, context.Simulator,
            model => model.ModifyCardPlayCount(context.Card.MutablePreview, context.Target, context.PlayCount)));
        ModifyCardPlayCountMirrors.AfterRegistry.Register<DecisionsDecisionsUpgradeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterModifyingCardPlayCount(context.Card.MutablePreview)), typeof(DecisionsDecisionsUpgradeRune)));
    }
    private static void NativeMyriadVfx(Creature creature) { if (_simulator is null) HextechMyriadSwordsVfx.Play(creature); }
    private static void NativeMyriadReconcile(Player player) { if (_simulator is null) HextechSovereignBladeVfxSync.Reconcile(player); }
    private static CardChoiceSpec NativeDecisionsSpec(CombatPredictionSimulator simulator, PredictedCard source)
    {
        var hand = simulator.State.GetPlayerCombatState(source.Preview.Owner).Hand.Cards;
        var options = hand.Where(card => !card.References(source.MutablePreview)
            && !card.HasKeyword(simulator.State, CardKeyword.Unplayable)).ToArray();
        int count = Math.Min(1, options.Length);
        return new(PlanChoiceEffect.AutoPlayRepeated, PileType.Hand, count, count, options, hand, ReplacementValue: 0d, IsImplicitAllSelection: options.Length <= 1);
    }
    private static Task<IEnumerable<CardModel>> SelectNativeDecisions(PlayerChoiceContext context, Player player,
        CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        if (_simulator is not { } simulator) return CardSelectCmd.FromHand(context, player, prefs, filter, source);
        if (source is not DecisionsDecisions || prefs.MinSelect != 1 || prefs.MaxSelect != 1 || !prefs.PretendCardsCanBePlayed)
            throw new PredictionUnsupportedException("Native upgraded Decisions selector contract changed.");
        var card = simulator.State.FindCard((CardModel)source) ?? throw new PredictionUnsupportedException("Native Decisions source is absent.");
        var spec = NativeDecisionsSpec(simulator, card);
        if (spec.Options.Count == 0) return Task.FromResult<IEnumerable<CardModel>>([]);
        if (filter is null || spec.Options.Any(option => !filter(option.MutablePreview)))
            throw new PredictionUnsupportedException("Native Decisions filter differs from the captured candidates.");
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var request = new TurnStartChoiceRequest("", spec.Effect, PileType.Hand, 0, spec, Timing: combat.ActiveActionChoiceTiming);
        var cursor = (TurnStartChoiceCursor?)NativeActionChoices.GetValue(combat);
        if (spec.Options.Count == 1)
        {
            if (cursor is null || !cursor.TryTakeIfMatches(request, out var implicitChoice))
                return Task.FromResult<IEnumerable<CardModel>>([spec.Options[0].MutablePreview]);
            return Task.FromResult<IEnumerable<CardModel>>(CardChoiceSupport.ResolveStandaloneChoice(simulator, implicitChoice!, spec.Options, 1, PileType.Hand).Select(item => item.MutablePreview).ToArray());
        }
        if (cursor is null || !cursor.TryTake(request, out var choice))
        { combat.SetPendingTurnStartChoice(request); PauseNativeChoice(simulator); throw new PredictionUnsupportedException("Native Decisions selector did not suspend."); }
        if (choice!.Cards.Count != 1) throw new InvalidPlannedChoiceBranchException("Native Decisions requires exactly one selected card.");
        var selected = CardChoiceSupport.ResolveStandaloneChoice(simulator, choice, spec.Options, 1, PileType.Hand);
        combat.ClearPendingTurnStartChoice();
        return Task.FromResult<IEnumerable<CardModel>>(selected.Select(item => item.MutablePreview).ToArray());
    }
    private static Task NativeCommandAutoPlay(PlayerChoiceContext context, CardModel card, Creature? target,
        AutoPlayType type, bool skipXCapture, bool skipCardPileVisuals)
    {
        if (_simulator is not { } simulator) return CardCmd.AutoPlay(context, card, target, type, skipXCapture, skipCardPileVisuals);
        var owned = simulator.State.FindCard(card) ?? throw new PredictionUnsupportedException("Native command autoplay card is absent.");
        simulator.AutoPlay(owned, target, type, skipXCapture, nestedChoiceSourceId: card.Id.Entry);
        PauseNativeChoice(simulator); return Task.CompletedTask;
    }
}
