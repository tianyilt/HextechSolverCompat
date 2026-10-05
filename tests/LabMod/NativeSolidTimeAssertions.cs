using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeSolidTime(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var player = scenario.Player; var combat = scenario.CombatState;
        var rune = player.Relics.OfType<SolidTimeRune>().Single();
        using var request = Request();
        var initialDeck = player.Deck.Cards.ToArray();
        if (request.RootElement.TryGetProperty("hextechNativeSolidStoredCards", out var stored))
        {
            rune.SavedRemovedPowerCardsJson = JsonSerializer.Serialize(stored.EnumerateArray().Select(item =>
            {
                var canonical = ModelDb.AllCards.Single(card => card.Id.Entry == item.GetProperty("cardId").GetString());
                return new { category = canonical.Id.Category, entry = canonical.Id.Entry,
                    upgrades = item.TryGetProperty("upgrades", out var upgrades) ? upgrades.GetInt32() : 0 };
            }).ToArray());
            await rune.BeforeCombatStart();
            var root = CombatRootSnapshot.Capture(combat);
            var simulator = root.ForkSimulator(); var shadow = (SimulatedCombatState)simulator.State.CombatState;
            var parent = simulator.Fork(); var sibling = simulator.Fork();
            string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
                root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            string parentStamp = Stamp(parent), siblingStamp = Stamp(sibling);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
                AfterPlayerTurnStartMirrors.Invoke(shadow.RelicsOf(player).OfType<SolidTimeRune>().Single(),
                    new() { Simulator = simulator, Player = player, Choices = new TurnStartChoiceCursor([]) }, 2);
                if (simulator.HasPendingChoice || ContinuationStamp.CaptureLive(combat).StateText != liveBefore
                    || Stamp(parent) != parentStamp || Stamp(sibling) != siblingStamp)
                    throw new Exception("SolidTime direct-body opening contaminated another state or unexpectedly suspended.");
                await rune.AfterPlayerTurnStartLate(new ThrowingPlayerChoiceContext(), player);
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                foreach (var enemy in combat.Enemies)
                    runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                        UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechSolidTime", "OriginalDirectBody:" + repeat);
                AssertNativeScalarModels(simulator, combat);
            }
        }
        VerifyNativeTokenFork(scenario);
        var result = await VerifyNativeTokenActual(runner, scenario);
        if (request.RootElement.TryGetProperty("hextechNativeSolidExpectedRemoved", out var removed))
        {
            if (initialDeck.Length - player.Deck.Cards.Count != removed.GetInt32())
                throw new Exception("SolidTime did not remove the source-derived number of permanent cards.");
            var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(rune.SavedRemovedPowerCardsJson) ? "[]" : rune.SavedRemovedPowerCardsJson);
            int initialStored = request.RootElement.TryGetProperty("hextechNativeSolidStoredCards", out var cards) ? cards.GetArrayLength() : 0;
            if (json.RootElement.GetArrayLength() != initialStored + removed.GetInt32())
                throw new Exception("SolidTime stored a duplicate or generated card instead of one permanent original.");
            json.Dispose();
        }
        GD.Print("HEXTECH_NATIVE_SOLID_TIME_VERIFIED original_json=true permanent_deck_owned=true direct_body_without_play_hooks=true once_per_combat=true full_snapshots=true fork_key_stamp_rng=true");
        return result;
    }
}
