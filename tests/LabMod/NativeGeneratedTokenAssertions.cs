using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeGeneratedTokens(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeChemtechExpectFull", out var expectFull) && expectFull.GetBoolean()
            && scenario.Player.PotionSlots.Any(potion => potion is null))
            throw new Exception("Chemtech full-slot control did not actually retain a full potion belt.");
        if (request.RootElement.TryGetProperty("hextechNativeChemtechExpectedCount", out var expectedCount)
            && scenario.Player.Potions.Count() != Math.Min(scenario.Player.PotionSlots.Count, expectedCount.GetInt32()))
            throw new Exception("Chemtech did not actually procure the source-derived number of potions.");
        GD.Print("HEXTECH_NATIVE_GENERATED_TOKENS_VERIFIED original_card_body=true original_target_order=true full_snapshots=true fork_key_stamp_rng=true potion_slots_compared=true");
        return outcome;
    }

    private static void AssertNativeGeneratedPotionSlots(CombatPredictionSimulator simulator, CombatState combat, Player player)
    {
        using var request = Request();
        if (!request.RootElement.TryGetProperty("hextechNativeGeneratedTokensProbe", out var flag) || !flag.GetBoolean()) return;
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (shadow.PotionSlotCount(player) != player.PotionSlots.Count)
            throw new Exception("Native potion slot capacities differ.");
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            var predicted = shadow.GetPotionAtSlot(player, slot);
            var actual = player.GetPotionAtSlotIndex(slot);
            if (predicted?.Id != actual?.Id || predicted is not null && !ReferenceEquals(predicted.Owner, player))
                throw new Exception($"Native potion slot {slot} differs: predicted={predicted?.Id} actual={actual?.Id}.");
        }
        GD.Print("HEXTECH_NATIVE_GENERATED_POTION_SLOTS_VERIFIED stable_original_picker=true all_slots=true");
    }
}
