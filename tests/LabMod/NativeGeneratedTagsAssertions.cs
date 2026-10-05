using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void AssertNativeGeneratedTags(CombatPredictionSimulator simulator, MegaCrit.Sts2.Core.Combat.CombatState actual)
    {
        var closureType = AccessTools.Inner(typeof(ModManager), "<>c__DisplayClass29_0");
        var closure = Activator.CreateInstance(closureType, nonPublic: true)!;
        var mod = ModManager.Mods.First(m => m.manifest?.id == "HextechSolverCompat");
        AccessTools.Field(closureType, "mod").SetValue(closure, mod);
        var predicate = AccessTools.DeclaredMethod(closureType, "<Initialize>b__1");
        var local = new SettingsSaveMod(mod) { IsEnabled = true };
        var workshop = new SettingsSaveMod(mod) { IsEnabled = false, Source = ModSource.SteamWorkshop };
        foreach (var rows in new[] { new[] { workshop, local }, new[] { local, workshop } })
        {
            var match = rows.FirstOrDefault(row => (bool)predicate.Invoke(closure, [row])!);
            if (!ReferenceEquals(match, local) || !local.IsEnabled || workshop.IsEnabled)
                throw new Exception("Native settings rebuild conflated local and workshop choices.");
        }
        var differentId = new SettingsSaveMod(mod) { Id = "UnrelatedMod" };
        if ((bool)predicate.Invoke(closure, [differentId])!) throw new Exception("Settings rebuild matched another mod ID.");
        GD.Print("HEXTECH_MOD_SOURCE_SETTINGS_VERIFIED both_list_orders=true local_enabled=true workshop_disabled=true unrelated_id_exempt=true");
        var player = actual.Players.Single(); var state = simulator.State.GetPlayerCombatState(player);
        static string Identity(CardModel card) => card.Id.Entry + ":" + card.CurrentUpgradeLevel + ":"
            + string.Join(',', card.Tags.Order()) + ":" + card.TargetType + ":"
            + card.Enchantment?.Id.Entry + ":" + card.Enchantment?.Amount;
        var native = player.PlayerCombatState!.AllCards.Select(Identity).Order(StringComparer.Ordinal).ToArray();
        var predicted = state.AllCards.Select(card => Identity(card.MutablePreview)).Order(StringComparer.Ordinal).ToArray();
        if (!native.SequenceEqual(predicted)) throw new Exception("Generated card tags/target/enchantment differ: native="
            + string.Join(';', native) + " predicted=" + string.Join(';', predicted));
        if (player.GetRelic<BigKnifeRune>() is not null && state.AllCards.Any(card => card.Preview is Shiv))
            throw new Exception("BigKnife left a generated Shiv in the branch roster.");
        if (player.GetRelic<DeviantCognitionRune>() is not null && state.AllCards.Any(card =>
            (card.Preview.Type == CardType.Attack || player.GetRelic<IllusoryWeaponRune>() is not null
                && (card.Preview.CanonicalInstance?.Type ?? card.Preview.Type) == CardType.Skill)
            && !card.Preview.Tags.Contains(CardTag.Strike)))
            throw new Exception("Deviant omitted an effective attack's Strike tag.");
        GD.Print("HEXTECH_NATIVE_GENERATED_TAGS_VERIFIED native_tags=true native_targets=true native_enchantments=true no_phantom_shiv=true");
        var target = AccessTools.PropertyGetter(typeof(CardModel), "Tags");
        var probe = new Harmony("HextechCompatLab.UnknownTagCallback");
        var stamp = ContinuationStamp.CaptureLive(actual).StateText;
        try
        {
            probe.Patch(target, postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(UnknownTagPostfix)));
            if (ContinuationStamp.CaptureLive(actual).StateText == stamp) throw new Exception("Extra tag callback did not invalidate the live stamp.");
            try { _ = CombatRootSnapshot.Capture(actual); throw new Exception("Extra tag callback was accepted."); }
            catch (PredictionUnsupportedException) { }
        }
        finally { probe.Unpatch(target, HarmonyPatchType.Postfix, probe.Id); }
        if (ContinuationStamp.CaptureLive(actual).StateText != stamp) throw new Exception("Tag callback probe failed to restore the composition.");
        GD.Print("HEXTECH_TAG_COMPOSITION_GUARD_VERIFIED extra_callback_rejected=true live_stamp_restored=true");
    }
    private static void UnknownTagPostfix() { }
    private static async Task VerifyNativeGeneratedBatch(UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var actual = scenario.CombatState; var player = scenario.Player;
        var root = CombatRootSnapshot.Capture(actual); var parent = root.ForkSimulator();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(actual).StateText;
        static void Generate(CombatPredictionSimulator simulator, MegaCrit.Sts2.Core.Entities.Players.Player owner, bool playerCreated)
        {
            var cards = new[] { PredictedCard.Create(ModelDb.Card<Burn>(), owner).Upgrade(),
                PredictedCard.Create(ModelDb.Card<Injury>(), owner), PredictedCard.Create(ModelDb.Card<Dazed>(), owner) };
            var results = simulator.AddGeneratedCardsToCombat(cards, PileType.Draw, playerCreated ? owner : null, CardPilePosition.Random);
            if (results.Any(result => !result.Success) || simulator.HasPendingChoice) throw new Exception("Native status batch did not complete.");
        }
        var child = parent.Fork(); var sibling = parent.Fork(); Generate(child, player, false);
        if (Stamp(child) == stamp || Stamp(parent) != stamp || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(actual).StateText != live)
            throw new Exception("Generated batch changed parent, sibling or real game.");
        foreach (bool playerCreated in new[] { false, true })
        {
            int count = player.PlayerCombatState!.DrawPile.Cards.Count;
            Generate(parent, player, playerCreated);
            var burn = actual.CreateCard<Burn>(player); CardCmd.Upgrade(burn);
            CardModel[] cards = [burn, actual.CreateCard<Injury>(player), actual.CreateCard<Dazed>(player)];
            await CardPileCmd.AddGeneratedCardsToCombat(cards, PileType.Draw, playerCreated ? player : null, CardPilePosition.Random);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            if (player.PlayerCombatState!.DrawPile.Cards.Count - count != (playerCreated ? 3 : 5))
                throw new Exception("Status batch did not duplicate only enemy-created status cards.");
            foreach (var enemy in actual.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy),
                    UnattendedTestRunner.CaptureActual(actual, player, enemy), "NativeGeneratedBatch", playerCreated ? "Player" : "Enemy");
            AssertNativeGeneratedTags(parent, actual);
        }
        GD.Print("HEXTECH_NATIVE_GENERATED_BATCH_VERIFIED enemy_status_doubled=true player_status_exempt=true curse_exempt=true upgraded_clone=true random_insertion=true fork_isolation=true");
    }
}
