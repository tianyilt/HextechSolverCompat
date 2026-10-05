using CombatSolver;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeThieving(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var player = scenario.Player;
        var enemy = scenario.CombatState.Enemies.First();
        var initialDeck = player.Deck.Cards.ToArray();
        VerifyNativeTokenFork(scenario);
        var frozenRoot = CombatRootSnapshot.Capture(scenario.CombatState);
        var frozen = frozenRoot.ForkSimulator();
        var frozenCombat = (SimulatedCombatState)frozen.State.CombatState;
        var ai = (BranchMonsterAiState)AccessTools.Method(typeof(SimulatedCombatState), "GetMonsterAiState").Invoke(frozenCombat, [enemy])!;
        string FrozenAi() => string.Join(";", ai.Machine.States.Keys.Order()) + "|" + ai.Current.Id + "|"
            + string.Join(";", ai.Current.Intents.Select(intent => intent.GetType().FullName));
        string frozenAi = FrozenAi();
        string FrozenStamp() => ContinuationStamp.CapturePredicted(player, frozen,
            frozenRoot.StartTurnNumber, frozenRoot.Forecast, frozenRoot.StartTurnNumber).StateText;
        string frozenStamp = FrozenStamp();
        if (ReferenceEquals(ai.Machine, enemy.Monster!.MoveStateMachine))
            throw new Exception("Native theft still shares the live mutable monster state machine.");
        var result = await VerifyNativeTokenActual(runner, scenario);
        if (FrozenAi() != frozenAi || FrozenStamp() != frozenStamp)
            throw new Exception("Native gameplay changed an earlier frozen theft search root.");
        using var request = Request();
        int expected = request.RootElement.GetProperty("hextechNativeTheftExpectedRemoved").GetInt32();
        var removed = initialDeck.Except(player.Deck.Cards).ToArray();
        if (removed.Length != expected) throw new Exception($"Native theft permanent removal: expected={expected}, actual={removed.Length}.");
        if (expected > 0 && request.RootElement.TryGetProperty("hextechNativeTheftExpectedCard", out var cardId)
            && removed.Any(card => card.Id.Entry != cardId.GetString())) throw new Exception("Native theft chose the wrong permanent card priority.");
        var room = (CombatRoom)scenario.CombatState.RunState.CurrentRoom!;
        var returns = room.ExtraRewards.GetValueOrDefault(player, []).OfType<SpecialCardReward>()
            .Where(reward => removed.Contains((CardModel)AccessTools.Field(typeof(SpecialCardReward), "_card").GetValue(reward)!)).ToArray();
        bool recover = request.RootElement.TryGetProperty("hextechNativeTheftRecover", out var recovery) && recovery.GetBoolean();
        if (returns.Length != (recover ? expected : 0)) throw new Exception("Native theft death/escape returned an incorrect number of rewards.");
        if (recover)
        {
            foreach (var reward in returns)
                await reward.SelectUnsynchronized();
            if (removed.Any(card => !player.Deck.Cards.Contains(card)) || player.Deck.Cards.Count != initialDeck.Length)
                throw new Exception("Native theft reward selection did not restore the exact original permanent card.");
        }
        else if (expected > 0 && scenario.CombatState.Enemies.Contains(enemy))
            throw new Exception("Native theft escape did not remove its owner from combat.");
        GD.Print("HEXTECH_NATIVE_THIEVING_VERIFIED original_priority=true permanent_removal=true death_reward_not_immediate_return=true escape_without_return=true full_snapshots=true fork_key_stamp_rng=true");
        return result;
    }
}
