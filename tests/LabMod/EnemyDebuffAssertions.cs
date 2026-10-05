using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemyDebuffs(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        bool limited = mode.StartsWith("courage", StringComparison.Ordinal)
            || mode.StartsWith("slap", StringComparison.Ordinal) || mode.StartsWith("tormentor", StringComparison.Ordinal);
        Dictionary<uint, int> Counts(HextechMayhemModifier modifier) =>
            mode.StartsWith("tormentor", StringComparison.Ordinal) ? modifier.CombatTracking.TormentorProcsThisTurn
            : mode.StartsWith("slap", StringComparison.Ordinal) ? modifier.CombatTracking.SlapProcsThisTurn
            : modifier.CombatTracking.CourageProcsThisTurn;
        if (mode == "artifact") await HextechPowerCmdCompat.Apply<ArtifactPower>(enemy, 1, enemy, null, true);
        if (mode == "courage-artifact") await HextechPowerCmdCompat.Apply<ArtifactPower>(enemy, 1, enemy, null, true);
        if (mode == "slap-artifact") await HextechPowerCmdCompat.Apply<ArtifactPower>(enemy, 1, enemy, null, true);
        if (mode == "tormentor-artifact") await HextechPowerCmdCompat.Apply<ArtifactPower>(enemy, 1, enemy, null, true);
        if (mode is "courage-captured" or "slap-captured" or "tormentor-captured") await HextechPowerCmdCompat.Apply<WeakPower>(enemy, 1, player.Creature, null, true);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var child = simulator.Fork();
        var sibling = simulator.Fork();
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        static StateFingerprint Key(CombatPredictionSimulator s)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)s.State.CombatState).AppendFingerprint(ref builder, s);
            return builder.Finish();
        }
        string before = Stamp(simulator);
        var beforeKey = Key(simulator);
        if (limited)
        {
            // Only the transient counter differs: powers, HP, cards and turn
            // remain identical. This catches an omitted adapter state writer.
            var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
            var counts = Counts(modifier);
            uint id = enemy.CombatId!.Value;
            bool had = counts.TryGetValue(id, out int count);
            counts[id] = count + 1;
            var changedRoot = CombatRootSnapshot.Capture(combat).ForkSimulator();
            if (Key(simulator) != beforeKey || Stamp(simulator) != before || Key(sibling) != beforeKey || Stamp(sibling) != before)
                throw new Exception("Captured enemy proc counter read subsequent live state.");
            if (Key(changedRoot) == beforeKey || Stamp(changedRoot) == before)
                throw new Exception("Courage proc count omitted from search key or continuation.");
            if (had) counts[id] = count;
            else counts.Remove(id);
            if (ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Courage key probe failed to restore actual transient count.");
        }
        var childCombat = (SimulatedCombatState)child.State.CombatState;
        childCombat.ApplyPowerFromSource(typeof(WeakPower), enemy, 1, player.Creature, null);
        PowerLifecycleSupport.ResolvePowerAmountChanges(child, childCombat);
        if (Key(child) == beforeKey || Key(simulator) != beforeKey || Key(sibling) != beforeKey
            || Stamp(child) == before || Stamp(simulator) != before || Stamp(sibling) != before
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Enemy-debuff probe failed branch/key/continuation/live isolation.");
        async Task Apply<T>(int amount, bool self, Creature? recipient = null) where T : PowerModel
        {
            var target = recipient ?? enemy;
            var applier = self ? target : player.Creature;
            shadow.ApplyPowerFromSource(typeof(T), target, amount, applier, null);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            await HextechPowerCmdCompat.Apply<T>(target, amount, applier, null, true);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechEnemyDebuff", typeof(T).Name);
        }
        if (limited)
        {
            if (mode is "courage-artifact" or "slap-artifact" or "tormentor-artifact") await Apply<WeakPower>(1, false);
            else
            {
                for (int i = 0; i < 5; i++) await Apply<WeakPower>(1, false);
                await Apply<WeakPower>(-1, false);
                await Apply<StrengthPower>(-1, true);
                if (mode is "courage-round" or "slap-round" or "tormentor-round")
                {
                    await runner.AssertReportRoundAsync(combat, player);
                    var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
                    if (Counts(modifier).Count != 0)
                        throw new Exception("Native Courage counter did not reset at player turn start.");
                    simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
                    shadow = (SimulatedCombatState)simulator.State.CombatState;
                    await Apply<WeakPower>(1, false);
                }
            }
        }
        else if (mode is "card-only" or "no-source")
        {
            var source = player.PlayerCombatState!.Hand.Cards.Single();
            var cardBranch = simulator.Fork();
            var noCardBranch = simulator.Fork();
            var cardCombat = (SimulatedCombatState)cardBranch.State.CombatState;
            var noCardCombat = (SimulatedCombatState)noCardBranch.State.CombatState;
            cardCombat.ApplyPowerFromSource(typeof(StrengthPower), enemy, -1, null, source);
            noCardCombat.ApplyPowerFromSource(typeof(StrengthPower), enemy, -1, null, null);
            if (Key(cardBranch) == Key(noCardBranch) || Stamp(cardBranch) == Stamp(noCardBranch))
                throw new Exception("Pending card-source predicate omitted from key or continuation.");
            PowerLifecycleSupport.ResolvePowerAmountChanges(cardBranch, cardCombat);
            PowerLifecycleSupport.ResolvePowerAmountChanges(noCardBranch, noCardCombat);
            if (cardBranch.State.GetCreature(enemy).CurrentHp != Math.Min(enemy.MaxHp, enemy.CurrentHp + BadTasteEnemyHex.HealAmountFor(enemy.MaxHp))
                || noCardBranch.State.GetCreature(enemy).CurrentHp != enemy.CurrentHp
                || Stamp(simulator) != before || Stamp(sibling) != before
                || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Card-only/unsourced negative attribute reaction disagreed or leaked.");
            var cardSource = mode == "card-only" ? source : null;
            shadow.ApplyPowerFromSource(typeof(StrengthPower), enemy, -1, null, cardSource);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            await HextechPowerCmdCompat.Apply<StrengthPower>(enemy, -1, null, cardSource, true);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechEnemyDebuff", mode);
        }
        else if (mode == "reforged")
        {
            await Apply<StrengthPower>(-1, false);
            await Apply<StrengthPower>(-1, true);
            await Apply<StrengthPower>(2, true);
            await Apply<DexterityPower>(-1, false);
            await Apply<StrengthPower>(-1, false, player.Creature);
        }
        else if (mode == "artifact") await Apply<WeakPower>(1, false);
        else if (mode == "repeat")
        {
            for (int i = 0; i < 5; i++) await Apply<WeakPower>(1, false);
            await Apply<WeakPower>(-1, false); // Ordinary debuff duration loss does not heal.
            await Apply<StrengthPower>(-1, false); // External negative attribute change does heal.
            await Apply<StrengthPower>(-1, true); // Self expiration/reduction does not heal.
            await Apply<StrengthPower>(1, true); // Buff grant does not heal.
        }
        else throw new Exception("Unknown enemy-debuff probe: " + mode);
        GD.Print($"HEXTECH_ENEMY_DEBUFF_VERIFIED mode={mode} branch_isolated=true fingerprint=true continuation=true live_unchanged=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
