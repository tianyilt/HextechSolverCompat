using System.Collections;
using CombatSolver;
using Godot;
using HarmonyLib;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeDamageBoundary(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeOstyHp", out var hp))
        {
            var osty = scenario.Player.Osty ?? throw new Exception("Damage boundary fixture requires a live Osty.");
            osty.SetMaxHpInternal(hp.GetInt32());
            osty.SetCurrentHpInternal(hp.GetInt32());
            if (!osty.Powers.Any(power => power is MegaCrit.Sts2.Core.Models.Powers.DieForYouPower))
                throw new Exception("Osty fixture must restore the real damage redirection power after clearing powers.");
        }
        var counter = AccessTools.DeclaredField(typeof(HextechCombatHooks), "_nextActualDamageCommandId");
        long before = (long)counter.GetValue(null)!;
        var registries = new[] {
            AccessTools.DeclaredField(typeof(CompensationRune), "RunesWithPendingCompensation"),
            AccessTools.DeclaredField(typeof(PiercingThreadRune), "RunesWithPendingDamage") };
        object[][] original = registries.Select(field => ((IEnumerable)field.GetValue(null)!).Cast<object>().ToArray()).ToArray();
        object? enemy = AccessTools.DeclaredField(typeof(CompensationEnemyHex), "_effectWithPendingCompensation").GetValue(null);
        var slipperyRegistries = new[] { "SlipperyReductionsByCommand", "OstyRedirectSlipperyByCommand" }
            .Select(name => (ICollection)AccessTools.DeclaredField(typeof(HextechCombatHooks), name).GetValue(null)!).ToArray();
        if (slipperyRegistries.Any(registry => registry.Count != 0)) throw new Exception("Native Slippery root command queues are not quiescent.");
        VerifyNativeTokenFork(scenario);
        if (slipperyRegistries.Any(registry => registry.Count != 0)) throw new Exception("Prediction changed a native Slippery command queue.");
        if ((long)counter.GetValue(null)! != before
            || AccessTools.Field(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "_nativeDamageCommand").GetValue(null) is not null)
            throw new Exception("Prediction changed the native command counter or retained an owned command scope.");
        for (int i = 0; i < registries.Length; i++)
            if (!((IEnumerable)registries[i].GetValue(null)!).Cast<object>().ToHashSet(ReferenceEqualityComparer.Instance)
                .SetEquals(original[i])) throw new Exception("Prediction changed the original pending-damage registry.");
        if (!ReferenceEquals(enemy, AccessTools.DeclaredField(typeof(CompensationEnemyHex), "_effectWithPendingCompensation").GetValue(null)))
            throw new Exception("Prediction changed the original enemy compensation queue owner.");
        var result = await VerifyNativeTokenActual(runner, scenario);
        GD.Print("HEXTECH_NATIVE_DAMAGE_BOUNDARY_VERIFIED native_command_counter_unchanged=true live_registries_unchanged=true original_callbacks=true full_native_snapshots=true scoped_cleanup=true");
        return result;
    }

    private static void AssertNativeDamageWitness(CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Combat.CombatState combat, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        using var request = Request();
        if (!request.RootElement.TryGetProperty("hextechNativeDamageAfterPlay", out var expected)) return;
        var enemy = combat.Enemies.First();
        foreach (var property in expected.EnumerateObject())
        {
            int actual = property.Name switch
            {
                "enemyHp" => enemy.CurrentHp,
                "enemyBlock" => enemy.Block,
                "nextDamage" => enemy.GetPowerAmount<HextechNextTurnDamagePower>(),
                "playerHp" => player.Creature.CurrentHp,
                "playerBlock" => player.Creature.Block,
                _ => throw new Exception("Unknown pending-damage witness field: " + property.Name)
            };
            if (actual != property.Value.GetInt32()) throw new Exception($"Native damage mechanism was not exercised: {property.Name} expected={property.Value} actual={actual}.");
        }
        GD.Print("HEXTECH_NATIVE_DAMAGE_WITNESS_VERIFIED " + expected.GetRawText());
    }
}
