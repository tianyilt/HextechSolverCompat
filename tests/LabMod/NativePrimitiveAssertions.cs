using System.Text.Json;
using CombatSolver;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativePrimitive(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement settings)
    {
        var player = scenario.Player;
        bool cancel = settings.GetProperty("cancel").GetBoolean();
        var originalDeck = player.Deck.Cards.ToArray();
        var selected = originalDeck.First(card => card.IsTransformable && card.IsUpgradable);
        CardCmd.Upgrade(selected, CardPreviewStyle.None);
        int upgrades = selected.CurrentUpgradeLevel;
        var selector = new GeneratedScenarioCardSelector([cancel ? [] : [selected.Id.Entry]]);
        using (CardSelectCmd.PushSelector(selector, localOnly: true))
            await RelicCmd.Obtain(ModelDb.AllRelics.Single(relic => relic.Id.Entry == "PRIMITIVE_MADNESS_RUNE").ToMutable(), player);
        selector.AssertConsumed();
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Deck.Cards.Count != originalDeck.Length) throw new Exception("Primitive transformation changed deck cardinality.");
        if (cancel)
        {
            if (!player.Deck.Cards.SequenceEqual(originalDeck)) throw new Exception("Canceled native transformation changed the deck.");
        }
        else
        {
            var replacement = player.Deck.Cards.Except(originalDeck).Single();
            if (player.Deck.Cards.Contains(selected) || replacement.Id.Entry != "GIANT_ROCK" || replacement.CurrentUpgradeLevel != upgrades)
                throw new Exception("Native Primitive did not replace the selected card with an upgrade-preserving GiantRock.");
        }
        GD.Print($"HEXTECH_NATIVE_PRIMITIVE_ACQUISITION_VERIFIED cancel={cancel} original_obtain=true real_deck=true upgrade_preserved=true");
        VerifyNativeTokenFork(scenario);
        return await VerifyNativeTokenActual(runner, scenario);
    }
}
