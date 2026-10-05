using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void TraceEnemyTurnSnapshots(UnattendedTestRunner.MoveStateSnapshot predicted,
        UnattendedTestRunner.MoveStateSnapshot actual, string monsterId, string moveId)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeSolidTimeProbe", out _))
        {
            string Canonical(System.Text.Json.JsonElement value) => value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => property.Name + ":" + Canonical(property.Value))) + "}",
                System.Text.Json.JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
                _ => value.GetRawText()
            };
            var left = System.Text.Json.JsonSerializer.SerializeToElement(predicted);
            var right = System.Text.Json.JsonSerializer.SerializeToElement(actual);
            foreach (var property in left.EnumerateObject())
            {
                string a = Canonical(property.Value), b = Canonical(right.GetProperty(property.Name));
                if (a != b && property.Name != "ExactContinuationState")
                    GD.Print("HEXTECH_NATIVE_SOLID_SNAPSHOT_DIFFERENCE field=" + property.Name + " predicted=" + a + " actual=" + b);
            }
        }
        if ((request.RootElement.TryGetProperty("hextechNativeGrowthProbe", out _)
            || request.RootElement.TryGetProperty("hextechNativeSolidTimeProbe", out _))
            && predicted.ExactContinuationState != actual.ExactContinuationState)
        {
            var left = predicted.ExactContinuationState; var right = actual.ExactContinuationState;
            int pos = 0;
            while (pos < Math.Min(left.Length, right.Length) && left[pos] == right[pos]) pos++;
            int start = Math.Max(0, pos - 130);
            GD.Print("HEXTECH_NATIVE_GROWTH_STATE_DIFFERENCE predicted=" + left.Substring(start, Math.Min(600, left.Length - start))
                + " actual=" + right.Substring(start, Math.Min(600, right.Length - start)));
        }
        if (!request.RootElement.TryGetProperty("hextechNativeEnemyTurnProbe", out var flag) || !flag.GetBoolean()) return;
        GD.Print($"HEXTECH_ENEMY_TURN_SNAPSHOT stage={moveId} predicted_hp={predicted.EnemyHp} actual_hp={actual.EnemyHp} predicted_block={predicted.EnemyBlock} actual_block={actual.EnemyBlock}");
    }

    private static void UnreviewedEnemyTurnPrefix() { }

    private static void VerifyEnemyTurnRoot(UnattendedTestRunner.ScenarioContext scenario)
    {
        var live = scenario.CombatState;
        var player = scenario.Player;
        var liveModifier = live.Modifiers.OfType<HextechMayhemModifier>().Single();
        string liveStamp = ContinuationStamp.CaptureLive(live).StateText;
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string frozen = Stamp(parent);
        void Invoke(CombatPredictionSimulator simulator, CombatSide side)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            BeforeSideTurnStartMirrors.Invoke(combat.Modifiers.OfType<HextechMayhemModifier>().Single(),
                new BeforeSideTurnStartMirrorContext { Simulator = simulator, Side = side,
                    Participants = side == CombatSide.Player ? [player.Creature] : combat.Enemies.ToArray() });
        }
        foreach (var side in new[] { CombatSide.Player, CombatSide.Enemy })
        {
            var baseline = parent.Fork();
            Invoke(baseline, side);
            var child = parent.Fork();
            int tier = liveModifier.SavedMonsterHexStrengthTierFloor;
            var enemy = live.Enemies.Single();
            int hp = enemy.CurrentHp, maxHp = enemy.MaxHp;
            var globalProcs = liveModifier.CombatTracking.GlobalProcsThisCombat.ToArray();
            var thresholdSets = new[] { liveModifier.CombatTracking.EscapePlanTriggered,
                liveModifier.CombatTracking.RepulsorTriggered, liveModifier.CombatTracking.DawnTriggered,
                liveModifier.CombatTracking.FeelTheBurnTriggered, liveModifier.CombatTracking.FeelTheBurnPending };
            var thresholdCopies = thresholdSets.Select(set => set.ToArray()).ToArray();
            string? thresholdKey = liveModifier.CombatTracking.LastEnemyThresholdTriggerKey;
            try
            {
                liveModifier.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
                enemy.SetMaxHpInternal(maxHp + 100);
                enemy.SetCurrentHpInternal(1);
                foreach (var item in globalProcs) liveModifier.CombatTracking.GlobalProcsThisCombat[item.Key] = 0;
                foreach (var set in thresholdSets) { set.Clear(); set.Add(enemy.CombatId!.Value); }
                liveModifier.CombatTracking.LastEnemyThresholdTriggerKey = "live mutation after captured root";
                Invoke(child, side);
                if (Stamp(child) != Stamp(baseline)) throw new Exception("Native enemy turn callback read live strength tier: side=" + side
                    + " " + new ContinuationStamp(Stamp(baseline)).DescribeFirstDifference(new(Stamp(child))));
            }
            finally
            {
                liveModifier.SavedMonsterHexStrengthTierFloor = tier;
                enemy.SetMaxHpInternal(maxHp);
                enemy.SetCurrentHpInternal(hp);
                for (int i = 0; i < thresholdSets.Length; i++)
                { thresholdSets[i].Clear(); thresholdSets[i].UnionWith(thresholdCopies[i]); }
                liveModifier.CombatTracking.LastEnemyThresholdTriggerKey = thresholdKey;
                liveModifier.CombatTracking.GlobalProcsThisCombat.Clear();
                foreach (var item in globalProcs) liveModifier.CombatTracking.GlobalProcsThisCombat.Add(item.Key, item.Value);
            }
            if (Stamp(parent) != frozen || Stamp(sibling) != frozen || ContinuationStamp.CaptureLive(live).StateText != liveStamp)
                throw new Exception("Native enemy turn callback leaked into a parent, sibling or live combat.");
        }
        var harmony = new Harmony("HextechCompatLab.UnreviewedEnemyTurn");
        var target = AccessTools.DeclaredMethod(typeof(SonataEnemyHex), "BeforePlayerSideTurnStart");
        var frozenPatch = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp;
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(UnreviewedEnemyTurnPrefix)));
            if (ContinuationStamp.CaptureLive(live).StateText == liveStamp
                || ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp != frozenPatch)
                throw new Exception("Unknown enemy turn patch did not invalidate live routes or changed frozen composition.");
            try
            {
                _ = CombatRootSnapshot.Capture(live);
                throw new Exception("Unknown native enemy turn patch was accepted.");
            }
            catch (PredictionUnsupportedException error) when (error.Message.Contains("Unreviewed native enemy turn composition")) { }
        }
        finally { harmony.Unpatch(target, HarmonyPatchType.Prefix, harmony.Id); }
        if (ContinuationStamp.CaptureLive(live).StateText != liveStamp) throw new Exception("Enemy turn probe did not restore native composition.");
        GD.Print("HEXTECH_NATIVE_ENEMY_TURN_ROOT_VERIFIED frozen_tier=true frozen_hp=true frozen_max_hp=true player_boundary=true enemy_boundary=true parent=true sibling=true live=true unknown_patch_rejected=true composition_frozen=true");
    }
}
