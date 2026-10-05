using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyEnemyAttackCounter(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var liveModifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        var liveCounts = liveModifier.CombatTracking.PlayerAttackCardsPlayedThisTurn;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var modifier = ((SimulatedCombatState)parent.State.CombatState).Modifiers.OfType<HextechMayhemModifier>().Single();
        var type = AccessTools.TypeByName("HextechSolverCompat.EnemyState");
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type);
        Dictionary<ulong, int> Counts(CombatPredictionSimulator simulator) => (Dictionary<ulong, int>)
            AccessTools.Field(type, "PlayerAttacks").GetValue(get.Invoke(null, [simulator, modifier]))!;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator simulator) => driver.BuildStateKey(root.StartTurnNumber,
            simulator.State.GetCreature(player.Creature), simulator.State.GetPlayerCombatState(player),
            (SimulatedCombatState)simulator.State.CombatState, simulator, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent), liveStamp = ContinuationStamp.CaptureLive(combat).StateText;
        int count = Counts(parent).GetValueOrDefault(player.NetId);
        if (ReferenceEquals(Counts(parent), Counts(child)) || ReferenceEquals(Counts(child), Counts(sibling)))
            throw new Exception("Enemy attack counter dictionaries are shared between forks.");
        Counts(child)[player.NetId] = count + 1;
        if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Key(sibling) != key
            || Stamp(parent) != stamp || Stamp(sibling) != stamp || Counts(parent).GetValueOrDefault(player.NetId) != count)
            throw new Exception("Enemy attack count omitted state identity or leaked to parent/sibling.");
        bool hadLive = liveCounts.TryGetValue(player.NetId, out int saved);
        try
        {
            liveCounts[player.NetId] = saved + 1;
            if (Key(parent) != key || Stamp(parent) != stamp || ContinuationStamp.CaptureLive(combat).StateText == liveStamp)
                throw new Exception("Enemy attack counter root is not frozen or live continuation omitted it.");
        }
        finally
        {
            if (hadLive) liveCounts[player.NetId] = saved;
            else liveCounts.Remove(player.NetId);
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != liveStamp)
            throw new Exception("Enemy attack counter probe changed live state.");
        GD.Print("HEXTECH_ENEMY_ATTACK_COUNTER_VERIFIED full_key=true continuation=true parent=true sibling=true live_frozen=true");
    }
}
