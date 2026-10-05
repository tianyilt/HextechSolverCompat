using CombatSolver;
using Godot;
using HarmonyLib;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeSweepingBlade(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        VerifyNativeTokenFork(scenario);
        var field = AccessTools.DeclaredField(typeof(SweepingBladeRune), "_activeContext");
        if (field.GetValue(scenario.Player.Relics.OfType<SweepingBladeRune>().Single()) is not null)
            throw new Exception("Prediction changed the live SweepingBlade card context.");
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        if (field.GetValue(scenario.Player.Relics.OfType<SweepingBladeRune>().Single()) is not null)
            throw new Exception("Native SweepingBlade card context was not cleared after play.");
        GD.Print("HEXTECH_NATIVE_SWEEPING_VERIFIED original_retarget=true original_power_replication=true original_doom_correction=true full_native_snapshots=true fork_key_stamp_rng=true");
        return outcome;
    }
    private static void AssertNativeSweepingWitness(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        using var request = Request();
        if (!request.RootElement.TryGetProperty("hextechNativeSweepingDooms", out var expected)) return;
        var dooms = combat.Enemies.Select(enemy => enemy.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.DoomPower>()).ToArray();
        if (!dooms.SequenceEqual(expected.EnumerateArray().Select(value => value.GetInt32())))
            throw new Exception("SweepingBlade must use each target's actual attack result for Doom, including Artifact blocking: " + string.Join(',', dooms));
        GD.Print("HEXTECH_NATIVE_SWEEPING_DOOM_VERIFIED " + string.Join(',', dooms));
    }
}
