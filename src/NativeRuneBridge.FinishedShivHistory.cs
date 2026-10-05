using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeFinishedShivHistory(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ChainInSleeveRune), "ResolveShivProgressFromHistory"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ChainInSleeveRune), "ResolveShivRewards"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ChainInSleeveRune), "CountOwnedShivCardsPlayedFromHistory"),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatHistoryHelper), "CountOwnedCardsPlayed"), nameof(NativeCountFinishedShivs)));
        var copies = AccessTools.DeclaredMethod(typeof(HextechRelicBase), "AddCardCopiesToCombatHand");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ChainInSleeveRune), "AddShivRewardCards"),
            new(copies.MakeGenericMethod(typeof(Shiv)), AccessTools.Method(typeof(NativeRuneBridge), nameof(AddNativeRewardCopies)).MakeGenericMethod(typeof(Shiv)), 1),
            new(copies.MakeGenericMethod(typeof(SovereignBlade)), AccessTools.Method(typeof(NativeRuneBridge), nameof(AddNativeRewardCopies)).MakeGenericMethod(typeof(SovereignBlade)), 1));
        foreach (string name in new[] { "AfterCardPlayed", "AfterCardPlayedLate", "IsCountedShivPlay", "BeforeCombatStart", "AfterCombatEnd", "ResetCounter", "GetShivsPlayedThisCombat" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ChainInSleeveRune), name));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechKnifeHelper), "IsShivLike"));
        RegisterState<ChainInSleeveRune>(); RuneMirrors.RegisterNativeBase<ChainInSleeveRune>();
        RegisterAfterCardPlayedCallback<ChainInSleeveRune>();
        AfterCardPlayedMirrors.LateRegistry.Register<ChainInSleeveRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, owned => owned.AfterCardPlayedLate(new ThrowingPlayerChoiceContext(), context.CardPlay)), typeof(ChainInSleeveRune)));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(HookMirrors), "AfterCardPlayed"), nameof(UpdateNativeFinishedShivHistory));
    }

    internal static int CountLiveFinishedShivs(Player owner) => HextechCombatHistoryHelper.CountOwnedCardsPlayed(owner,
        card => HextechKnifeHelper.IsShivLike(card, owner), firstInSeriesOnly: false, includeAutoPlay: true);
    private static int CountPredictedFinishedShivs(CombatPredictionSimulator simulator, Player owner)
        => simulator.History.Entries.OfType<CombatPredictionCardPlayFinishedEntry>().Count(entry =>
            entry.CardPlay.Card.Owner == owner && HextechKnifeHelper.IsShivLike(entry.CardPlay.Card, owner));
    private static void UpdateNativeFinishedShivHistory(CombatPredictionSimulator simulator)
    {
        // SDK records Finished immediately before this hook. Update before any
        // listener can nest another play or suspend execution for a choice.
        foreach (var player in simulator.State.CombatState.Players)
            foreach (var rune in ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player).OfType<ChainInSleeveRune>())
            {
                var state = ModelPredictionStateMirrors.Get<NativeRuneState>(simulator, rune);
                state.FinishedShivHistory = state.RootFinishedShivHistory + CountPredictedFinishedShivs(simulator, player);
            }
    }
    private static int NativeCountFinishedShivs(Player? owner, Func<CardModel, bool> matches, bool firstInSeriesOnly, bool includeAutoPlay)
    {
        if (_simulator is null) return HextechCombatHistoryHelper.CountOwnedCardsPlayed(owner, matches, firstInSeriesOnly, includeAutoPlay);
        if (owner is null) return 0;
        if (firstInSeriesOnly || !includeAutoPlay) throw new PredictionUnsupportedException("Reviewed ChainInSleeve finished-history flags changed.");
        var rune = ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(owner).OfType<ChainInSleeveRune>().Single();
        return ModelPredictionStateMirrors.Get<NativeRuneState>(_simulator, rune).FinishedShivHistory;
    }
    private static class NativeRewardCopies<T> where T : CardModel
    {
        internal static readonly Func<HextechRelicBase, int, Action<CardModel>?, Task> Original =
            AccessTools.DeclaredMethod(typeof(HextechRelicBase), "AddCardCopiesToCombatHand").MakeGenericMethod(typeof(T))
                .CreateDelegate<Func<HextechRelicBase, int, Action<CardModel>?, Task>>();
    }
    private static Task AddNativeRewardCopies<T>(HextechRelicBase rune, int count, Action<CardModel>? configure) where T : CardModel
    {
        if (_simulator is null) return NativeRewardCopies<T>.Original(rune, count, configure);
        if (rune.Owner is null || count <= 0 || !_simulator.IsInProgress || _simulator.IsOverOrEnding) return Task.CompletedTask;
        var cards = Enumerable.Range(0, count).Select(_ =>
        {
            var card = _simulator.State.CombatState.CreateCard<T>(rune.Owner);
            configure?.Invoke(card);
            return (CardModel)card;
        }).ToArray();
        return AddNativeGeneratedCards(cards, PileType.Hand, true, CardPilePosition.Bottom, false);
    }
}
