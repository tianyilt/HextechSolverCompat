using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static object KeywordTracker(string name) => AccessTools.DeclaredField(
        typeof(ModEntry).Assembly.GetType("HextechRunes." + name, throwOnError: true)!, "Tracker").GetValue(null)!;
    private static void SetupNativeKeywords(UnattendedTestRunner.ScenarioContext scenario, JsonElement entries)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            var card = scenario.Player.PlayerCombatState!.Hand.Cards[entry.GetProperty("handIndex").GetInt32()];
            var tracker = KeywordTracker(entry.GetProperty("tracker").GetString()!);
            var restore = AccessTools.DeclaredMethod(tracker.GetType(), "Restore").CreateDelegate<Action<CardModel>>(tracker);
            if (card.DeckVersion is null) throw new Exception("Keyword input requires a real distinct deck version.");
            restore(card.DeckVersion);
            foreach (var rune in scenario.Player.Relics.OfType<HextechRelicBase>())
                if (rune is ThoughtOverwriteRune or CurtainCallRune or UndyingUpgradeRune)
                    if (!rune.AfterCardEnteredCombat(card).IsCompletedSuccessfully)
                        throw new Exception("Native entered-card keyword callback did not complete synchronously.");
        }
        GD.Print("HEXTECH_NATIVE_KEYWORD_SETUP original_deck_markers=true original_entered_callbacks=true");
    }
    private static void VerifyNativeKeywordIsolation(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState; var player = scenario.Player;
        var tracker = KeywordTracker("ThoughtOverwriteKeywordPersistence");
        var table = (ConditionalWeakTable<CardModel, object>)AccessTools.DeclaredField(tracker.GetType(), "_trackedCards").GetValue(tracker)!;
        var track = AccessTools.DeclaredMethod(tracker.GetType(), "Track").CreateDelegate<Action<CardModel?>>(tracker);
        var root = CombatRootSnapshot.Capture(combat); var parent = root.ForkSimulator();
        var child = parent.Fork(); var sibling = parent.Fork();
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent); string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var candidate = child.State.GetPlayerCombatState(player).Hand.Cards.First(card => !table.TryGetValue(card.Preview, out _));
        var beforeKeywords = candidate.Preview.Keywords.ToArray();
        track(candidate.MutablePreview);
        if (!candidate.Preview.Keywords.SequenceEqual(beforeKeywords) || Key(child) == key || Stamp(child) == stamp
            || Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Keyword marker-only mutation is shared or missing from either signature.");
        var grandchild = child.Fork();
        foreach (var card in grandchild.State.GetPlayerCombatState(player).AllCards) _ = card.MutablePreview;
        if (Key(grandchild) != Key(child) || Stamp(grandchild) != Stamp(child))
            throw new Exception("Keyword marker membership was lost through fork and preview COW.");
        GD.Print("HEXTECH_NATIVE_KEYWORD_ISOLATION_VERIFIED marker_only_key=true marker_only_continuation=true fork_cow_preserved=true parent_sibling_live_unchanged=true");
    }
}
