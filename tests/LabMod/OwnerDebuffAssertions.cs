using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyOwnerDebuffs(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        bool courage = mode.StartsWith("courage", StringComparison.Ordinal);
        var target = courage ? combat.Enemies.Single() : player.Creature;
        var enemy = combat.Enemies.Single();
        if (mode is "artifact" or "courage-artifact") await HextechPowerCmdCompat.Apply<ArtifactPower>(target, 1, target, null, true);
        if (mode == "courage-captured") await HextechPowerCmdCompat.Apply<WeakPower>(target, 1, player.Creature, null, true);
        if (mode == "vicious") await HextechPowerCmdCompat.Apply<ViciousPower>(target, 1, target, null, true);
        var root = CombatRootSnapshot.Capture(combat);
        int initialBlock = player.Creature.Block;
        int initialStrength = player.Creature.GetPowerAmount<StrengthPower>();
        int initialHp = player.Creature.CurrentHp;
        var simulator = root.ForkSimulator();
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string original = Stamp(simulator);
        static StateFingerprint Key(CombatPredictionSimulator s)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)s.State.CombatState).AppendFingerprint(ref builder, s);
            return builder.Finish();
        }
        var originalKey = Key(simulator);
        var childCombat = (SimulatedCombatState)child.State.CombatState;
        childCombat.ApplyPowerFromSource(typeof(WeakPower), target, 1, courage ? player.Creature : enemy, null);
        PowerLifecycleSupport.ResolvePowerAmountChanges(child, childCombat);
        if (Key(child) == originalKey || Key(simulator) != originalKey || Key(sibling) != originalKey ||
            Stamp(child) == original || Stamp(simulator) != original || Stamp(sibling) != original ||
            ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new Exception("Owner-debuff probe changed a parent/sibling/live state or omitted its continuation.");
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        async Task Apply<T>(int amount, bool self) where T : PowerModel
        {
            var applier = self ? target : courage ? player.Creature : enemy;
            if (typeof(T) == typeof(HextechTemporaryStrengthPower))
                shadow.ApplyTemporaryStrengthGain<HextechTemporaryStrengthPower>(target, amount, applier);
            else shadow.ApplyPowerFromSource(typeof(T), target, amount, applier, null);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            await HextechPowerCmdCompat.Apply<T>(target, amount, applier, null, true);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechOwnerDebuff", typeof(T).Name);
        }
        switch (mode)
        {
            case "courage-repeat":
            case "courage-captured":
            case "courage-round":
            case "repeat":
                for (int i = 0; i < 5; i++) await Apply<WeakPower>(1, false);
                // Removing Weak must not trigger owner-debuff listeners.
                // The subsequent Strength is a positive owner buff and does
                // trigger the 0.9.7 Adamant listener for immediate block.
                await Apply<WeakPower>(-1, false);
                await Apply<StrengthPower>(1, true);
                if (mode == "courage-round")
                {
                    if (player.Creature.GetPower<PlatingPower>()?.Amount != 6)
                        throw new Exception("Courage round requires six native plating before player turn reset.");
                    await runner.AssertReportRoundAsync(combat, player);
                    if (player.Creature.GetPower<PlatingPower>()?.Amount != 5)
                        throw new Exception("Native player plating did not decrement once at next turn start.");
                    simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
                    shadow = (SimulatedCombatState)simulator.State.CombatState;
                    await Apply<WeakPower>(1, false);
                }
                break;
            case "courage-artifact":
            case "artifact":
                await Apply<WeakPower>(1, false);
                if (player.Creature.Block != initialBlock || player.Creature.GetPowerAmount<StrengthPower>() != initialStrength
                    || player.Creature.CurrentHp != initialHp || target.GetPowerAmount<ArtifactPower>() != 0)
                    throw new Exception("Artifact must consume exactly once and prevent owner-debuff rewards beyond its setup buff.");
                break;
            case "temporary": await Apply<HextechTemporaryStrengthPower>(1, true); break;
            case "vicious":
                for (int i = 0; i < 5; i++) await Apply<VulnerablePower>(1, true);
                break;
            case "round":
                await Apply<WeakPower>(1, false);
                await runner.AssertReportRoundAsync(combat, player);
                GD.Print("HEXTECH_OWNER_DEBUFF_VERIFIED mode=round native_round=true");
                return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
            default: throw new Exception("Unknown owner-debuff probe mode: " + mode);
        }
        var fork = simulator.Fork();
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
            UnattendedTestRunner.CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
            "HextechOwnerDebuff", "Fork");
        GD.Print($"HEXTECH_OWNER_DEBUFF_VERIFIED mode={mode} branch_isolated=true fingerprint=true continuation=true live_unchanged=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
