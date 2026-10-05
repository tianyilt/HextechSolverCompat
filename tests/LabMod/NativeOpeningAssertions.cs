using System.Text.Json;
using CombatSolver;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task ApplyNativeOpening(UnattendedTestRunner.ScenarioContext scenario, JsonElement settings)
    {
        var player = scenario.Player;
        // Obtain these only after the report builder has entered a playable
        // battle. In particular ToolsOfTheTrade's real initial discard must
        // not suspend the builder before it reconstructs the fixture hand.
        if (settings.TryGetProperty("relicIds", out var relicIds))
            foreach (var id in relicIds.EnumerateArray())
            {
                if (player.Relics.Any(r => r.Id.Entry == id.GetString()))
                    throw new Exception("Native opening fixture already holds its deferred opening relic.");
                await RelicCmd.Obtain(ModelDb.AllRelics.Single(r => r.Id.Entry == id.GetString()).ToMutable(), player);
            }
        if (settings.TryGetProperty("ostyHp", out var ostyHp))
        {
            var osty = player.Osty ?? throw new Exception("Native opening expects a real Osty.");
            osty.SetMaxHpInternal(settings.GetProperty("ostyMaxHp").GetInt32());
            osty.SetCurrentHpInternal(ostyHp.GetInt32());
        }
        foreach (var relic in player.Relics.OfType<HextechRelicBase>())
        {
            if (relic is not (BeginningAndEndRune or KeystoneHunterRune or FleshAndBoneRune
                or LingeringMightRune or ServantMasterRune or SummonForthRune or NineDragonPowerRune
                or HardBonesRune or GhostFormRune or GetExcitedRune)) continue;
            if (relic is LingeringMightRune lingering && settings.TryGetProperty("carriedStrength", out var strength))
                lingering.SavedLingeringBuff = string.Join('|', ModelDb.Power<StrengthPower>().Id.Category,
                    ModelDb.Power<StrengthPower>().Id.Entry, strength.GetInt32());
            await relic.BeforeCombatStart();
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (settings.TryGetProperty("roomEnteredRelicIds", out var enteredIds))
            foreach (var id in enteredIds.EnumerateArray())
                await player.Relics.Single(relic => relic.Id.Entry == id.GetString()).AfterRoomEntered(
                    player.RunState.CurrentRoom!);
        if (settings.TryGetProperty("expected", out var expected))
            foreach (var check in expected.EnumerateObject())
            {
                int actual = Observe(scenario, check.Name);
                if (actual != check.Value.GetInt32())
                    throw new Exception($"Original opening {check.Name}: expected={check.Value} actual={actual}.");
            }
        if (player.Relics.OfType<LingeringMightRune>().Any(r => r.SavedLingeringBuff.Length != 0))
            throw new Exception("Original carried buff was not consumed at opening.");
        GD.Print("HEXTECH_NATIVE_OPENING_APPLIED original_callbacks=true before_capture=true");
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeOpening(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement settings)
    {
        await ApplyNativeOpening(scenario, settings);
        using (var request = Request())
            if (request.RootElement.TryGetProperty("hextechNativeScalarProbe", out var scalar))
                VerifyNativeScalar(scenario, scalar);
        VerifyNativeTokenFork(scenario);
        var result = await VerifyNativeTokenActual(runner, scenario);
        int choices = 0;
        for (int round = 0; round < settings.GetProperty("rounds").GetInt32(); round++)
            choices += await VerifyChoiceRound(runner, scenario);
        if (settings.TryGetProperty("requireChoice", out var require) && require.GetBoolean() && choices == 0)
            throw new Exception("Native opening did not exercise the expected real turn-start choice.");
        GD.Print($"HEXTECH_NATIVE_OPENING_VERIFIED choices={choices} native_rounds=true fork=true full_snapshots=true");
        return result;
    }
}
