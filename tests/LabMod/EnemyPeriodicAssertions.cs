using CombatSolver;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeEnemyPeriodic(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        var combat = scenario.CombatState;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        if (combat.RoundNumber != 1) throw new Exception("Native periodic setup must start on round one.");
        // The upstream report builder clears all piles after actual combat
        // start. Reapply only the reviewed original opening callbacks to the
        // reconstructed fixture, before capturing any prediction root.
        modifier.CombatTracking.GlobalProcsThisCombat.Remove("round-once:SlimedBerserker:1");
        modifier.CombatTracking.GlobalProcsThisCombat.Remove("enemy-haunted-ship-opening");
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var reviewed = effects.Where(effect => effect is DivineInterventionEnemyHex or CerberusEnemyHex
            or LeafSlimeEnemyHex or SlimedBerserkerEnemyHex or MyteEnemyHex or HauntedShipEnemyHex);
        var context = new HextechEnemyHexContext(modifier);
        foreach (var effect in reviewed)
        {
            var kind = AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)();
            if (!modifier.HasActiveMonsterHex(kind)) continue;
            var callback = AccessTools.Method(effect.GetType(), "BeforePlayerSideTurnStart")
                .CreateDelegate<Func<HextechEnemyHexContext, ICombatState, IReadOnlyList<Creature>, Task>>(effect);
            await callback(context, combat, combat.PlayerCreatures.ToArray());
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (request.RootElement.TryGetProperty("hextechNativePeriodicOpeningExpected", out var opening))
            foreach (var item in opening.EnumerateObject())
            {
                int actual = scenario.Player.PlayerCombatState!.AllCards.Count(card => card.Id.Entry == item.Name);
                if (actual != item.Value.GetInt32()) throw new Exception($"Native periodic opening {item.Name}: expected={item.Value} actual={actual}.");
            }
        VerifyEnemyTurnRoot(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        if (request.RootElement.TryGetProperty("hextechNativePeriodicFinalExpected", out var final))
            foreach (var item in final.EnumerateObject())
                if (Observe(scenario, item.Name) != item.Value.GetInt32())
                    throw new Exception($"Native periodic final effect was not exercised: {item.Name}.");
        GD.Print($"HEXTECH_NATIVE_ENEMY_PERIODIC_VERIFIED tier={modifier.SavedMonsterHexStrengthTierFloor} original_opening_callbacks=true interval=true once_per_combat=true native_rounds=true frozen_counters=true");
        return outcome;
    }
}
