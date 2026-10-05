using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeBeforeHandDraw(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        using var request = Request();
        var expectations = request.RootElement.GetProperty("hextechNativeBeforeHandDrawProbe");
        if (expectations.TryGetProperty("opening", out var opening))
            await ApplyNativeOpening(scenario, opening);
        if (expectations.TryGetProperty("resetSnakeFirstTurn", out var reset) && reset.GetBoolean())
            await player.Relics.OfType<SnakebiteRune>().Single().BeforeCombatStart();
        if (expectations.TryGetProperty("resetRelicIds", out var resetIds))
            foreach (var id in resetIds.EnumerateArray())
                await player.Relics.Single(relic => relic.Id.Entry == id.GetString()).BeforeCombatStart();
        var existingCards = player.PlayerCombatState!.AllCards.ToArray();
        int drawCount = player.PlayerCombatState.DrawPile.Cards.Count;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        if (shadow.PrepareBeforeHandDraw(child, player, new TurnStartChoiceCursor(null)) || child.HasPendingChoice)
            throw new Exception("BeforeHandDraw representative unexpectedly opened a choice.");
        if (Stamp(parent) != before || Stamp(sibling) != before || ContinuationStamp.CaptureLive(combat).StateText != live)
        {
            var changed = ContinuationStamp.CaptureLive(combat).StateText;
            int difference = 0;
            while (difference < Math.Min(live.Length, changed.Length) && live[difference] == changed[difference]) difference++;
            string Around(string text) => text.Substring(Math.Max(0, difference - 40), Math.Min(text.Length - Math.Max(0, difference - 40), 180));
            throw new Exception($"BeforeHandDraw isolation failed: parentChanged={Stamp(parent) != before} siblingChanged={Stamp(sibling) != before} liveChanged={changed != live} firstDifference={difference} before={Around(live)} after={Around(changed)}.");
        }
        await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
        foreach (var enemy in combat.Enemies)
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechBeforeHandDraw", "OrderedNativeCallbacks");
        var generated = player.PlayerCombatState.AllCards.Except(existingCards).ToArray();
        if (generated.Length != expectations.GetProperty("generated").GetInt32()
            || drawCount - player.PlayerCombatState.DrawPile.Cards.Count != expectations.GetProperty("movedFromDraw").GetInt32())
            throw new Exception($"BeforeHandDraw effect did not occur: generated={generated.Length} moved={drawCount - player.PlayerCombatState.DrawPile.Cards.Count}.");
        if (player.Relics.OfType<SingularityAIRune>().Any()
            && !generated.Any(card => card.Type == CardType.Power && card.EnergyCost.GetWithModifiers(CostModifiers.All) == 0))
            throw new Exception("SingularityAI did not create a free power card.");
        if (player.Relics.OfType<MindOverMatterRune>().Any()
            && !generated.Any(card => card.Keywords.Contains(CardKeyword.Ethereal)
                && card.EnergyCost.GetWithModifiers(CostModifiers.All) == 0))
            throw new Exception("MindOverMatter did not create a free ethereal card.");
        if (expectations.GetProperty("generated").GetInt32() + expectations.GetProperty("movedFromDraw").GetInt32() > 0
            && Stamp(child) == before)
            throw new Exception("BeforeHandDraw change is absent from continuation state.");
        if (expectations.TryGetProperty("playSequence", out var playSequence) && playSequence.GetBoolean())
        {
            VerifyNativeTokenFork(scenario);
            await VerifyNativeTokenActual(runner, scenario);
        }
        int rounds = expectations.GetProperty("rounds").GetInt32();
        for (int index = 0; index < rounds; index++) await runner.AssertReportRoundAsync(combat, player);
        GD.Print($"HEXTECH_NATIVE_BEFORE_HAND_DRAW_VERIFIED generated={generated.Length} ordered_callbacks=true free_keywords=true fork=true live_isolated=true rng=true native_rounds={rounds}");
        return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }
}
