using System.Text.Json;
using CombatSolver;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeOrbSetup(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement setup)
    {
        var player = scenario.Player;
        var queue = player.PlayerCombatState!.OrbQueue;
        foreach (var orb in queue.Orbs.ToArray())
        {
            _ = queue.Remove(orb);
            orb.RemoveInternal();
        }
        var names = setup.EnumerateArray().Select(value => value.GetString()!).ToArray();
        using var request = Request();
        bool nativeReactions = request.RootElement.TryGetProperty("hextechNativeOrbExpectedSetup", out var expectedSetup);
        if (names.Length > 12 || !nativeReactions && names.Length > queue.Capacity)
            throw new Exception("Orb setup exceeds its explicit native queue contract.");
        foreach (string name in names)
        {
            OrbModel orb = name switch
            {
                "Lightning" => ModelDb.Orb<LightningOrb>().ToMutable(),
                "Frost" => ModelDb.Orb<FrostOrb>().ToMutable(),
                "Dark" => ModelDb.Orb<DarkOrb>().ToMutable(),
                "Plasma" => ModelDb.Orb<PlasmaOrb>().ToMutable(),
                "Glass" => ModelDb.Orb<GlassOrb>().ToMutable(),
                _ => throw new Exception("Unknown explicit native orb setup: " + name)
            };
            await OrbCmd.Channel(new ThrowingPlayerChoiceContext(), orb, player);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var expected = nativeReactions
            ? expectedSetup.EnumerateArray().Select(value => value.GetString()!).ToArray() : names;
        if (queue.Orbs.Count != expected.Length || !queue.Orbs.Select(orb => orb.GetType().Name)
            .SequenceEqual(expected.Select(name => name + "Orb")))
            throw new Exception("Native orb setup did not produce the requested queue and order.");
        if (request.RootElement.TryGetProperty("hextechNativeScalarProbe", out var scalar)) VerifyNativeScalar(scenario, scalar);
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        GD.Print("HEXTECH_NATIVE_ORB_PASSIVE_SETUP_VERIFIED original_channel=true native_queue_order=true full_snapshots=true fork=true rng=true");
        return outcome;
    }
}
