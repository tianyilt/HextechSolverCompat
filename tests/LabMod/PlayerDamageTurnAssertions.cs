using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyPlayerDamage(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        using (var request = Request())
            if (request.RootElement.TryGetProperty("hextechNativeScalarProbe", out var scalar))
                VerifyNativeScalar(scenario, scalar);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var dealer = mode == "self" ? player.Creature : enemy;
        var props = mode == "unpowered" ? ValueProp.Unpowered : ValueProp.Move;
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string original = Stamp(simulator);
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        child.Damage(player.Creature, 6m, props, dealer);
        child.SynchronizePowerAmountPredictionStates();
        if (Stamp(child) == original || Stamp(simulator) != original || Stamp(sibling) != original
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Player damage branch or live continuation isolation failed.");
        for (int i = 0; i < 3; i++)
        {
            simulator.Damage(player.Creature, 6m, props, dealer);
            simulator.SynchronizePowerAmountPredictionStates();
            await HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 6m, props, dealer, null);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechPlayerDamage", mode);
        }
        using (var request = Request())
        {
            if (request.RootElement.TryGetProperty("hextechPlayerDamagePostCard", out var postCard) && postCard.GetBoolean())
            {
                var card = player.PlayerCombatState!.Hand.Cards.Single();
                UnattendedTestRunner.PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, enemy, combat.Enemies);
                if (!card.TryManualPlay(enemy)) throw new Exception("Post-hit native card could not be played.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechPlayerDamage", "PostHitCard");
                GD.Print("HEXTECH_PLAYER_DAMAGE_POST_CARD_VERIFIED native_play=true");
            }
            if (request.RootElement.TryGetProperty("hextechPlayerDamageRounds", out var rounds))
            {
                for (int i = 0; i < rounds.GetInt32(); i++)
                    await runner.AssertReportRoundAsync(combat, player);
                GD.Print($"HEXTECH_PLAYER_DAMAGE_ROUND_VERIFIED rounds={rounds.GetInt32()} native_full_snapshots=true");
            }
        }
        GD.Print($"HEXTECH_PLAYER_DAMAGE_VERIFIED mode={mode} native_each_hit=true branches_isolated=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyProtectiveTurns(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, int rounds)
    {
        var rune = scenario.Player.Relics.OfType<ProtectiveVeilRune>().Single();
        int initial = rune.SavedTurnsThisCombat;
        for (int i = 1; i <= rounds; i++)
        {
            await runner.AssertReportRoundAsync(scenario.CombatState, scenario.Player);
            if (rune.SavedTurnsThisCombat != initial + i)
                throw new Exception("Native protective turn counter did not advance exactly once.");
        }
        GD.Print($"HEXTECH_PROTECTIVE_TURNS_VERIFIED initial={initial} final={rune.SavedTurnsThisCombat} native_rounds={rounds}");
        return new(false, scenario.Player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
