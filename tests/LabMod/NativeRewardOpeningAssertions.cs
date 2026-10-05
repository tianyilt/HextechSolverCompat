using CombatSolver;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void InstallNativeRewardOpeningProbe(Harmony harmony) => harmony.Patch(
        AccessTools.DeclaredMethod(typeof(RunManager), "EnterRoomDebug"),
        prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(EnterNativeOpeningRoom)));
    private static void EnterNativeOpeningRoom(ref RoomType roomType)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeOpeningRoomType", out var requested))
            roomType = Enum.Parse<RoomType>(requested.GetString()!);
    }
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeHailOpening(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        var room = (CombatRoom)scenario.CombatState.RunState.CurrentRoom!;
        var expectedRoom = Enum.Parse<RoomType>(request.RootElement.GetProperty("hextechNativeOpeningRoomType").GetString()!);
        if (room.RoomType != expectedRoom) throw new Exception("Native room creation did not use the requested room type.");
        var modifier = scenario.CombatState.RunState.Modifiers.OfType<HextechMayhemModifier>().Single();
        if (!modifier.HasActiveMonsterHex(MonsterHexKind.HailToTheKing)) throw new Exception("Native Hail opening was not selected.");
        int tier = modifier.GetMonsterHexStrengthTier(MonsterHexKind.HailToTheKing);
        foreach (var enemy in scenario.CombatState.Enemies)
        {
            int expected = room.RoomType is RoomType.Elite or RoomType.Boss
                ? HextechEnemyHexContext.FractionOfMaxHp(enemy, tier <= 1 ? .03m : tier == 2 ? .05m : .08m) : 0;
            if (enemy.GetPowerAmount<ArtifactPower>() != (expected > 0 ? 3 : 0)
                || enemy.GetPowerAmount<PlatingPower>() != expected || enemy.GetPowerAmount<RegenPower>() != expected)
                throw new Exception($"Native Hail powers differ: room={room.RoomType} tier={tier} sustain={expected}.");
        }
        _ = CombatRootSnapshot.Capture(scenario.CombatState);
        for (int i = 0; i < 2; i++) await runner.AssertReportRoundAsync(scenario.CombatState, scenario.Player);
        GD.Print($"HEXTECH_NATIVE_HAIL_OPENING_VERIFIED room={room.RoomType} tier={tier} real_room_creation=true native_future_rounds=2");
        return new(false, scenario.Player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeDoubleVisionRewards(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var player = scenario.Player;
        _ = player.Relics.OfType<DoubleVisionRune>().Single();
        int turn = player.PlayerCombatState!.TurnNumber;
        int before = player.Gold;
        await PlayerCmd.GainGold(10, player, false);
        if (player.Gold != before + 10) throw new Exception("DoubleVision incorrectly duplicated an active-combat command.");
        _ = CombatRootSnapshot.Capture(scenario.CombatState);
        var finishingCard = player.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == "STRIKE_SILENT");
        if (!finishingCard.TryManualPlay(scenario.CombatState.Enemies.First())) throw new Exception("Native finishing Strike was not playable.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        for (int i = 0; i < 180 && CombatManager.Instance.IsInProgress; i++) await runner.NextFrameAsync();
        if (CombatManager.Instance.IsInProgress) throw new Exception("Native battle did not finish before the reward test.");
        before = player.Gold;
        await PlayerCmd.GainGold(10, player, false);
        if (player.Gold != before + 20) throw new Exception("Native DoubleVision direct reward did not duplicate exactly once.");
        before = player.Gold;
        var reward = new GoldReward(7, player);
        await reward.SelectUnsynchronized();
        if (!reward.SuccessfullySelected || player.Gold != before + 14)
            throw new Exception("Native DoubleVision reward selection did not preserve suppression and AfterRewardTaken.");
        GD.Print("HEXTECH_NATIVE_DOUBLE_VISION_REWARDS_VERIFIED battle_gain=10 outside_gain=20 reward_gain=14 native_victory=true original_reward_selection=true");
        return new(true, turn, true, false, false, false);
    }
}
