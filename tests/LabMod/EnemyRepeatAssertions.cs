using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static bool _traceRepeatMoves;
    private static int _repeatNativeCalls;
    private static bool _traceCeremonialMoves;
    private static Creature? _ceremonialOwner;
    private static decimal _ceremonialStartStrength;
    private static decimal _ceremonialCommandGain;
    private static bool _ceremonialObserverInstalled;

    private static void InstallEnemyRepeatProbe(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(MoveState), nameof(MoveState.PerformMove)),
        prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(TraceEnemyRepeatNative)));
    private static void TraceEnemyRepeatNative()
    {
        if (_traceRepeatMoves) _repeatNativeCalls++;
    }
    private static void ObserveNativeCeremonialStrength(Creature target, decimal amount)
    {
        if (!_traceCeremonialMoves || AccessTools.Field(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "_simulator").GetValue(null) is not null) return;
        if (!ReferenceEquals(target, _ceremonialOwner)) throw new Exception("Ceremonial command targeted a different enemy.");
        _ceremonialCommandGain += amount;
    }
    private static void BeginEnemyRepeatNativeTrace(Creature enemy)
    {
        using var request = Request();
        _repeatNativeCalls = 0;
        _traceCeremonialMoves = request.RootElement.TryGetProperty("hextechNativeCeremonialProbe", out var flag) && flag.GetBoolean();
        _ceremonialOwner = enemy;
        _ceremonialStartStrength = enemy.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>();
        _ceremonialCommandGain = 0;
        if (_traceCeremonialMoves && !_ceremonialObserverInstalled)
        {
            new Harmony("HextechCompatLab.CeremonialCommandObserver").Patch(
                AccessTools.DeclaredMethod(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "ApplyNativeCeremonialStrength"),
                prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveNativeCeremonialStrength)));
            _ceremonialObserverInstalled = true;
        }

        _traceRepeatMoves = request.RootElement.TryGetProperty("hextechEnemyRepeatProbe", out _);
    }
    private static void CompleteEnemyRepeatNativeTrace(int turn)
    {
        if (_traceCeremonialMoves)
        {
            _traceCeremonialMoves = false;
            using var ceremonialRequest = Request();
            decimal gain = _ceremonialOwner!.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>() - _ceremonialStartStrength;
            if (turn == 1 && ceremonialRequest.RootElement.TryGetProperty("hextechNativeCeremonialExpectedFirstGain", out var expected)
                && _ceremonialCommandGain != expected.GetInt32()) throw new Exception("Ceremonial first move did not exercise its required tier/attack/repeat branch.");
            GD.Print($"HEXTECH_NATIVE_CEREMONIAL_VERIFIED turn={turn} native_command_gain={_ceremonialCommandGain} total_strength_delta={gain} native_action_completion=true");
        }
        if (!_traceRepeatMoves) return;
        _traceRepeatMoves = false;
        using var request = Request();
        var probe = request.RootElement.GetProperty("hextechEnemyRepeatProbe");
        if (probe.TryGetProperty("nativeCalls", out var calls))
        {
            int expected = calls[turn - 1].GetInt32();
            if (_repeatNativeCalls != expected)
                throw new Exception($"Native repeat was not exercised: turn={turn} expected={expected} actual={_repeatNativeCalls}.");
        }
        GD.Print($"HEXTECH_ENEMY_REPEAT_NATIVE_VERIFIED turn={turn} native_moves={_repeatNativeCalls}");
    }
    private static void VerifyEnemyRepeatRoot(UnattendedTestRunner.ScenarioContext scenario, JsonElement probe)
    {
        var live = scenario.CombatState;
        var enemy = live.Enemies.Single();
        object?[] args = [enemy.Monster!, null];
        bool native = (bool)AccessTools.Method(typeof(HextechCombatHooks), "ShouldRepeatJeweledGauntletMove")
            .Invoke(null, args)!;
        var mod = live.Modifiers.OfType<HextechMayhemModifier>().Single();
        var run = mod.ActiveRunState!;
        int roll = HextechStableRandom.Index(run, 100, "enemy-jeweled-gauntlet-repeat",
            enemy.CombatId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            live.RoundNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), enemy.Monster!.NextMove.Id);
        GD.Print($"HEXTECH_ENEMY_REPEAT_ROOT seed={run.Rng.StringSeed} act={run.CurrentActIndex} floor={run.TotalFloor} id={enemy.CombatId} round={live.RoundNumber} move={enemy.Monster.NextMove.Id} roll={roll} repeat={native}");
        if (probe.TryGetProperty("repeat", out var expected) && native != expected.GetBoolean())
            throw new Exception("Fixture did not exercise the requested native repeat decision.");
        string liveStamp = ContinuationStamp.CaptureLive(live).StateText;
        var root = CombatRootSnapshot.Capture(live);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(live), BattleDamageTracker.Observe(live),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null));
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(scenario.Player.Creature), sim.State.GetPlayerCombatState(scenario.Player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            scenario.Player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var parentKey = Key(parent);
        string parentStamp = Stamp(parent);
        var bridge = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.EnemyMoveRepeatBridge", true)!;
        bool Predicted(CombatPredictionSimulator sim)
        {
            var combat = (SimulatedCombatState)sim.State.CombatState;
            return (bool)AccessTools.Method(bridge, "ShouldRepeat")
                .Invoke(null, [sim, combat, combat.CurrentMonsterMove(enemy)])!;
        }
        if (Predicted(parent) != native || Predicted(child) != native || Predicted(sibling) != native)
            throw new Exception("Frozen repeat decision differs from the original native predicate.");
        // Identical HP/powers with a different frozen seed must remain distinct
        // search states. Mutate only the child's adapter state, then restore it.
        var assembly = bridge.Assembly;
        var stateType = assembly.GetType("HextechSolverCompat.EnemyState", true)!;
        var predictedModifier = ((SimulatedCombatState)child.State.CombatState).Modifiers.OfType<HextechMayhemModifier>().Single();
        var state = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType)
            .Invoke(null, [child, predictedModifier])!;
        var property = AccessTools.Property(stateType, "StableSeed");
        var seed = property.GetValue(state)!;
        var replacement = Activator.CreateInstance(seed.GetType(), run.Rng.StringSeed + "X", run.CurrentActIndex, run.TotalFloor)!;
        try
        {
            property.SetValue(state, replacement);
            if (Key(child) == parentKey || Stamp(child) == parentStamp
                || Key(parent) != parentKey || Key(sibling) != parentKey
                || Stamp(parent) != parentStamp || Stamp(sibling) != parentStamp)
                throw new Exception("Frozen repeat seed was omitted from keys/continuation or shared by forks.");
        }
        finally { property.SetValue(state, seed); }
        int tier = mod.SavedMonsterHexStrengthTierFloor;
        try
        {
            mod.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
            if (Predicted(child) != native) throw new Exception("Repeat re-read the changed live tier.");
        }
        finally { mod.SavedMonsterHexStrengthTierFloor = tier; }
        if (enemy.Monster is KnowledgeDemon)
        {
            var combat = (SimulatedCombatState)child.State.CombatState;
            int before = combat.GetKnowledgeDemonCurseCounter(enemy);
            combat.AdvanceKnowledgeDemonCurseCounter(enemy);
            if (((SimulatedCombatState)parent.State.CombatState).GetKnowledgeDemonCurseCounter(enemy) != before
                || ((SimulatedCombatState)sibling.State.CombatState).GetKnowledgeDemonCurseCounter(enemy) != before
                || Predicted(child)) throw new Exception("Knowledge repeat exclusion leaked or ignored the branch counter.");
        }
        if (ContinuationStamp.CaptureLive(live).StateText != liveStamp)
            throw new Exception("Repeat root probe changed the live combat.");
        GD.Print("HEXTECH_ENEMY_REPEAT_ROOT_VERIFIED native_predicate=true frozen_tier=true seed_key=true seed_continuation=true fork=true parent=true sibling=true live=true");
    }
}
