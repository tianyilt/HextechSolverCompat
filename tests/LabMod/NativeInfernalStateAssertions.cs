using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeInfernalStateIsolation(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var field = typeof(HextechInfernalDragonSoulPower).GetField("_triggeredThisTurn",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var actual = player.Creature.GetPower<HextechInfernalDragonSoulPower>()!;
        bool initial = (bool)field.GetValue(actual)!;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var key = Key(parent);
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var mutable = shadow.GetMutablePowerInstance(shadow.GetPower<HextechInfernalDragonSoulPower>(player.Creature)!);
        field.SetValue(mutable, !initial);
        if (Stamp(child) == before || Key(child) == key
            || Stamp(parent) != before || Stamp(sibling) != before || Key(parent) != key || Key(sibling) != key
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Infernal proc flag alone was omitted from key/stamp or mutated another branch/live.");
        string childStamp = Stamp(child);
        var childKey = Key(child);
        var grandchild = child.Fork();
        var grandCombat = (SimulatedCombatState)grandchild.State.CombatState;
        var grandPower = grandCombat.GetMutablePowerInstance(grandCombat.GetPower<HextechInfernalDragonSoulPower>(player.Creature)!);
        field.SetValue(grandPower, initial);
        if (Stamp(child) != childStamp || Key(child) != childKey || Stamp(grandchild) != before || Key(grandchild) != key)
            throw new Exception("Infernal child fork did not deep-copy private proc state.");
        try
        {
            field.SetValue(actual, !initial);
            if (Stamp(parent) != before || Key(parent) != key || Stamp(sibling) != before || Key(sibling) != key)
                throw new Exception("Captured Infernal proc flag read live state after snapshot.");
        }
        finally { field.SetValue(actual, initial); }
        if (ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Infernal live flag restoration changed continuation.");
        GD.Print("HEXTECH_NATIVE_INFERNAL_STATE_VERIFIED flag_only_key=true flag_only_stamp=true deep_fork=true frozen_live=true");
    }
}
