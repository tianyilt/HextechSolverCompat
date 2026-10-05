using CombatSolver;
using Godot;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeEnemyDebuffTurns(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        VerifyEnemyTurnRoot(scenario);
        VerifyNativeTokenFork(scenario);
        var result = await VerifyNativeTokenActual(runner, scenario);
        var modifier = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single();
        if (modifier.HasActiveMonsterHex(MonsterHexKind.Omega)
            && modifier.CombatTracking.GlobalProcsThisCombat.GetValueOrDefault("enemy-omega-disintegration") != 1)
            throw new Exception("Native Omega fixture did not cross its once-in-combat round-four boundary.");
        // Capture again after crossing the trigger, then exercise next-turn
        // branch callbacks and frozen live mutations against the native data.
        VerifyEnemyTurnRoot(scenario);
        GD.Print("HEXTECH_NATIVE_ENEMY_DEBUFF_TURNS_VERIFIED native_rounds=true native_sequential_commands=true once_in_combat=true post_trigger_fork=true frozen_counters=true full_snapshots=true rng=true");
        return result;
    }
}
