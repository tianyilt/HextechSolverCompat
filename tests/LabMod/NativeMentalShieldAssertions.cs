using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task VerifyNativeMentalShield(UnattendedTestRunner runner,
        UnattendedTestRunner.ScenarioContext scenario, int expectedGain)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var rune = player.Relics.OfType<MentalShieldRune>().Single();
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var capturedRune = ((SimulatedCombatState)child.State.CombatState).RelicsOf(player)
            .OfType<MentalShieldRune>().Single();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string parentBefore = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        BeforeSideTurnEndMirrors.Invoke(capturedRune, new() { Simulator = child, Side = CombatSide.Enemy, Participants = combat.Enemies });
        if (Stamp(child) != parentBefore) throw new Exception("MentalShield triggered on the enemy side.");
        BeforeSideTurnEndMirrors.Invoke(capturedRune, new() { Simulator = child, Side = CombatSide.Player, Participants = [player.Creature] });
        if (Stamp(parent) != parentBefore || Stamp(sibling) != parentBefore
            || ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new Exception("MentalShield native callback changed parent, sibling or live state.");
        int before = player.Creature.Block;
        await rune.BeforeSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Enemy, combat.Enemies);
        if (player.Creature.Block != before) throw new Exception("Native MentalShield triggered on the enemy side.");
        await rune.BeforeSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Player, [player.Creature]);
        if (player.Creature.Block - before != expectedGain)
            throw new Exception($"Native MentalShield block gain expected={expectedGain} actual={player.Creature.Block - before}.");
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child,
            (SimulatedCombatState)child.State.CombatState, player, combat.Enemies.Single()),
            UnattendedTestRunner.CaptureActual(combat, player, combat.Enemies.Single()), "HextechMentalShield", "BeforeSideTurnEnd");
        if (expectedGain > 0 && Stamp(child) == parentBefore)
            throw new Exception("MentalShield block gain omitted continuation state.");
        GD.Print($"HEXTECH_NATIVE_MENTAL_SHIELD_VERIFIED hand={player.PlayerCombatState!.Hand.Cards.Count} gain={expectedGain} native_callback=true wrong_side=true fork=true live_isolated=true");
    }
}
