using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static int _natureWorkerArmed, _natureWorkerEntered, _natureDeploymentArmed, _natureDeploymentStarts;
    private static int _natureMainThread;

    // Fault injection is test-only and one-shot: stall the actual Task.Run worker
    // until its own cancellation, without changing the native timer or heal.
    private static void StallNativeNatureWorker()
    {
        if (System.Environment.CurrentManagedThreadId == _natureMainThread) return;
        if (Interlocked.Exchange(ref _natureWorkerArmed, 0) == 0) return;
        var cancellationToken = SolverController._search?.Cancellation.Token
            ?? throw new Exception("Native search worker has no cancellation session.");
        Volatile.Write(ref _natureWorkerEntered, 1);
        if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
            throw new Exception("Injected native worker stall was not canceled by the real Nature tick.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void DelayFirstNativeNatureDeployment(ref SolverSettingsSnapshot deploymentSettings)
    {
        Interlocked.Increment(ref _natureDeploymentStarts);
        if (Interlocked.Exchange(ref _natureDeploymentArmed, 0) != 0)
            deploymentSettings = deploymentSettings with { DeploymentInterActionDelaySeconds = 12d };
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeNatureInterruption(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        if (mode is not ("worker" or "deployment")) throw new Exception("Unknown Nature interruption probe.");
        var combat = scenario.CombatState; var player = scenario.Player; var host = NGame.Instance!;
        var bridge = AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge");
        var effect = AccessTools.Field(bridge, "NativeNatureEffect").GetValue(null)!;
        var timer = (Godot.Timer)AccessTools.Field(effect.GetType(), "_timer").GetValue(effect)!;
        if (!timer.IsInsideTree() || timer.IsStopped() || timer.Paused || timer.WaitTime != 5d)
            throw new Exception("Interruption probe requires the original running tier-three timer.");
        var preferences = SolverSettings.Capture();
        var table = AccessTools.Field(bridge, "NativeNatureEventStates").GetValue(null)!;
        var events = AccessTools.Method(table.GetType(), "GetOrCreateValue").Invoke(table, [combat])!;
        int Read(string name) => (int)AccessTools.Field(events.GetType(), name).GetValue(events)!;
        async Task WaitFor(Func<bool> done, int seconds = 12)
        {
            long until = System.Environment.TickCount64 + seconds * 1000;
            while (!done())
            {
                if (System.Environment.TickCount64 > until) throw new Exception($"Native Nature {mode} boundary timed out.");
                await runner.NextFrameAsync();
            }
        }
        var root = CombatRootSnapshot.Capture(combat); var parent = root.ForkSimulator(); var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string frozen = Stamp(parent);
        var harmony = new Harmony("HextechCompatLab.NatureInterruption");
        var solve = AccessTools.DeclaredMethod(typeof(CombatBeamSolver), "BuildStateKey");
        var deploy = AccessTools.DeclaredMethod(typeof(SolverController), "DeployCurrentTurn");
        _natureWorkerArmed = mode == "worker" ? 1 : 0; _natureWorkerEntered = 0;
        _natureMainThread = System.Environment.CurrentManagedThreadId;
        _natureDeploymentArmed = mode == "deployment" ? 1 : 0; _natureDeploymentStarts = 0;
        if (mode == "worker") harmony.Patch(solve, prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(StallNativeNatureWorker)));
        else harmony.Patch(deploy, prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(DelayFirstNativeNatureDeployment)));
        try
        {
            SolverController.BeginCombat(combat);
            SolverController.SetAutomaticCalculationEnabled(false, persist: false);
            // Use a naturally fresh timer interval; root capture and initial
            // advice may have consumed the first interval during loading.
            await WaitFor(() => timer.TimeLeft > 3d && !(bool)AccessTools.Field(effect.GetType(), "_healing").GetValue(effect)!);
            int epoch = Read("Epoch"), replans = Read("Replans");
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            CancellationToken token; Task operation;
            if (mode == "worker")
            {
                await WaitFor(() => Volatile.Read(ref _natureWorkerEntered) != 0);
                var active = SolverController._search ?? throw new Exception("Stalled worker lost its live controller session.");
                token = active.Cancellation.Token; operation = active.WorkerCompletion;
                if (operation.IsCompleted || !SolverController.IsSearching) throw new Exception("Injected stall did not hold a real running worker.");
            }
            else
            {
                await WaitFor(() => !SolverController.IsSearching && SolverController.CanExecuteCurrentTurn
                    && timer.TimeLeft > 3d && !(bool)AccessTools.Field(effect.GetType(), "_healing").GetValue(effect)!);
                epoch = Read("Epoch"); replans = Read("Replans");
                var result = SolverController.CurrentResultForBugReport!;
                if (result.BestNode.Actions.Count(action => action.Turn == result.StartTurnNumber && action.IsExecutable) < 2)
                    throw new Exception("Native deployment probe did not obtain a real multi-action route.");
                SolverController.StartDeployment(host, combat, result);
                var active = SolverController._deployment ?? throw new Exception("Native deployment did not create a session.");
                token = active.Cancellation.Token; operation = active.Operation;
                await WaitFor(() => player.PlayerCombatState!.Hand.Cards.Count < 3
                    && MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null
                    && !operation.IsCompleted);
            }
            GD.Print($"HEXTECH_NATIVE_NATURE_INTERRUPTION_ACTIVE mode={mode} real_session=true injected_stall=true original_timer=true");
            var enemies = combat.Enemies.Where(enemy => enemy.IsAlive).ToArray();
            var hp = enemies.Select(enemy => enemy.CurrentHp).ToArray();
            int turn = player.PlayerCombatState!.TurnNumber;
            await WaitFor(() => Read("Epoch") > epoch && !(bool)AccessTools.Field(effect.GetType(), "_healing").GetValue(effect)!);
            if (!token.IsCancellationRequested) throw new Exception("Original Nature heal failed to cancel the actual active session.");
            await WaitFor(() => operation.IsCompleted);
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i].CurrentHp != hp[i] + 1) throw new Exception($"Actual Nature timer HP mismatch: before={hp[i]} after={enemies[i].CurrentHp} expected={hp[i]+1} deployments={_natureDeploymentStarts} epoch={Read("Epoch")-epoch} operation={operation.Status} turn={player.PlayerCombatState!.TurnNumber}.");
            if (Stamp(parent) != frozen || Stamp(sibling) != frozen)
                throw new Exception("Actual native gameplay/timer changed frozen search branches.");
            if (mode == "worker")
                await WaitFor(() => Read("Replans") > replans && !SolverController.IsSearching && SolverController.CanExecuteCurrentTurn);
            else
                await WaitFor(() => Read("Replans") > replans && _natureDeploymentStarts >= 2
                    && player.PlayerCombatState!.TurnNumber > turn && !SolverController.IsDeploying
                    && player.PlayerCombatState.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play
                    && player.PlayerCombatState.Hand.Cards.Any(card => card.Id.Entry == "DEFEND_SILENT")
                    && MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted, 15);
            if (SolverSettings.Capture() != preferences || SolverController.FullAutoEnabled
                || timer.WaitTime != 5d || timer.Paused || timer.IsStopped())
                throw new Exception("Nature interruption changed saved settings, automation or the original timer.");
            GD.Print($"HEXTECH_NATIVE_NATURE_INTERRUPTION_VERIFIED mode={mode} actual_session_canceled=true old_operation_finished=true native_replan=true frozen_forks=true preferences_unchanged=true");
            SolverController.CancelSearch(); SolverController.CancelDeployment();
            VerifyNativeTokenFork(scenario);
            var outcome = await VerifyNativeTokenActual(runner, scenario);
            GD.Print("HEXTECH_NATIVE_NATURE_VERIFIED real_tick=true active_session_interruption=true native_card_snapshots=true");
            return outcome;
        }
        finally
        {
            _natureWorkerArmed = _natureDeploymentArmed = 0;
            SolverController.CancelSearch(); SolverController.CancelDeployment();
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
