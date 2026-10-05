using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemyCoefficients(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var liveModifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var modifier = shadow.Modifiers.OfType<HextechMayhemModifier>().Single();
        decimal Multiplier(CombatPredictionSimulator sim) => ModifyDamageMirrors.InvokeMultiplicative(modifier,
            new ModifyDamageMirrorContext { Simulator = sim, Target = player.Creature, Dealer = enemy,
                Amount = 20m, Props = ValueProp.Move, CardSource = null, CardPlay = null });
        decimal frozen = Multiplier(simulator);
        int floor = liveModifier.SavedMonsterHexStrengthTierFloor;
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        try
        {
            liveModifier.SavedMonsterHexStrengthTierFloor = floor == 3 ? 1 : 3;
            if (Multiplier(simulator) != frozen)
                throw new Exception("Enemy coefficient read the live strength floor after capture.");
        }
        finally { liveModifier.SavedMonsterHexStrengthTierFloor = floor; }
        if (ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Enemy coefficient floor probe altered live combat.");
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string stamp = Stamp(simulator);
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        child.Damage(player.Creature, 20m, ValueProp.Move, enemy);
        if (Stamp(child) == stamp || Stamp(simulator) != stamp || Stamp(sibling) != stamp
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Enemy coefficient damage leaked branch or live state.");
        using (var request = Request())
        {
            if (request.RootElement.TryGetProperty("hextechCoefficientBranchMaxHp", out var branchHp))
            {
                var hpChild = simulator.Fork();
                hpChild.State.GetCreature(player.Creature).SetMaxHp(branchHp.GetInt32());
                decimal expected = request.RootElement.GetProperty("hextechCoefficientBranchMultiplier").GetDecimal();
                if (Multiplier(hpChild) != expected || Multiplier(simulator) != frozen || Multiplier(sibling) != frozen)
                    throw new Exception("Enemy coefficient used the wrong player's branch max HP.");
            }
        }
        void Compare(string stage) => runner.AssertSnapshotEqual(
            UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
            UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechEnemyCoefficients", stage);
        simulator.Damage(player.Creature, 20m, ValueProp.Move, enemy);
        await HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 20m, ValueProp.Move, enemy, null);
        Compare("EnemyPoweredDamage");
        simulator.Heal(enemy, 20m);
        await CreatureCmd.Heal(enemy, 20m);
        Compare("EnemyHealAndCap");
        simulator.GainBlock(enemy, 20m, ValueProp.Unpowered);
        await CreatureCmd.GainBlock(enemy, 20m, ValueProp.Unpowered, null);
        Compare("EnemyBlock");
        GD.Print("HEXTECH_ENEMY_COEFFICIENTS_VERIFIED native_damage_heal_block=true frozen_tier=true branches_isolated=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
