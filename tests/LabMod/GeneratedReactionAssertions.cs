using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using System.Text.Json;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyGeneratedReactions(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement steps)
    {
        var live = scenario.CombatState;
        var player = scenario.Player;
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string parentStamp = Stamp(parent), liveStamp = ContinuationStamp.CaptureLive(live).StateText;
        void Generate(CombatPredictionSimulator simulator, JsonElement step)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            var canonical = ModelDb.AllCards.Single(card => card.Id.Entry == step.GetProperty("cardId").GetString());
            var card = combat.CreateCard(canonical, player);
            var result = simulator.AddGeneratedCardToCombat(PredictedCard.FromGenerated(card),
                Enum.Parse<PileType>(step.GetProperty("pile").GetString()!),
                step.GetProperty("creator").GetString() == "Player" ? player : null);
            if (!result.Success || simulator.HasPendingChoice) throw new Exception("Generated reaction did not finish.");
        }
        var patch = new Harmony("HextechCompatLab.UnreviewedGenerationReaction");
        var target = AccessTools.Method(typeof(EchoRune), "TryGetEchoPile");
        var frozen = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp;
        try
        {
            patch.Patch(target, prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(UnreviewedEnemyTurnPrefix)));
            if (ContinuationStamp.CaptureLive(live).StateText == liveStamp
                || ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp != frozen)
                throw new Exception("Unknown generation reaction patch did not invalidate live routes, or changed frozen composition.");
            try { _ = CombatRootSnapshot.Capture(live); throw new Exception("Unknown generation reaction patch was accepted."); }
            catch (PredictionUnsupportedException error) when (error.Message.Contains("Unreviewed native generation reaction composition")) { }
        }
        finally { patch.Unpatch(target, HarmonyPatchType.Prefix, patch.Id); }
        if (ContinuationStamp.CaptureLive(live).StateText != liveStamp) throw new Exception("Generated reaction patch probe was not restored.");
        var first = steps.EnumerateArray().First();
        var baseline = parent.Fork();
        Generate(baseline, first);
        var child = parent.Fork();
        int hp = player.Creature.CurrentHp;
        try
        {
            player.Creature.SetCurrentHpInternal(0);
            Generate(child, first);
            if (Stamp(child) != Stamp(baseline)) throw new Exception("Generated callback reread live owner death.");
        }
        finally { player.Creature.SetCurrentHpInternal(hp); }
        if (Stamp(parent) != parentStamp || Stamp(sibling) != parentStamp || ContinuationStamp.CaptureLive(live).StateText != liveStamp)
            throw new Exception("Generated reaction changed parent, sibling or live state.");
        GD.Print($"HEXTECH_GENERATED_REACTION_INITIAL stars={player.PlayerCombatState!.Stars} hand={player.PlayerCombatState.Hand.Cards.Count}");
        int initialStars = player.PlayerCombatState!.Stars;
        int index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            Generate(parent, step);
            var canonical = ModelDb.AllCards.Single(card => card.Id.Entry == step.GetProperty("cardId").GetString());
            var actual = live.CreateCard(canonical, player);
            await CardPileCmd.AddGeneratedCardsToCombat([actual], Enum.Parse<PileType>(step.GetProperty("pile").GetString()!),
                step.GetProperty("creator").GetString() == "Player" ? player : null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            foreach (var enemy in live.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(parent,
                    (SimulatedCombatState)parent.State.CombatState, player, enemy),
                    UnattendedTestRunner.CaptureActual(live, player, enemy), "GeneratedReactions", $"Generation:{++index}");
            if (step.TryGetProperty("expectedStarsDelta", out var stars) && player.PlayerCombatState!.Stars - initialStars != stars.GetInt32())
                throw new Exception($"Generated stars count at step {index}: expected={stars.GetInt32()} actual_delta={player.PlayerCombatState!.Stars - initialStars}.");
            if (step.TryGetProperty("expectedHandCount", out var hand) && player.PlayerCombatState!.Hand.Cards.Count != hand.GetInt32())
                throw new Exception("Generated draw/full-hand count is wrong.");
            if (step.TryGetProperty("checkCardId", out var id))
            {
                var cards = player.PlayerCombatState!.AllCards.Where(card => card.Id.Entry == id.GetString()).ToArray();
                if (cards.Length != step.GetProperty("expectedCardCount").GetInt32()) throw new Exception("Generated clone count is wrong.");
                if (step.TryGetProperty("expectedUpgradedCount", out var upgraded)
                    && cards.Count(card => card.IsUpgraded) != upgraded.GetInt32()) throw new Exception($"Generated upgrade count at step {index}: expected={upgraded.GetInt32()} actual={cards.Count(card => card.IsUpgraded)}.");
                if (step.TryGetProperty("expectedEtherealCount", out var ethereal)
                    && cards.Count(card => card.Keywords.Contains(CardKeyword.Ethereal)) != ethereal.GetInt32())
                    throw new Exception("Generated clone lacks Ethereal.");
            }
        }
        GD.Print("HEXTECH_GENERATED_REACTIONS_VERIFIED native_generation=true full_snapshots=true creator=true parent=true sibling=true frozen_owner_death=true no_recursive_generation=true unknown_patch_rejected=true composition_frozen=true");
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechGeneratedReactionRounds", out var rounds))
        {
            for (int i = 0; i < rounds.GetInt32(); i++) await runner.AssertReportRoundAsync(live, player);
            GD.Print($"HEXTECH_GENERATED_REACTION_ROUNDS_VERIFIED rounds={rounds.GetInt32()}");
        }
        if (request.RootElement.TryGetProperty("hextechGeneratedReactionPlayDefend", out _))
        {
            // Reuse the full native play comparison after recapturing the actual next turn.
            var card = player.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry.EndsWith("DEFEND", StringComparison.Ordinal)
                || card.Id.Entry.StartsWith("DEFEND_", StringComparison.Ordinal));
            var sim = CombatRootSnapshot.Capture(live).ForkSimulator();
            UnattendedTestRunner.PlaySimulatedCard(sim, (SimulatedCombatState)sim.State.CombatState,
                sim.State.FindCard(card)!, null, live.Enemies);
            if (!card.TryManualPlay(null)) throw new Exception("Generated reaction next-turn Defend was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            foreach (var enemy in live.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(sim,
                    (SimulatedCombatState)sim.State.CombatState, player, enemy), UnattendedTestRunner.CaptureActual(live, player, enemy),
                    "GeneratedReactions", "NextTurnPlay");
        }
        GD.Print("HEXTECH_GENERATED_REACTION_NEXT_PLAY_VERIFIED native=true full_snapshot=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
