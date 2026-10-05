using CombatSolver;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using System.Text.Json;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeCardAcquisition(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement option)
    {
        var player = scenario.Player;
        string cardId = option.GetProperty("cardId").GetString()!;
        string relicId = option.GetProperty("relicId").GetString()!;
        bool inCombat = option.GetProperty("inCombat").GetBoolean();
        int expectedCount = option.TryGetProperty("count", out var count) ? count.GetInt32() : 1;
        if (inCombat)
        {
            int deckCount = player.Deck.Cards.Count;
            int before = player.PlayerCombatState!.AllCards.Count();
            if (player.PlayerCombatState.AllCards.Any(c => c.Id.Entry == cardId)
                || player.Relics.Any(r => r.Id.Entry == relicId))
                throw new Exception("Native combat acquisition must begin without the acquired card/relic.");
            var canonical = ModelDb.AllRelics.Single(r => r.Id.Entry == relicId);
            await RelicCmd.Obtain(canonical.ToMutable(), player);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var generated = player.PlayerCombatState.Hand.Cards.Where(c => c.Id.Entry == cardId).ToArray();
            if (generated.Length != expectedCount || player.Deck.Cards.Count != deckCount
                || player.PlayerCombatState.AllCards.Count() != before + expectedCount)
                throw new Exception("Native in-combat acquisition did not create expected hand cards without altering deck.");
            int upgrade = option.TryGetProperty("upgrade", out var level) ? level.GetInt32() : 0;
            foreach (var card in generated)
            {
                for (int index = 0; index < upgrade; index++) CardCmd.Upgrade(card, CardPreviewStyle.None);
                if (card.CurrentUpgradeLevel != upgrade) throw new Exception("Native acquired card upgrade failed.");
            }
        }
        else
        {
            // The native runner obtained this relic before entering combat;
            // read back the permanent deck independently of injected hand data.
            if (player.Deck.Cards.Count(c => c.Id.Entry == cardId) != expectedCount
                || player.Relics.Count(r => r.Id.Entry == relicId) != 1)
                throw new Exception("Native pre-combat obtain failed to grant one permanent card and one relic.");
        }
        GD.Print($"HEXTECH_NATIVE_CARD_ACQUISITION_VERIFIED card={cardId} in_combat={inCombat} permanent_deck_readback=true");
        VerifyNativeTokenFork(scenario);
        using (var request = Request())
            if (request.RootElement.TryGetProperty("hextechNativeGeneratedTokensProbe", out var generated) && generated.GetBoolean())
                return await VerifyNativeGeneratedTokens(runner, scenario);
        return await VerifyNativeTokenActual(runner, scenario);
    }
}
