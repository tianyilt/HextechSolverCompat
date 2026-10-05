using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeTurnResources(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        using var request = Request();
        var probe = request.RootElement.GetProperty("hextechNativeTurnResourceProbe");
        bool early = probe.GetProperty("phase").GetString() == "early";
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        int hp = player.Creature.CurrentHp, energy = player.PlayerCombatState!.Energy,
            block = player.Creature.Block, hand = player.PlayerCombatState.Hand.Cards.Count;
        for (int index = 0; index < probe.GetProperty("repeats").GetInt32(); index++)
        {
            if (early)
            {
                foreach (var rune in shadow.RelicsOf(player).OfType<HextechRelicBase>()
                    .Where(rune => rune is BrutalityRune or SonataRune))
                    AfterPlayerTurnStartMirrors.Invoke(rune, new() { Simulator = child, Player = player,
                        Choices = new TurnStartChoiceCursor(null) }, 0);
            }
            else TurnStartRelicSupport.TriggerAfterEnergyResetLate(child, shadow, player);
            if (child.HasPendingChoice) throw new Exception("Turn resource representative opened an unexpected choice.");
            if (Stamp(parent) != before || Stamp(sibling) != before || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Turn resource callback changed parent, sibling or live battle/RNG.");
        }
        for (int index = 0; index < probe.GetProperty("repeats").GetInt32(); index++)
        {
            if (!early)
                foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
                    await power.AfterEnergyResetLate(player);
            foreach (var rune in player.Relics)
            {
                if (early && rune is BrutalityRune or SonataRune)
                    await rune.AfterPlayerTurnStartEarly(new BlockingPlayerChoiceContext(), player);
                if (!early)
                    await rune.AfterEnergyResetLate(player);
            }
        }
        foreach (var enemy in combat.Enemies)
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechTurnResources", "OriginalOrderedCallbacks");
        foreach (var (key, actual) in new[] { ("hpDelta", player.Creature.CurrentHp - hp),
            ("energyDelta", player.PlayerCombatState.Energy - energy), ("blockDelta", player.Creature.Block - block),
            ("handDelta", player.PlayerCombatState.Hand.Cards.Count - hand) })
            if (probe.TryGetProperty(key, out var expected) && expected.GetInt32() != actual)
                throw new Exception($"Turn resource {key}: expected={expected} actual={actual}.");
        int rounds = probe.GetProperty("rounds").GetInt32();
        for (int index = 0; index < rounds; index++) await runner.AssertReportRoundAsync(combat, player);
        GD.Print($"HEXTECH_NATIVE_TURN_RESOURCE_VERIFIED phase={(early ? "early" : "energy-late")} original_callbacks=true guards=true fork=true live_isolated=true rng=true native_rounds={rounds}");
        return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }
}
