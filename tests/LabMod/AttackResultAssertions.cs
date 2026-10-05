using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyOverkillWithSurvivor(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemies = combat.Enemies.ToArray();
        if (enemies.Length != 2) throw new Exception("Overkill fixture requires exactly two enemies.");
        var target = enemies.Single(e => e.CurrentHp == 5);
        var survivor = enemies.Single(e => !ReferenceEquals(e, target));
        int survivorHp = survivor.CurrentHp;
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var card = player.PlayerCombatState!.Hand.Cards.Single();
        await runner.PlayHistorySensitiveFixtureCardAsync(simulator, shadow, combat, player, target, card, "HextechOverkill");
        // Compare the remaining enemy too: the standard per-target helper does
        // not include this creature's state in its own snapshot.
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, survivor),
            UnattendedTestRunner.CaptureActual(combat, player, survivor), "HextechOverkill", "Survivor");
        var fork = simulator.Fork();
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, survivor),
            UnattendedTestRunner.CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, survivor),
            "HextechOverkill", "Fork");
        if (target.CurrentHp != 0 || survivor.CurrentHp != survivorHp || !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            throw new Exception("Overkill fixture did not kill only its target while preserving combat.");
        GD.Print($"HEXTECH_OVERKILL_SURVIVOR_VERIFIED target_hp=0 survivor_hp={survivorHp} block={player.Creature.Block}");
        return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }

    private static bool _checkingAttackResults;
    private static readonly List<(int Total, int Overkill)> SimulatedAttackResults = [];
    private static readonly List<(int Total, int Overkill)> ActualAttackResults = [];

    private static void InstallAttackResultProbe(Harmony harmony)
    {
        // Observe the two real execution engines, rather than a small bridge
        // helper that can be inlined before the test-only patch is installed.
        harmony.Patch(AccessTools.Method(typeof(AttackCommand), nameof(AttackCommand.Execute), [typeof(PlayerChoiceContext)]),
            postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveActualAttackResults)));
        harmony.Patch(AccessTools.Method(typeof(CombatPredictionSimulator), "ExecuteAttack"),
            postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveSimulatedAttackResults)));
    }

    private static void BeginAttackResultProbe()
    {
        SimulatedAttackResults.Clear(); ActualAttackResults.Clear();
        _checkingAttackResults = true;
    }

    private static void ObserveActualAttackResults(ref Task<AttackCommand> __result)
    {
        if (!_checkingAttackResults) return;
        __result = CollectAttackResults(__result);
    }

    private static async Task<AttackCommand> CollectAttackResults(Task<AttackCommand> pending)
    {
        var command = await pending;
        foreach (var hit in command.Results)
            foreach (var result in hit) ActualAttackResults.Add((result.TotalDamage, result.OverkillDamage));
        return command;
    }

    private static void ObserveSimulatedAttackResults(AttackCommand attackCommand)
    {
        if (!_checkingAttackResults) return;
        foreach (var hit in attackCommand.Results)
            foreach (var result in hit) SimulatedAttackResults.Add((result.TotalDamage, result.OverkillDamage));
    }

    private static void VerifyAttackResults()
    {
        _checkingAttackResults = false;
        if (ActualAttackResults.Count == 0 || !SimulatedAttackResults.SequenceEqual(ActualAttackResults) ||
            ActualAttackResults.Sum(r => r.Overkill) <= 0)
            throw new Exception($"Attack result/overkill mismatch: simulated={string.Join(';', SimulatedAttackResults)}, actual={string.Join(';', ActualAttackResults)}");
        GD.Print($"HEXTECH_NATIVE_ATTACK_RESULTS_VERIFIED count={ActualAttackResults.Count} overkill={ActualAttackResults.Sum(r => r.Overkill)} total={ActualAttackResults.Sum(r => r.Total)}");
    }
}
