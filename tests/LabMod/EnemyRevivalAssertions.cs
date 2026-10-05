using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static bool _tracingRevivalHp;
    private static void InstallRevivalHpProbe(Harmony harmony) => harmony.Patch(
        AccessTools.PropertySetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "CurrentHp"),
        prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(TraceRevivalHp)));
    private static void TraceRevivalHp(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance, int value)
    {
        if (!_tracingRevivalHp || __instance.Monster is not TestSubject) return;
        GD.Print($"HEXTECH_REVIVAL_HP_TRACE before={__instance.CurrentHp} requested={value} "
            + string.Join(" | ", new System.Diagnostics.StackTrace().GetFrames().Take(12).Select(frame => frame.GetMethod()?.ToString())));
    }
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemyRevival(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        if (enemy.Monster is not TestSubject) throw new Exception("Revival fixture requires the actual TestSubject monster.");
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        uint id = enemy.CombatId!.Value;
        _tracingRevivalHp = true;
        for (int phase = 1; phase <= 2; phase++)
        {
            using (var nearRequest = Request())
                if (nearRequest.RootElement.TryGetProperty("hextechNativeNearRevivalProbe", out var nearPhase) && nearPhase.GetBoolean())
                {
                    await HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), enemy, enemy.CurrentHp,
                        MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, player.Creature, null);
                    if (enemy.CurrentHp != 1 || modifier.CombatTracking.NearDeathFeastEnemyDebt.GetValueOrDefault(id, -1) != 0)
                        throw new Exception("Revival fixture failed to exercise native dying debt before kill.");
                }
            await CreatureCmd.Kill(enemy);
            if (enemy.CurrentHp != 0 || enemy.Monster.NextMove.Id != "RESPAWN_MOVE")
                throw new Exception("Native kill did not actually enter TestSubject's pending revival.");
            using (var request = Request())
                if (request.RootElement.TryGetProperty("hextechEnemyRepeatProbe", out var repeatProbe))
                    VerifyEnemyRepeatRoot(scenario, repeatProbe.Clone());
            var root = CombatRootSnapshot.Capture(combat);
            var parent = root.ForkSimulator();
            var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
                sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
                (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
            string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
                player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            var key = Key(parent);
            string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
            var child = parent.Fork();
            var sibling = parent.Fork();
            var shadow = (SimulatedCombatState)child.State.CombatState;
            int floor = modifier.SavedMonsterHexStrengthTierFloor;
            try
            {
                modifier.SavedMonsterHexStrengthTierFloor = floor == 3 ? 1 : 3;
                shadow.ResolveReviveMove(child, enemy, "RESPAWN_MOVE");
            }
            finally { modifier.SavedMonsterHexStrengthTierFloor = floor; }
            if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Stamp(parent) != stamp
                || Key(sibling) != key || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Revival altered a parent, sibling or live state.");
            int expectedHp = (int)MegaCrit.Sts2.Core.Entities.Creatures.Creature.ScaleHpForMultiplayer(
                shadow.GetMonsterInt(enemy, phase == 1 ? "SecondFormHp" : "ThirdFormHp"),
                combat.Encounter, combat.Players.Count, combat.RunState.CurrentActIndex);
            if (child.State.GetCreature(enemy).MaxHp != expectedHp || child.State.GetCreature(enemy).CurrentHp != expectedHp)
                throw new Exception("Simulation replayed opening HP coefficients on TestSubject revival.");
            ((SimulatedCombatState)sibling.State.CombatState).ResolveReviveMove(sibling, enemy, "RESPAWN_MOVE");
            if (Key(sibling) != Key(child) || Stamp(sibling) != Stamp(child))
                throw new Exception("Revival read the mutated live tier or shared mutable monster state.");
            await runner.AssertReportRoundAsync(combat, player);
            int nativePhase = (int)AccessTools.Field(typeof(TestSubject), "_respawns").GetValue(enemy.Monster)!;
            if (!enemy.IsAlive || nativePhase != phase || modifier.CombatTracking.TestSubjectPhaseStartApplied.GetValueOrDefault(id) != 0)
                throw new Exception("Native TestSubject revival unexpectedly invoked the Osty-only callback.");
            if (modifier.HasActiveMonsterHex(MonsterHexKind.NearDeathFeast)
                && (modifier.CombatTracking.NearDeathFeastEnemyDebt.ContainsKey(id) || modifier.CombatTracking.NearDeathFeastEnemyStrength.ContainsKey(id)))
                throw new Exception("Near-death tracking survived native kill and phase recovery.");
            GD.Print($"HEXTECH_ENEMY_REVIVAL_PHASE phase={phase} hp={enemy.CurrentHp} max_hp={enemy.MaxHp} native_round=true fork_isolation=true opening_not_replayed=true");
        }
        GD.Print("HEXTECH_ENEMY_REVIVAL_VERIFIED phases=2 native_rounds=true full_key=true continuation=true frozen_tier=true live_unchanged=true native_opening_replay_count=0");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
