using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeRounds(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, int rounds)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechInitialExpected", out var initial))
            foreach (var item in initial.EnumerateObject())
                if (Observe(scenario, item.Name) != item.Value.GetInt32())
                    throw new Exception($"Initial effect {item.Name} was not exercised.");
        int choices = 0;
        for (int i = 0; i < rounds; i++) choices += await VerifyChoiceRound(runner, scenario);
        if (request.RootElement.TryGetProperty("hextechMinimumRoundChoices", out var minimum)
            && choices < minimum.GetInt32())
            throw new Exception($"Round choices were not exercised: {choices} < {minimum.GetInt32()}.");
        GD.Print($"HEXTECH_NATIVE_ROUNDS_VERIFIED rounds={rounds} choices={choices} actual_turn={scenario.Player.PlayerCombatState!.TurnNumber} full_native_snapshots=true rng=true replay=true fork=true parent=true sibling=true live_isolated=true");
        return new(false, scenario.Player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }

    private static async Task<int> VerifyChoiceRound(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        int turn = player.PlayerCombatState!.TurnNumber;
        BeginNativeVakuuRound();
        var dualCommandsBefore = CaptureNativeDualCommands();
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(turn,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim, int atTurn) => ContinuationStamp.CapturePredicted(
            player, sim, atTurn, root.Forecast, root.StartTurnNumber).StateText;
        var parentKey = Key(parent);
        string parentStamp = Stamp(parent, turn);
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        void AssertIsolation()
        {
            AssertNativeDualCommandsUnchanged(dualCommandsBefore);
            if (Key(parent) != parentKey || Key(sibling) != parentKey
                || Stamp(parent, turn) != parentStamp || Stamp(sibling, turn) != parentStamp
                || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Round prediction changed a parent, sibling or live combat.");
        }
        List<PlanCardChoice> choices = [];
        SimulationSnapshot? next = null;
        try
        {
            // Discover each suspension from the same root. A pending choice is not
            // forkable; replay the complete explicit plan instead of bypassing it.
            for (int attempt = 0; attempt <= 16; attempt++)
            {
                next?.ReleaseSimulator();
                next = null;
                PlanAction end = new(PlanActionKind.EndTurn, turn, TurnStartChoices: choices.ToArray());
                next = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [end], null, 0, null);
                var shadow = (SimulatedCombatState)next.Simulator.State.CombatState;
                AssertIsolation();
                if (shadow.PendingKnowledgeDemonChoice is { } knowledge)
                {
                    if (attempt == 16) throw new Exception("Knowledge choice discovery exceeded its bounded limit.");
                    choices.Add(KnowledgeDemonChoiceSupport.BuildChoices(knowledge, SolverDisplayNames.Capture(combat))[0]);
                    continue;
                }
                if (shadow.PendingTurnStartChoice is not { } pending) break;
                if (attempt == 16) throw new Exception("Round choice discovery exceeded its bounded limit.");
                var spec = TurnStartChoiceSupport.BuildPendingSpec(next.Simulator, shadow, player);
                choices.Add(CardChoiceSupport.BuildAutomaticPolicyChoice(spec) with
                {
                    SourceId = pending.SourceId, ContextId = pending.ContextId, Timing = pending.Timing,
                });
            }
            var predicted = (SimulatedCombatState)next!.Simulator.State.CombatState;
            if (predicted.HasPendingChoice)
                throw new Exception("Round replay still has an unresolved choice.");
            var expected = UnattendedTestRunner.CaptureSimulated(next.Simulator, predicted, player, enemy);
            var fork = next.Simulator.Fork();
            runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureSimulated(fork,
                (SimulatedCombatState)fork.State.CombatState, player, enemy), "HextechChoiceRound", "Fork");
            if (Stamp(fork, next.Turn) != Stamp(next.Simulator, next.Turn))
                throw new Exception("Round fork continuation stamps differ.");
            PlanAction replayEnd = new(PlanActionKind.EndTurn, turn, TurnStartChoices: choices.ToArray());
            var replay = UnattendedTestRunner.InvokeForcedTerminalReplay(driver, [replayEnd], null, 0, null);
            try
            {
                runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureSimulated(replay.Simulator,
                    (SimulatedCombatState)replay.Simulator.State.CombatState, player, enemy), "HextechChoiceRound", "Replay");
                if (replay.StateKey != next.StateKey
                    || Stamp(replay.Simulator, replay.Turn) != Stamp(next.Simulator, next.Turn))
                    throw new Exception("Round replay state keys or continuation stamps differ.");
                AssertIsolation();
            }
            finally { replay.ReleaseSimulator(); }
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(NativeVakuuRoundDeadlineSeconds()));
            // The solver's native UI driver requires a visible selection
            // surface. Its headless differential selector supplies the same
            // explicit tokens through the game's original selection API.
            var selector = new PlannedCardSelector(choices);
            selector.CaptureBefore(player);
            using var selectionScope = CardSelectCmd.PushSelector(selector, localOnly: true);
            CombatManager.Instance.OnEndedTurnLocally();
            if (dualCommandsBefore is not null) VerifyOwnedDualForecast(parent);
            BeginEnemyRepeatNativeTrace(enemy);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            async Task AdvanceNativeRound()
            {
                while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!CombatManager.Instance.IsInProgress) throw new Exception("Round fixture ended combat.");
                    await runner.NextFrameAsync();
                }
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            await AdvanceNativeRound().WaitAsync(deadline.Token);
            CompleteEnemyRepeatNativeTrace(turn);
            CompleteNativeVakuuRound(scenario);
            selector.ReconcileImplicitChoices(player);
            selector.AssertConsumed();
            runner.AssertSnapshotEqual(expected, UnattendedTestRunner.CaptureActual(combat, player, enemy),
                "HextechChoiceRound", "NativeNextTurn");
            return choices.Count;
        }
        finally { next?.ReleaseSimulator(); }
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifySummonRunes(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement request)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var osty = player.Osty ?? throw new Exception("Summon fixture requires Necrobinder's initial Osty.");
        osty.SetMaxHpInternal(request.GetProperty("maxHp").GetInt32());
        osty.SetCurrentHpInternal(request.GetProperty("hp").GetInt32());
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        int[] amounts = request.GetProperty("amounts").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        foreach (int amount in amounts)
        {
            shadow.SummonOsty(child, player, amount);
            PowerLifecycleSupport.ResolvePowerAmountChanges(child, shadow);
            if (Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp
                || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Summon callback leaked to a parent/sibling or live combat.");
        }
        if ((amounts.Any(x => x > 0) && (Key(child) == key || Stamp(child) == stamp))
            || (amounts.All(x => x <= 0) && (Key(child) != key || Stamp(child) != stamp)))
            throw new Exception("Summon did not respect positive/zero amount or omitted key/continuation.");
        foreach (int amount in amounts)
        {
            await OstyCmd.Summon(new ThrowingPlayerChoiceContext(), player, amount, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, enemy),
            UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechSummon", "NativeOstyCommands");
        GD.Print($"HEXTECH_SUMMON_VERIFIED commands={amounts.Length} hp={player.Osty!.CurrentHp} max_hp={player.Osty.MaxHp} block={player.Creature.Block} full_key=true continuation=true parent=true sibling=true live=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
