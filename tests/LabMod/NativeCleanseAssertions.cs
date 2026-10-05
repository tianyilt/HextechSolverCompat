using CombatSolver;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeCleanseDampen(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var player = scenario.Player;
        if (!player.PlayerCombatState!.AllCards.Any(card => card.IsUpgraded))
            throw new Exception("Native Dampen cleanse must have actual upgraded cards.");
        var dampen = await PowerCmd.Apply<DampenPower>(new BlockingPlayerChoiceContext(), player.Creature, 1,
            scenario.CombatState.Enemies.First(), null) ?? throw new Exception("Native Dampen setup failed.");
        dampen.AddCaster(scenario.CombatState.Enemies.First());
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.PlayerCombatState.AllCards.Any(card => card.IsUpgraded))
            throw new Exception("Native Dampen did not downgrade the actual fixture cards.");
        VerifyNativeTokenFork(scenario);
        var result = await VerifyNativeTokenActual(runner, scenario);
        if (player.Creature.GetPower<DampenPower>() is not null
            || !player.PlayerCombatState.AllCards.Any(card => card.IsUpgraded))
            throw new Exception("Native cleanse did not remove Dampen and restore actual upgrades.");
        GD.Print("HEXTECH_NATIVE_CLEANSE_DAMPEN_VERIFIED native_add_caster=true native_downgrade=true native_restore=true branch_cards=true fork=true full_snapshots=true rng=true");
        return result;
    }
}
