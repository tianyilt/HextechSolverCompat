using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeNearDeath(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var actual = scenario.CombatState; var player = scenario.Player; var enemy = actual.Enemies.First();
        bool playerTarget = mode == "player"; Creature target = playerTarget ? player.Creature : enemy;
        var modifier = actual.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault()!;
        var root = CombatRootSnapshot.Capture(actual); var sim = root.ForkSimulator();
        var shadow = (SimulatedCombatState)sim.State.CombatState;
        var rune = player.Relics.OfType<NearDeathFeastRune>().SingleOrDefault();
        uint id = target.CombatId!.Value;
        HextechMayhemCombatTrackingState PredictedTracking(CombatPredictionSimulator state)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat").GetType("HextechSolverCompat.EnemyState", true)!;
            var owned = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type).Invoke(null, [state, ((SimulatedCombatState)state.State.CombatState).Modifiers.OfType<HextechMayhemModifier>().Single()])!;
            var turns = AccessTools.Property(type, "NativeTurns").GetValue(owned)!;
            return (HextechMayhemCombatTrackingState)AccessTools.Field(turns.GetType(), "Model").GetValue(turns)!;
        }
        var processedDeaths = new HashSet<uint>();
        void Compare(string step)
        {
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, shadow, shadow.KnownEnemies, processedDeaths))
                throw new Exception("Native near-death fixture unexpectedly paused in deferred death settlement.");
            sim.SynchronizePowerAmountPredictionStates();
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(sim, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(actual, player, enemy), "HextechNativeNearDeath", step);
            AssertNativeScalarModels(sim, actual);
            if (!playerTarget)
            {
                var owned = PredictedTracking(sim);
                foreach (var pair in new[] { (owned.NearDeathFeastEnemyDebt, modifier.CombatTracking.NearDeathFeastEnemyDebt),
                    (owned.NearDeathFeastEnemyStrength, modifier.CombatTracking.NearDeathFeastEnemyStrength) })
                    if (pair.Item1.Count != pair.Item2.Count || pair.Item1.Any(kv => pair.Item2.GetValueOrDefault(kv.Key, -1) != kv.Value))
                        throw new Exception("Native near-death debt/strength tracking differs at " + step);
            }
        }
        string Stamp(CombatPredictionSimulator state) => ContinuationStamp.CapturePredicted(player, state,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(actual), BattleDamageTracker.Observe(actual),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), actual, false, null));
        var buildKey = AccessTools.Method(typeof(CombatBeamSolver), "BuildStateKey");
        StateFingerprint Key(CombatPredictionSimulator state) => (StateFingerprint)buildKey.Invoke(driver, [root.StartTurnNumber,
            state.State.GetCreature(player.Creature), state.State.GetPlayerCombatState(player), (SimulatedCombatState)state.State.CombatState,
            state, 0, new HashSet<uint>()])!;
        async Task Hit(decimal amount, string step)
        {
            sim.Damage(target, amount, ValueProp.Unpowered | ValueProp.Unblockable, playerTarget ? enemy : player.Creature);
            await HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), target, amount,
                ValueProp.Unpowered | ValueProp.Unblockable, playerTarget ? enemy : player.Creature, null);
            Compare(step);
        }
        Compare("root"); await Hit(6m, "enter-debt-1");
        if (target.CurrentHp != 1 || target.GetPowerAmount<StrengthPower>() != 1) throw new Exception("Fixture did not enter actual negative-HP debt.");
        var oldKey = Key(sim); string oldStamp = Stamp(sim), liveStamp = ContinuationStamp.CaptureLive(actual).StateText;
        int previousDebt = playerTarget ? rune!.SavedNearDeathDebt : modifier.CombatTracking.NearDeathFeastEnemyDebt[id];
        if (playerTarget) rune!.SavedNearDeathDebt++; else modifier.CombatTracking.NearDeathFeastEnemyDebt[id]++;
        var changed = CombatRootSnapshot.Capture(actual).ForkSimulator();
        if (Key(changed) == oldKey || Stamp(changed) == oldStamp) throw new Exception("Debt omitted from key or continuation.");
        if (playerTarget) rune!.SavedNearDeathDebt = previousDebt; else modifier.CombatTracking.NearDeathFeastEnemyDebt[id] = previousDebt;
        var child = sim.Fork(); var sibling = sim.Fork();
        child.Damage(target, 2m, ValueProp.Unpowered | ValueProp.Unblockable, player.Creature); child.SynchronizePowerAmountPredictionStates();
        if (Key(child) == oldKey || Stamp(child) == oldStamp || Key(sim) != oldKey || Stamp(sim) != oldStamp
            || Key(sibling) != oldKey || Stamp(sibling) != oldStamp || ContinuationStamp.CaptureLive(actual).StateText != liveStamp)
            throw new Exception("Near-death fork aliases parent, sibling or real game.");
        int hp = target.CurrentHp, block = target.Block;
        sim.Heal(target, 10m); await CreatureCmd.Heal(target, 10m);
        sim.GainBlock(target, 10m, ValueProp.Unpowered); await CreatureCmd.GainBlock(target, 10m, ValueProp.Unpowered, null);
        Compare("blocked-heal-and-block");
        if (target.CurrentHp != hp || target.Block != block) throw new Exception("Dying native creature gained HP or block.");
        await Hit(2m, "debt-3-strength-3");
        sim.State.GetCreature(target).CurrentHp = -4; await CreatureCmd.SetCurrentHp(target, -4);
        CombatSolver.Engine.InCombat.Mirrors.HookMirrors.AfterCurrentHpChanged(sim, target, -5m);
        Compare("direct-negative-setter");
        if (mode == "recover")
        {
            sim.State.GetCreature(target).CurrentHp = 20; await CreatureCmd.SetCurrentHp(target, 20);
            CombatSolver.Engine.InCombat.Mirrors.HookMirrors.AfterCurrentHpChanged(sim, target, 19m);
            Compare("recover-positive-clears-tracking");
            sim.Heal(target, 5m); await CreatureCmd.Heal(target, 5m); Compare("recovered-healing");
        }
        else if (mode == "kill")
        {
            sim.Kill(target); await CreatureCmd.Kill(target); Compare("explicit-kill-bypasses-debt");
            if (target.CurrentHp != 0) throw new Exception("Explicit kill left enemy dying alive.");
        }
        else if (!playerTarget)
        {
            int tier = modifier.GetMonsterHexStrengthTier(MonsterHexKind.NearDeathFeast);
            int limit = Math.Max(1, (int)Math.Floor(target.MaxHp * .05m * tier));
            await Hit(limit - 5, "one-below-death-limit");
            if (target.CurrentHp != 1) throw new Exception("Died below the native negative-HP threshold.");
            await Hit(1m, "exact-death-limit");
            if (target.CurrentHp != 0) throw new Exception("Did not die exactly at the native negative-HP threshold.");
        }
        GD.Print($"HEXTECH_NATIVE_NEAR_DEATH_VERIFIED mode={mode} native_snapshots=true debt_tracking=true strength_delta=true no_heal_block=true debt_key_stamp=true fork_isolation=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
