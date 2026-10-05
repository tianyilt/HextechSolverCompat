using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task VerifyNativeEndOnlyRound(UnattendedTestRunner runner, CombatState combat, Player player)
    {
        int turn = player.PlayerCombatState!.TurnNumber;
        var enemies = combat.Enemies.ToArray();
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        List<PlanCardChoice> choices = [];
        PlanAction Action() => new(PlanActionKind.EndTurn, turn, TurnStartChoices: choices.ToArray());
        var next = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [Action()], null, 0, null);
        int continuedBranches = 0;
        for (int attempt = 0; next.Simulator.HasPendingChoice; attempt++)
        {
            if (attempt >= 12) throw new Exception("Native end-only choice sequence exceeded its bound.");
            var pending = ((SimulatedCombatState)next.Simulator.State.CombatState).PendingTurnStartChoice
                ?? throw new Exception("Native end-only choice lost its request.");
            var spec = TurnStartChoiceSupport.BuildPendingSpec(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player);
            var choice = CardChoiceSupport.BuildAutomaticPolicyChoice(spec) with
            { SourceId = pending.SourceId, ContextId = pending.ContextId, Timing = pending.Timing };
            var continuation = next.Simulator.TakeExecutionContinuation();
            if (continuation is not null)
            {
                var alternatives = CardChoiceSupport.BuildChoices(spec, SolverDisplayNames.Capture(combat), 2, 2)
                    .Select(candidate => candidate with { SourceId = pending.SourceId, ContextId = pending.ContextId, Timing = pending.Timing }).ToArray();
                foreach (var candidate in alternatives)
                {
                    var forked = next.Simulator.ForkExecutionContinuation(continuation, out var copied);
                    var forkedCombat = (SimulatedCombatState)forked.State.CombatState;
                    forkedCombat.BeginActionChoices([candidate]);
                    try
                    {
                        forked.ResumeExecutionContinuation(copied);
                        if (!forked.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(forked, forkedCombat);
                    }
                    finally { forkedCombat.EndActionChoices(); }
                    var replayed = UnattendedTestRunner.InvokeForcedTerminalReplay(driver,
                        [new PlanAction(PlanActionKind.EndTurn, turn, TurnStartChoices: [.. choices, candidate])], null, 0, null);
                    try
                    {
                        foreach (var enemy in enemies)
                            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(forked, forkedCombat, player, enemy),
                                UnattendedTestRunner.CaptureSimulated(replayed.Simulator, (SimulatedCombatState)replayed.Simulator.State.CombatState, player, enemy),
                                "NativeLateBatchChoice", "ContinuationVsReplay");
                        if (ContinuationStamp.CapturePredicted(player, forked, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText
                            != ContinuationStamp.CapturePredicted(player, replayed.Simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText)
                            throw new Exception("Native late-batch continued choice stamp differs from full replay.");
                        continuedBranches++;
                    }
                    finally { replayed.ReleaseSimulator(); }
                }
            }
            choices.Add(choice);
            next.ReleaseSimulator();
            next = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [Action()], null, 0, null);
        }
        var selector = new PlannedCardSelector(choices);
        selector.CaptureBefore(player);
        try
        {
            var shadow = (SimulatedCombatState)next.Simulator.State.CombatState;
            var fork = next.Simulator.Fork();
            foreach (var enemy in enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(next.Simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                    "HextechNativeEndOnly", "FutureFork");
            using var nativeChoices = CardSelectCmd.PushSelector(selector, localOnly: true);
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var wait = Stopwatch.StartNew();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                if (wait.Elapsed.TotalSeconds > 20 || !CombatManager.Instance.IsInProgress)
                    throw new Exception("Native end-only round failed to reach the next living player turn.");
                await runner.NextFrameAsync();
            }
            foreach (var enemy in enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(next.Simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechNativeEndOnly", "AllEnemyFutureRound");
            selector.ReconcileImplicitChoices(player);
            AssertNativeScalarModels(next.Simulator, combat);
            if (choices.Count > 0) GD.Print($"HEXTECH_NATIVE_LATE_BATCH_CHOICES_VERIFIED selections={choices.Count} continued_branches={continuedBranches} original_native=true full_replay=true remaining_queue=true");
            AssertNativeGoldModels(next.Simulator, combat);
            AssertNativeGeneratedPotionSlots(next.Simulator, combat, player);
        }
        finally { next.ReleaseSimulator(); }
    }
}
