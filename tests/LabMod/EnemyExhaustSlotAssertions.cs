using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyEnemyExhaustSlots(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var liveModifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        var modifier = ((SimulatedCombatState)parent.State.CombatState).Modifiers.OfType<HextechMayhemModifier>().Single();
        var type = AccessTools.TypeByName("HextechSolverCompat.EnemyState");
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type);
        HashSet<ulong> Counts(CombatPredictionSimulator simulator, string field) => (HashSet<ulong>)
            AccessTools.Field(type, field).GetValue(get.Invoke(null, [simulator, modifier]))!;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator simulator) => driver.BuildStateKey(root.StartTurnNumber,
            simulator.State.GetCreature(player.Creature), simulator.State.GetPlayerCombatState(player),
            (SimulatedCombatState)simulator.State.CombatState, simulator, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent), liveStamp = ContinuationStamp.CaptureLive(combat).StateText;
        foreach (var (field, liveCounts) in new[] {
            ("ExhaustFirst", liveModifier.CombatTracking.EightPennyGatePlayersTriggeredThisTurn),
            ("ExhaustSecond", liveModifier.CombatTracking.EightPennyGatePlayersTriggeredSecondThisTurn) })
        {
            var child = parent.Fork();
            var counts = Counts(child, field);
            if (ReferenceEquals(counts, Counts(parent, field)) || ReferenceEquals(counts, Counts(sibling, field)))
                throw new Exception("Enemy exhaust slots are shared between branches.");
            if (!counts.Add(player.NetId)) counts.Remove(player.NetId);
            if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Key(sibling) != key
                || Stamp(parent) != stamp || Stamp(sibling) != stamp)
                throw new Exception("Enemy exhaust slot omitted search/continuation identity or leaked state.");
            bool had = liveCounts.Contains(player.NetId);
            try
            {
                if (had) liveCounts.Remove(player.NetId); else liveCounts.Add(player.NetId);
                if (Key(parent) != key || Stamp(parent) != stamp || ContinuationStamp.CaptureLive(combat).StateText == liveStamp)
                    throw new Exception("Enemy exhaust slot root reads live state or live identity omits the slot.");
            }
            finally
            {
                if (had) liveCounts.Add(player.NetId); else liveCounts.Remove(player.NetId);
            }
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != liveStamp)
            throw new Exception("Enemy exhaust slot probe changed live state.");
        GD.Print("HEXTECH_ENEMY_EXHAUST_SLOT_VERIFIED first=true second=true full_key=true continuation=true fork=true live_frozen=true");
    }
}
