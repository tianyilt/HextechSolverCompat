using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeNatureTimer(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeNatureInterruptionProbe", out var interruption))
            return await VerifyNativeNatureInterruption(runner, scenario, interruption.GetString()!);
        var actual = scenario.CombatState; var player = scenario.Player;
        var host = NGame.Instance!;
        var bridge = AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge");
        bool playerTimer = request.RootElement.TryGetProperty("hextechNativePlayerNature", out var playerNature) && playerNature.GetBoolean();
        object effect = playerTimer ? player.Relics.OfType<NatureIsHealingRune>().Single()
            : AccessTools.Field(bridge, "NativeNatureEffect").GetValue(null)!;
        if (playerTimer) await ((NatureIsHealingRune)effect).BeforeCombatStart();
        var timer = AccessTools.Field(effect.GetType(), "_timer").GetValue(effect) as Godot.Timer
            ?? throw new Exception("Original Nature opening did not create a live timer.");
        double interval = request.RootElement.GetProperty("hextechNativeNatureInterval").GetDouble();
        if (!timer.IsInsideTree() || timer.IsStopped() || timer.OneShot || timer.WaitTime != interval || timer.Paused)
            throw new Exception("Original native Nature timer did not retain its real tier interval.");
        GD.Print($"HEXTECH_NATIVE_NATURE_TIMER_CREATED interval={interval} actual_node_name={timer.Name}");
        if (request.RootElement.TryGetProperty("hextechNativeNatureBudgetProbe", out var budgetProbe) && budgetProbe.GetBoolean())
        {
            var preferences = SolverSettings.Capture();
            var policy = SolverController.CaptureSearchPolicy(preferences, actual, false, null);
            int requested = request.RootElement.GetProperty("searchBudgetOverrideMilliseconds").GetInt32();
            if (policy.BudgetOverrideMilliseconds is not { } limited || limited <= 0 || limited >= requested
                || limited > interval * 500 || policy.EarlyTurnExplorationBudgetMilliseconds > limited
                || SolverSettings.Capture() != preferences)
                throw new Exception("Nature long request/scout budgets were not bounded by the next original timer interval.");
            GD.Print($"HEXTECH_NATIVE_NATURE_BUDGET_VERIFIED requested={requested} limited={limited} early_limit={policy.EarlyTurnExplorationBudgetMilliseconds} original_preferences_unchanged=true");
        }
        var table = AccessTools.Field(bridge, "NativeNatureEventStates").GetValue(null)!;
        var obtain = AccessTools.Method(table.GetType(), "GetOrCreateValue");
        object Events() => obtain.Invoke(table, [actual])!;
        int Read(string field) => (int)AccessTools.Field(Events().GetType(), field).GetValue(Events())!;
        async Task WaitFor(Func<bool> done, int seconds)
        {
            long end = System.Environment.TickCount64 + seconds * 1000;
            while (!done())
            {
                if (System.Environment.TickCount64 > end) throw new Exception("Native real-time Nature/replan boundary timed out.");
                await runner.NextFrameAsync();
            }
        }
        SolverController.BeginCombat(actual);
        SolverController.SetAutomaticCalculationEnabled(false, persist: false);
        SolverController.RequestSearch(host, actual, SearchReason.Manual);
        await WaitFor(() => !SolverController.IsSearching && SolverController.CanExecuteCurrentTurn, 10);
        GD.Print("HEXTECH_NATIVE_NATURE_INITIAL_ADVICE_READY");
        var root = CombatRootSnapshot.Capture(actual); var parent = root.ForkSimulator(); var sibling = parent.Fork();
        if (playerTimer)
        {
            var liveRune = (NatureIsHealingRune)effect;
            var rootRune = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(player).OfType<NatureIsHealingRune>().Single();
            var stateType = AccessTools.TypeByName("HextechSolverCompat.NativeRuneState");
            var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType);
            var state = get.Invoke(null, [parent, rootRune]);
            var detached = AccessTools.Field(stateType, "Model").GetValue(state);
            if (AccessTools.Field(effect.GetType(), "_timer").GetValue(detached) is not null
                || !ReferenceEquals(AccessTools.Field(effect.GetType(), "_timer").GetValue(liveRune), timer))
                throw new Exception("Player Nature retained a live timer in captured branch or changed the original timer.");
            GD.Print("HEXTECH_NATIVE_PLAYER_NATURE_DETACHED live_timer_retained=true branch_timer_null=true");
        }
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(actual), BattleDamageTracker.Observe(actual),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), actual, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string frozen = Stamp(parent); var frozenKey = Key(parent);
        var enemies = playerTimer ? new[] { player.Creature } : actual.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        var hp = enemies.Select(enemy => enemy.CurrentHp).ToArray();
        int epoch = Read("Epoch"), replans = Read("Replans");
        var oldRoute = SolverController.CurrentResultForBugReport;
        var oldStamp = SolverController._combat.LatestStamp;
        await WaitFor(() => Read("Epoch") > epoch && !(bool)AccessTools.Field(effect.GetType(), "_healing").GetValue(effect)!, (int)interval + 5);
        if (SolverController.CurrentResultForBugReport == oldRoute
            || SolverController._combat.LatestStamp == oldStamp && SolverController.CanExecuteCurrentTurn)
            throw new Exception("Native timer heal retained an executable stale result.");
        for (int i = 0; i < enemies.Length; i++)
            if (enemies[i].CurrentHp != Math.Min(hp[i] + 1, enemies[i].MaxHp))
                throw new Exception("Original real-time heal did not heal each current living enemy once.");
        if (Stamp(parent) != frozen || Stamp(sibling) != frozen || Key(parent) != frozenKey || Key(sibling) != frozenKey)
            throw new Exception("Wall-clock native heal changed a frozen parent or sibling prediction.");
        var fresh = CombatRootSnapshot.Capture(actual).ForkSimulator();
        if (Stamp(fresh) == frozen || Key(fresh) == frozenKey)
            throw new Exception("Post-tick root did not include the actual changed HP in stamp and search key.");
        await WaitFor(() => Read("Replans") > replans && !SolverController.IsSearching && SolverController.CanExecuteCurrentTurn, 10);
        GD.Print("HEXTECH_NATIVE_NATURE_FRESH_ADVICE_READY");
        if (SolverController.CurrentResultForBugReport == oldRoute || SolverController.FullAutoEnabled
            || timer.IsStopped() || timer.Paused || timer.WaitTime != interval)
            throw new Exception("Timer replan changed the user's automation state or native timer.");
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        GD.Print($"HEXTECH_NATIVE_NATURE_VERIFIED interval={interval} living_targets={enemies.Length} player_timer={playerTimer} real_tick=true frozen_forks=true changed_key_stamp=true old_route_rejected=true fresh_native_controller_search=true timer_unchanged=true full_native_card_snapshot=true");
        return outcome;
    }
}
