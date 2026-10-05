using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeCallbackChoice(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string cardId)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var card = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == cardId);
        int turn = player.PlayerCombatState.TurnNumber;
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(turn,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, turn, root.Forecast, root.StartTurnNumber).StateText;
        var parentKey = Key(parent);
        string parentStamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        void AssertIsolation()
        {
            if (Key(parent) != parentKey || Key(sibling) != parentKey || Stamp(parent) != parentStamp
                || Stamp(sibling) != parentStamp || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Native callback choice replay changed a parent, sibling or live combat.");
        }
        List<PlanCardChoice> choices = [];
        PlanAction Action() => new(PlanActionKind.PlayCard, turn, CardId: cardId, NestedChoices: choices.ToArray());
        SimulationSnapshot? next = null;
        try
        {
            for (int attempt = 0; attempt <= 16; attempt++)
            {
                next?.ReleaseSimulator();
                next = null;
                next = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [Action()], null, 0, null);
                AssertIsolation();
                var shadow = (SimulatedCombatState)next.Simulator.State.CombatState;
                if (!next.Simulator.HasPendingChoice) break;
                if (attempt == 16) throw new Exception("Native callback choice replay exceeded the bounded limit.");
                if (next.Simulator.TakeExecutionContinuation() is not null)
                    throw new Exception("Native async callback exposed an incomplete optimized continuation.");
                var pending = shadow.PendingTurnStartChoice
                    ?? throw new Exception("Native callback choice fixture expected a card selection.");
                var spec = TurnStartChoiceSupport.BuildPendingSpec(next.Simulator, shadow, player);
                choices.Add(CardChoiceSupport.BuildAutomaticPolicyChoice(spec) with
                { SourceId = pending.SourceId, ContextId = pending.ContextId, Timing = pending.Timing });
            }
            if (next is null || next.Simulator.HasPendingChoice || choices.Count == 0)
                throw new Exception("Native callback fixture did not exercise and resolve a real nested selection.");
            var predicted = (SimulatedCombatState)next.Simulator.State.CombatState;
            var expected = UnattendedTestRunner.CaptureSimulated(next.Simulator, predicted, player, enemy);
            var fork = next.Simulator.Fork();
            runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureSimulated(fork,
                (SimulatedCombatState)fork.State.CombatState, player, enemy), "NativeCallbackChoice", "Fork");
            if (Key(fork) != Key(next.Simulator) || Stamp(fork) != Stamp(next.Simulator))
                throw new Exception("Resolved native callback fork changed its key or continuation stamp.");
            var replay = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [Action()], null, 0, null);
            try
            {
                runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureSimulated(replay.Simulator,
                    (SimulatedCombatState)replay.Simulator.State.CombatState, player, enemy), "NativeCallbackChoice", "Replay");
                if (replay.StateKey != next.StateKey || Stamp(replay.Simulator) != Stamp(next.Simulator))
                    throw new Exception("Complete native callback replay was not deterministic.");
                AssertIsolation();
            }
            finally { replay.ReleaseSimulator(); }
            var selector = new PlannedCardSelector(choices);
            selector.CaptureBefore(player);
            using (CardSelectCmd.PushSelector(selector, localOnly: true))
            {
                if (!card.TryManualPlay(null)) throw new Exception("Native callback choice card was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(TimeSpan.FromSeconds(20));
                selector.ReconcileImplicitChoices(player);
                selector.AssertConsumed();
            }
            runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureActual(combat, player, enemy),
                "NativeCallbackChoice", "NativeManualPlay");
            AssertNativeScalarModels(next.Simulator, combat);
            using var request = Request();
            if (request.RootElement.TryGetProperty("hextechNativeDiscardQueueProbe", out var queueProbe) && queueProbe.GetBoolean())
                AssertNativeDiscardModels(next.Simulator, combat);
            GD.Print($"HEXTECH_NATIVE_CALLBACK_CHOICE_VERIFIED card={cardId} choices={choices.Count} full_native_snapshots=true rng=true full_action_replay=true incomplete_continuation_rejected=true parent=true sibling=true live=true");
            return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
        }
        finally { next?.ReleaseSimulator(); }
    }
}
