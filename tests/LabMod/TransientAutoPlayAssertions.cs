using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeTransientAutoPlay(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, int expectedPlays)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var osty = player.Osty ?? throw new Exception("Transient autoplay requires an actual Osty.");
        var rune = player.Relics.OfType<AutoPatrolRune>().Single();
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string frozen = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        void Apply(CombatPredictionSimulator sim)
        {
            var shadow = (SimulatedCombatState)sim.State.CombatState;
            AfterPlayerTurnStartMirrors.Invoke(shadow.RelicsOf(player).OfType<AutoPatrolRune>().Single(),
                new AfterPlayerTurnStartMirrorContext { Simulator = sim, Player = player, Choices = new(null) }, 1);
        }
        var baseline = parent.Fork();
        Apply(baseline);
        var child = parent.Fork();
        int hp = osty.CurrentHp, maxHp = osty.MaxHp;
        try
        {
            osty.SetMaxHpInternal(maxHp + 100);
            osty.SetCurrentHpInternal(1);
            Apply(child);
        }
        finally { osty.SetMaxHpInternal(maxHp); osty.SetCurrentHpInternal(hp); }
        if (Stamp(child) != Stamp(baseline) || Stamp(parent) != frozen || Stamp(sibling) != frozen
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Transient autoplay read live Osty HP or changed a parent/sibling/live combat.");
        int predictedPlays = child.History.Entries.OfType<CombatPredictionCardPlayStartedEntry>()
            .Count(entry => entry.CardPlay.Card.Id.Entry == "SWEEPING_GAZE");
        if (predictedPlays != expectedPlays)
            throw new Exception($"Transient boundary did not execute expected cards: {predictedPlays} != {expectedPlays}.");
        var predicted = (SimulatedCombatState)child.State.CombatState;
        var expected = UnattendedTestRunner.CaptureSimulated(child, predicted, player, enemy);
        var fork = child.Fork();
        runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureSimulated(fork,
            (SimulatedCombatState)fork.State.CombatState, player, enemy), "TransientAutoPlay", "Fork");
        if (Stamp(fork) != Stamp(child)) throw new Exception("Transient callback completion did not preserve fork state.");
        int before = CombatManager.Instance.History.CardPlaysStarted.Count(entry => entry.CardPlay.Card.Id.Entry == "SWEEPING_GAZE");
        await rune.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), player);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        int actualPlays = CombatManager.Instance.History.CardPlaysStarted.Count(entry => entry.CardPlay.Card.Id.Entry == "SWEEPING_GAZE") - before;
        if (actualPlays != expectedPlays) throw new Exception("Native transient action count differed.");
        runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureActual(combat, player, enemy), "TransientAutoPlay", "NativeCallback");
        AssertNativeScalarModels(child, combat);
        await VerifyChoiceRound(runner, scenario);
        GD.Print($"HEXTECH_NATIVE_TRANSIENT_AUTOPLAY_VERIFIED initial_osty_hp={hp} plays={actualPlays} native_callback=true native_round=true full_snapshots=true rng=true frozen_hp=true parent=true sibling=true live=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
