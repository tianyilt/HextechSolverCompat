using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record NativeKeywordTracker(KeywordPersistenceTracker Tracker,
        ConditionalWeakTable<CardModel, object> Table, Action<CardModel?> Track);
    private static readonly NativeKeywordTracker[] NativeKeywordTrackers = new[] {
        typeof(ThoughtOverwriteKeywordPersistence), typeof(CurtainCallKeywordPersistence),
        typeof(CosplayInnateKeywordPersistence), typeof(CorruptedBranchInnateKeywordPersistence),
        typeof(UndyingEtherealKeywordPersistence) }.Select(type =>
        {
            var tracker = (KeywordPersistenceTracker)AccessTools.DeclaredField(type, "Tracker").GetValue(null)!;
            return new NativeKeywordTracker(tracker,
                (ConditionalWeakTable<CardModel, object>)AccessTools.DeclaredField(typeof(KeywordPersistenceTracker), "_trackedCards").GetValue(tracker)!,
                AccessTools.DeclaredMethod(typeof(KeywordPersistenceTracker), "Track").CreateDelegate<Action<CardModel?>>(tracker));
        }).ToArray();
    private static readonly ConditionalWeakTable<CardModel, object> NativePredictionKeywordCards = new();
    private static readonly object NativePredictionKeywordMarker = new();
    internal static int ReadNativeKeywordMask(CardModel card, bool includeDeck)
    {
        int mask = 0;
        for (int index = 0; index < NativeKeywordTrackers.Length; index++)
            if (NativeKeywordTrackers[index].Table.TryGetValue(card, out _)
                || includeDeck && card.DeckVersion is { } deck && NativeKeywordTrackers[index].Table.TryGetValue(deck, out _))
                mask |= 1 << index;
        return mask;
    }
    private static void CopyNativePredictionKeywordMarkers(CardModel source, CardModel __result)
    {
        if (NativeGeneratedCardContext.Owners.TryGetValue(source, out var owner)) owner.Bind(__result);
        int mask = ReadNativeKeywordMask(source, !NativePredictionKeywordCards.TryGetValue(source, out _));
        NativePredictionKeywordCards.GetValue(__result, _ => NativePredictionKeywordMarker);
        // Prediction copies preserve the current keywords exactly. Only copy
        // marker membership here; Restore would add gameplay clone effects.
        for (int index = 0; index < NativeKeywordTrackers.Length; index++)
            if ((mask & (1 << index)) != 0) NativeKeywordTrackers[index].Track(__result);
    }
    private static bool NativeKeywordShouldPersist(KeywordPersistenceTracker __instance, CardModel card, ref bool __result)
    {
        if (!NativePredictionKeywordCards.TryGetValue(card, out _)) return true;
        var tracker = NativeKeywordTrackers.Single(entry => ReferenceEquals(entry.Tracker, __instance));
        __result = tracker.Table.TryGetValue(card, out _); return false;
    }
    private static bool NativeKeywordIsTracked(KeywordPersistenceTracker __instance, CardModel? card, ref bool __result)
    {
        if (card is null || NativePredictionKeywordCards.TryGetValue(card, out _) || GrowthSimulator is not { } simulator) return true;
        var modifier = simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return true;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        int index = Array.FindIndex(NativeKeywordTrackers, entry => ReferenceEquals(entry.Tracker, __instance));
        __result = state.KeywordDeckMarkers?.GetValueOrDefault(card) is { } mask && (mask & (1 << index)) != 0;
        return false;
    }
    private static void RegisterNativeKeywordPersistence(Harmony harmony)
    {
        var clone = AccessTools.DeclaredMethod(typeof(PredictionUtils), "CloneCardStateForSimulation");
        var copy = AccessTools.Method(typeof(NativeRuneBridge), nameof(CopyNativePredictionKeywordMarkers));
        harmony.Patch(clone, postfix: new HarmonyMethod(copy));
        NativeCallbackContracts.AddNativePostfix(clone, copy, "HextechSolverCompat", Priority.Normal);
        foreach (var (name, prefixName) in new[] { ("ShouldPersist", nameof(NativeKeywordShouldPersist)), ("IsTracked", nameof(NativeKeywordIsTracked)) })
        {
            var method = AccessTools.DeclaredMethod(typeof(KeywordPersistenceTracker), name);
            var prefix = AccessTools.Method(typeof(NativeRuneBridge), prefixName);
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            NativeCallbackContracts.AddNativePrefix(method, prefix, "HextechSolverCompat", Priority.Normal);
        }
        foreach (string name in new[] { "Track", "Restore" }) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(KeywordPersistenceTracker), name));
        RegisterState<CurtainCallRune>(); RuneMirrors.RegisterNativeBase<CurtainCallRune>();
        RegisterState<UndyingUpgradeRune>(); RuneMirrors.RegisterNativeBase<UndyingUpgradeRune>();
        RegisterState<ThoughtOverwriteRune>(); RuneMirrors.RegisterNativeBase<ThoughtOverwriteRune>();
        RegisterNativeReplayHooks<ThoughtOverwriteRune>();
        foreach (var type in new[] { typeof(CurtainCallRune), typeof(UndyingUpgradeRune), typeof(ThoughtOverwriteRune) })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "AfterCardEnteredCombat"));
        var entered = AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "AfterCardEnteredCombat");
        var dispatch = AccessTools.Method(typeof(NativeRuneBridge), nameof(AfterNativeKeywordCardEntered));
        harmony.Patch(entered, postfix: new HarmonyMethod(dispatch));
        NativeCallbackContracts.AddNativePostfix(entered, dispatch, "HextechSolverCompat", Priority.Normal);
    }
    private static void AfterNativeKeywordCardEntered(CombatPredictionSimulator simulator, PredictedCard card)
    {
        var model = card.MutablePreview;
        GeneratedContext(simulator, model.Owner).Bind(model);
        NativePredictionKeywordCards.GetValue(model, _ => NativePredictionKeywordMarker);
        foreach (var rune in ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(model.Owner).OfType<HextechRelicBase>())
            if (rune is CurtainCallRune or UndyingUpgradeRune or ThoughtOverwriteRune or InkshadowRune)
                RequireCompleted(Invoke(rune, simulator, owned => owned.AfterCardEnteredCombat(model)), rune.GetType());
        foreach (var power in ((SimulatedCombatState)simulator.State.CombatState).EffectivePowers().OfType<HextechVitalSparkPower>())
            InvokeNativePower(power, simulator, owned => owned.AfterCardEnteredCombat(model));
    }
    internal static IReadOnlyDictionary<CardModel, int> CaptureNativeKeywordDeckMarkers(CombatPredictionSimulator simulator)
    {
        var player = simulator.State.CombatState.Players.Single();
        var result = player.Deck.Cards.Concat(player.PlayerCombatState!.AllCards.Select(card => card.DeckVersion).OfType<CardModel>())
            .Distinct().ToDictionary(card => card, card => ReadNativeKeywordMask(card, false));
        // Materialize once now, freezing marker membership before later COW
        // copies could read live deck tables. Marker writes remain per model.
        foreach (var card in simulator.State.GetPlayerCombatState(player).AllCards) _ = card.MutablePreview;
        return result.ToFrozenDictionary();
    }
    internal static void WriteNativeLiveKeywords(HextechMayhemModifier modifier, ref ModelPredictionStateWriter writer)
    {
        var groups = modifier.ActiveRunState.Players.Single().PlayerCombatState!.AllCards
            .Select(card => (Card: card, Mask: ReadNativeKeywordMask(card, true))).Where(entry => entry.Mask != 0)
            .GroupBy(entry => entry.Mask).OrderBy(group => group.Key).ToArray();
        writer.Add("nativeKeywordGroups", groups.Length);
        foreach (var group in groups) writer.AddCards("nativeKeywordMask:" + group.Key, group.Select(entry => (CardModel?)entry.Card).ToArray(), unordered: true);
    }
    internal static void WriteNativePredictedKeywords(SimPlayerCombatState player, ref ModelPredictionStateWriter writer)
    {
        var groups = player.AllCards
            .Select(card => (Card: card, Mask: ReadNativeKeywordMask(card.Preview, false))).Where(entry => entry.Mask != 0)
            .GroupBy(entry => entry.Mask).OrderBy(group => group.Key).ToArray();
        writer.Add("nativeKeywordGroups", groups.Length);
        foreach (var group in groups) writer.AddCards("nativeKeywordMask:" + group.Key, group.Select(entry => (PredictedCard?)entry.Card).ToArray(), unordered: true);
    }
}
