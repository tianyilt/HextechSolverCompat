using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyWhiteHole(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, bool fromHandDraw)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        if (player.PlayerCombatState!.Hand.Cards.Count != 0)
            throw new Exception("WhiteHole probe requires initially empty hand.");
        using (var request = Request())
        {
            int expectedUpgrade = request.RootElement.GetProperty("hextechWhiteHoleUpgradeLevels").GetInt32();
            var card = player.PlayerCombatState.DrawPile.Cards.First();
            if (card.Id.Entry != "WHITE_HOLE_CARD" || card.CurrentUpgradeLevel != expectedUpgrade)
                throw new Exception($"WhiteHole fixture upgrade expected={expectedUpgrade}, actual={card.CurrentUpgradeLevel}, card={card.Id.Entry}.");
        }
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string original = Stamp(simulator);
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        child.Draw(player, 1m, fromHandDraw);
        if (Stamp(child) == original || Stamp(simulator) != original || Stamp(sibling) != original
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("WhiteHole draw failed branch/pile/energy/live isolation.");
        simulator.Draw(player, 1m, fromHandDraw);
        await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 1m, player, fromHandDraw);
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
            UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechWhiteHole", "DrawEnergy");
        using (var request = Request())
        {
            int expected = request.RootElement.GetProperty("hextechWhiteHoleDrawEnergy").GetInt32();
            if (player.PlayerCombatState.Energy != expected)
                throw new Exception($"WhiteHole draw energy expected={expected}, actual={player.PlayerCombatState.Energy}.");
        }
        // Scenario ends in -CARD: native runner plays WhiteHole, compares its
        // two-card draw, exhaustion and the entire next player turn against replay.
        await runner.AssertReportRoundAsync(combat, player);
        if (!player.PlayerCombatState.ExhaustPile.Cards.Any(c => c.Id.Entry == "WHITE_HOLE_CARD"))
            throw new Exception("WhiteHole was not actually played and exhausted.");
        GD.Print($"HEXTECH_WHITE_HOLE_VERIFIED from_hand_draw={fromHandDraw} draw_energy=true native_play_and_round=true branches_isolated=true");
        return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }
}
