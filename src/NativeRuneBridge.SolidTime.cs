using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Saves;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static bool _nativeStoredBody;
    [ThreadStatic] private static PredictedCard? _nativeStoredCard;
    private static void RegisterNativeSolidTime(Harmony harmony)
    {
        var type = typeof(SolidTimeRune);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "TryGetDeckPower"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "DeckVersion"), nameof(NativeGrowthDeckVersion)),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "Pile"), nameof(NativeSolidDeckPile)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeGrowthDeckCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterCardPlayed"),
            Site(AccessTools.Method(typeof(CardPileCmd), "RemoveFromDeck", [typeof(CardModel), typeof(bool)]), nameof(RemoveNativePermanentCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "TriggerStoredPowersAtCombatStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "CreateCombatCard"),
            Site(AccessTools.Method(typeof(SaveManager), "MarkCardAsSeen", [typeof(CardModel)]), nameof(MarkNativeSolidCardSeen)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "ApplyStoredPowerDirectly"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "Pile"), nameof(NativeBranchCardPile), 3),
            Site(AccessTools.Method(typeof(CardPileCmd), "Add", [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(AddNativeRawCard)),
            Site(AccessTools.Method(typeof(CardPileCmd), "RemoveFromCombat", [typeof(CardModel), typeof(bool)]), nameof(RemoveNativeSolidCombatCard)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(MethodBase), "Invoke", [typeof(object), typeof(object[])]), nameof(InvokeNativeStoredCardBody)));
        var voidSite = SingleNativePowerSite<VoidFormPower>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "TryApplySolidTimeSpecialCase"),
            new NativeCallSite(voidSite.Original, AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ApplyNativeStoredVoid)), 1));
        // Preserve source JSON validation, canonical identity, upgrades,
        // MadScience type/rider and deterministic target selection verbatim.
        foreach (string name in new[] { "BeforeCombatStart", "AfterCombatEnd", "AfterPlayerTurnStartLate", "AppendStoredCard",
            "DecodeStoredCards", "IsStoredPowerCard", "IsStoredAsPowerCard", "TryGetCanonical", "ApplyUpgradeLevels", "ApplyStoredCardState", "PickTarget" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, name));
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name == "StoredCard"))
            foreach (var method in AccessTools.GetDeclaredMethods(nested)) NativeCallbackContracts.Add(method);
        ModelPredictionStateMirrors.RegisterRelic<SolidTimeRune, NativeRuneState>("native-solid-time-permanent-deck-v1",
            (simulator, live) => new NativeRuneState(NativeRuneState.Clone(live))
            { SelfUpgrades = NativeSelfUpgradeState.Capture(simulator, live.Owner), StableGeneration = new(live) },
            (SolidTimeRune live, ref ModelPredictionStateWriter writer) =>
            { NativeRuneState.WriteModel(live, ref writer); MysterySeedState.Write(new(live), ref writer); }, NativeRuneState.WriteState);
        RuneMirrors.RegisterNativeBase<SolidTimeRune>();
        RegisterAfterCardPlayedCallback<SolidTimeRune>();
        AfterPlayerTurnStartMirrors.RegisterLate<SolidTimeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStartLate(new ThrowingPlayerChoiceContext(), context.Player)), type));
        // The SDK's common effect completion also contains ordinary played-card
        // reactions. A stored body has no play hooks/history in the original.
        foreach (var target in new[] {
            AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "ResolveMonologues"),
            AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "SynchronizePanacheState"),
            AccessTools.DeclaredMethod(typeof(PowerLifecycleSupport), "AfterCardPlayed") })
        {
            var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(AllowNativeStoredPlayReaction));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            NativeCallbackContracts.AddNativePrefix(target, prefix, "HextechSolverCompat", Priority.Normal);
        }
        var core = AccessTools.DeclaredMethod(typeof(CorePowerSupport), "ApplyCardPowers");
        var enter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterNativeStoredEffectBody));
        var leave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveNativeStoredEffectBody));
        harmony.Patch(core, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
        NativeCallbackContracts.AddScope(core, enter, leave,
            AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ResolveKnowThyPlaceCommands)));
    }
    private static bool AllowNativeStoredPlayReaction() => !_nativeStoredBody;
    private static Task<VoidFormPower?> ApplyNativeStoredVoid(Creature target, decimal amount, Creature? applier,
        CardModel? cardSource, bool silent)
    {
        if (_simulator is not { } simulator)
            return HextechPowerCmdCompat.Apply<VoidFormPower>(target, amount, applier, cardSource, silent);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        // Original BeforeApplied/BeforePowerAmountChanged disables the current
        // turn's free-card slots. Ordinary VoidForm also ends the turn; this
        // original stored-power special case deliberately does not.
        TurnStartPowerSupport.PrepareVoidFormApplication(simulator, combat, target);
        var result = ApplyPowerOne<VoidFormPower>(target, amount, applier, cardSource, silent);
        TurnStartPowerSupport.PrepareVoidFormApplication(simulator, combat, target);
        return result;
    }
    private static void EnterNativeStoredEffectBody(PredictedCard playedCard, out bool __state)
    {
        __state = _nativeStoredBody;
        // A real nested autoplay retains its own played-card reactions.
        _nativeStoredBody = ReferenceEquals(_nativeStoredCard, playedCard);
    }
    private static void LeaveNativeStoredEffectBody(bool __state) => _nativeStoredBody = __state;

    private static Task RemoveNativePermanentCard(CardModel card, bool showPreview)
    {
        if (_simulator is null) return CardPileCmd.RemoveFromDeck(card, showPreview);
        NativeSelfUpgradeState.Require(_simulator).RemovePersistent(card);
        return Task.CompletedTask;
    }
    private static CardPile? NativeSolidDeckPile(CardModel card)
    {
        if (_simulator is null) return card.Pile;
        // This audited call site reads only Pile.Type. The native deck object
        // supplies that immutable identity; membership and cards remain owned.
        return NativeSelfUpgradeState.Require(_simulator).DeckCards.Contains(card) ? card.Owner.Deck : null;
    }
    private static void MarkNativeSolidCardSeen(SaveManager manager, CardModel card)
    {
        if (_simulator is null) manager.MarkCardAsSeen(card);
        // The audited sole caller just used the owned ICombatState.CreateCard.
        // The unpiled model is registered when its original temporary Add runs.
        else if (!card.IsMutable || !_simulator.State.CombatState.Players.Contains(card.Owner))
            throw new PredictionUnsupportedException("Stored power creation returned an invalid detached model.");
    }
    private static Task RemoveNativeSolidCombatCard(CardModel card, bool skipVisuals)
    {
        if (_simulator is null) return CardPileCmd.RemoveFromCombat(card, skipVisuals);
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Stored power cleanup received a foreign card.");
        _simulator.RemoveFromCombat(predicted);
        return Task.CompletedTask;
    }
    private static object? InvokeNativeStoredCardBody(MethodBase method, object? receiver, object?[]? arguments)
    {
        if (_simulator is not { } simulator) return method.Invoke(receiver, arguments);
        if (receiver is not CardModel card || arguments is not [PlayerChoiceContext, CardPlay play]
            || method.Name != "OnPlay" || method.DeclaringType != typeof(CardModel))
            throw new PredictionUnsupportedException("SolidTime reflection call no longer targets the reviewed card body.");
        var predicted = simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Stored power body received a foreign card.");
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var previous = _nativeStoredCard; _nativeStoredCard = predicted;
        try
        {
            using (simulator.BeginExecutionDispatch())
            using (((ICombatPredictionCardExecutionSink)combat).BeginCardPowerApplication(predicted))
            {
                int history = simulator.History.Entries.Count;
                int block = simulator.State.GetCreature(card.Owner.Creature).Block;
                CardOnPlayMirrors.Invoke(simulator, predicted, play);
                PauseNativeChoice(simulator);
                _ = CorePowerSupport.ApplyCardPowers(simulator, combat, predicted, play, play.Target,
                    block, 0m, history, new HashSet<uint>());
                PauseNativeChoice(simulator);
            }
        }
        finally { _nativeStoredCard = previous; }
        return Task.CompletedTask;
    }
}
