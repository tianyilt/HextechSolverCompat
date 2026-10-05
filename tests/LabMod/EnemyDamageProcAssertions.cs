using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemyDamageProcs(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        Task NativeHit() => HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), enemy,
            6m, ValueProp.Unpowered, player.Creature, null);
        if (mode == "blood-captured") await NativeHit();
        if (mode == "clown-existing") await HextechPowerCmdCompat.Apply<SlipperyPower>(enemy, 1, enemy, null, true);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var buildKey = AccessTools.Method(typeof(CombatBeamSolver), "BuildStateKey");
        StateFingerprint Key(CombatPredictionSimulator s)
        {
            // Use the actual solver key, including creature HP/block. The
            // combat-extension fingerprint alone intentionally omits these.
            return (StateFingerprint)buildKey.Invoke(driver, [root.StartTurnNumber,
                s.State.GetCreature(player.Creature), s.State.GetPlayerCombatState(player),
                (SimulatedCombatState)s.State.CombatState, s, 0, new HashSet<uint>()])!;
        }
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        var key = Key(simulator);
        string stamp = Stamp(simulator);
        var counts = mode.StartsWith("clown", StringComparison.Ordinal)
            ? modifier.CombatTracking.ClownCollegeProcsThisTurn : modifier.CombatTracking.BloodPactProcsThisTurn;
        uint id = enemy.CombatId!.Value;
        bool had = counts.TryGetValue(id, out int count);
        counts[id] = count + 1;
        var different = CombatRootSnapshot.Capture(combat).ForkSimulator();
        if (Key(different) == key || Stamp(different) == stamp)
            throw new Exception("Damage proc count omitted from key or continuation.");
        if (had) counts[id] = count; else counts.Remove(id);
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        child.Damage(enemy, 6m, ValueProp.Unpowered, player.Creature);
        child.SynchronizePowerAmountPredictionStates();
        if (Key(child) == key || Stamp(child) == stamp || Key(simulator) != key || Stamp(simulator) != stamp
            || Key(sibling) != key || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception($"Damage proc fork/key/continuation/live isolation failed: childKeyChanged={Key(child) != key}, childStampChanged={Stamp(child) != stamp}, parentKeySame={Key(simulator) == key}, parentStampSame={Stamp(simulator) == stamp}, siblingKeySame={Key(sibling) == key}, siblingStampSame={Stamp(sibling) == stamp}, liveSame={ContinuationStamp.CaptureLive(combat).StateText == live}, childBlock={child.State.GetCreature(enemy).Block}, rootBlock={simulator.State.GetCreature(enemy).Block}.");
        async Task Hit()
        {
            simulator.Damage(enemy, 6m, ValueProp.Unpowered, player.Creature);
            simulator.SynchronizePowerAmountPredictionStates();
            await NativeHit();
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechEnemyDamageProc", mode);
        }
        for (int i = 0; i < 5; i++) await Hit();
        if (mode == "blood-round")
        {
            await runner.AssertReportRoundAsync(combat, player);
            if (modifier.CombatTracking.BloodPactProcsThisTurn.Count != 0)
                throw new Exception("BloodPact counter did not reset at player turn start.");
            simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            shadow = (SimulatedCombatState)simulator.State.CombatState;
            await Hit();
        }
        GD.Print($"HEXTECH_ENEMY_DAMAGE_PROC_VERIFIED mode={mode} native_snapshots=true counter_key=true continuation=true forks_isolated=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
