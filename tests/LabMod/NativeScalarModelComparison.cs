using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void AssertNativeScalarModels(CombatPredictionSimulator sim, CombatState actual)
    {
        var shadow = (SimulatedCombatState)sim.State.CombatState;
        var stateType = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.NativeRuneState", throwOnError: true)!;
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType);
        var modelField = stateType.GetField("Model", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int compared = 0;
        foreach (var player in actual.Players)
        {
            var actualScopes = player.Relics.OfType<HextechRelicBase>().Where(r => r is UniversalScopeRuneBase or CircleOfDeathRune or NightstalkingRune or RekindleRune or DivineInterventionRune or DeathWarrantRune or GalacticGiftRune or LightEmUpRune or TwinFlamesRune or IllusoryWeaponRune or BloodIdolRune or SacrificeRune or GoldrendRune or BurningInterestRune or CollectorRune or GoliathRune or GoldenSpatulaRune or TankEngineRune or SoulEaterRune or JudicatorRune or CorpseExplosionRune or DiveBomberRune or FeedUpgradeRune or NineDragonPowerRune or CerberusRune or LubricantRune or TriPrismRune or MagicMissileRune or SpinToWinRune or BlossomBladeRune or FanTheHammerRune or JeweledGauntletRune or SerpentsFangRune or InfernalConduitRune or BoneBreakUpgradeRune or BorrowedTimeUpgradeRune or UnleashUpgradeRune or FallingStarUpgradeRune or MiseryUpgradeRune or SnakebiteUpgradeRune or NeutralizeUpgradeRune or StrikeUpgradeRune or DefendUpgradeRune or HiddenGemUpgradeRune or JackpotUpgradeRune or CompactUpgradeRune or FlakCannonUpgradeRune or SurvivorUpgradeRune or NightmareUpgradeRune or VoltaicUpgradeRune or CreativeAiUpgradeRune or ArcanePunchRune or DevilsDanceRune or MakeItMineRune or TranscendentEvilRune or RoyaltiesUpgradeRune or SubroutineUpgradeRune or SendThemInRune or RoyalTrialRune or ReanimateUpgradeRune or EternalArmorUpgradeRune or DrawYourSwordRune or MyriadManifestationsRune or OmniDragonSoulRune or BloodDebtRune or EndlessRotationRune or StormUpgradeRune or BigHammerRune or KingdomArmyRune or ChainInSleeveRune or NetherSoulRune or NearDeathFeastRune or MasterOfDualityRune or MyriadSwordsRune or DecisionsDecisionsUpgradeRune or BigKnifeRune or InkshadowRune or DeviantCognitionRune).ToArray();
            var predictedScopes = shadow.RelicsOf(player).OfType<HextechRelicBase>().Where(r => r is UniversalScopeRuneBase or CircleOfDeathRune or NightstalkingRune or RekindleRune or DivineInterventionRune or DeathWarrantRune or GalacticGiftRune or LightEmUpRune or TwinFlamesRune or IllusoryWeaponRune or BloodIdolRune or SacrificeRune or GoldrendRune or BurningInterestRune or CollectorRune or GoliathRune or GoldenSpatulaRune or TankEngineRune or SoulEaterRune or JudicatorRune or CorpseExplosionRune or DiveBomberRune or FeedUpgradeRune or NineDragonPowerRune or CerberusRune or LubricantRune or TriPrismRune or MagicMissileRune or SpinToWinRune or BlossomBladeRune or FanTheHammerRune or JeweledGauntletRune or SerpentsFangRune or InfernalConduitRune or BoneBreakUpgradeRune or BorrowedTimeUpgradeRune or UnleashUpgradeRune or FallingStarUpgradeRune or MiseryUpgradeRune or SnakebiteUpgradeRune or NeutralizeUpgradeRune or StrikeUpgradeRune or DefendUpgradeRune or HiddenGemUpgradeRune or JackpotUpgradeRune or CompactUpgradeRune or FlakCannonUpgradeRune or SurvivorUpgradeRune or NightmareUpgradeRune or VoltaicUpgradeRune or CreativeAiUpgradeRune or ArcanePunchRune or DevilsDanceRune or MakeItMineRune or TranscendentEvilRune or RoyaltiesUpgradeRune or SubroutineUpgradeRune or SendThemInRune or RoyalTrialRune or ReanimateUpgradeRune or EternalArmorUpgradeRune or DrawYourSwordRune or MyriadManifestationsRune or OmniDragonSoulRune or BloodDebtRune or EndlessRotationRune or StormUpgradeRune or BigHammerRune or KingdomArmyRune or ChainInSleeveRune or NetherSoulRune or NearDeathFeastRune or MasterOfDualityRune or MyriadSwordsRune or DecisionsDecisionsUpgradeRune or BigKnifeRune or InkshadowRune or DeviantCognitionRune).ToArray();
            if (actualScopes.Length != predictedScopes.Length) throw new Exception("Native scope membership changed.");
            for (int index = 0; index < actualScopes.Length; index++)
            {
                var native = actualScopes[index];
                var ownedState = get.Invoke(null, [sim, predictedScopes[index]])!;
                // A fork may replace card previews through copy-on-write.
                // Inspect the same refreshed model view that Invoke consumes;
                // the state ledger itself retains remapped PredictedCards.
                stateType.GetMethod("BindCardLedgers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(ownedState, null);
                var predicted = (HextechRelicBase)modelField.GetValue(ownedState)!;
                if (native.GetType() != predicted.GetType()) throw new Exception("Scope listener order changed.");
                for (Type? type = native.GetType(); type is not null && type.Assembly == typeof(ModEntry).Assembly; type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object? left = field.GetValue(native), right = field.GetValue(predicted);
                        bool equal = typeof(ICombatState).IsAssignableFrom(field.FieldType)
                            ? (left is null) == (right is null)
                            : left is System.Collections.IDictionary ledger && right is System.Collections.IDictionary predictedLedger
                                ? NativeCardLedgerEqual(ledger, predictedLedger, sim)
                                : left is HashSet<MegaCrit.Sts2.Core.Entities.Creatures.Creature> targets
                                && right is HashSet<MegaCrit.Sts2.Core.Entities.Creatures.Creature> predictedTargets
                                    ? targets.SetEquals(predictedTargets)
                                    : left is Dictionary<MegaCrit.Sts2.Core.Models.CardModel, bool> rolls
                                        && right is Dictionary<MegaCrit.Sts2.Core.Models.CardModel, bool> predictedRolls
                                            ? rolls.Count == predictedRolls.Count && rolls.All(pair =>
                                                sim.State.FindCard(pair.Key) is { } card
                                                && predictedRolls.TryGetValue(card.MutablePreview, out var roll) && roll == pair.Value)
                                            : left is HashSet<MegaCrit.Sts2.Core.Models.CardModel> free && right is HashSet<MegaCrit.Sts2.Core.Models.CardModel> predictedFree
                                                ? free.Count == predictedFree.Count && free.All(card => sim.State.FindCard(card) is { } mapped && predictedFree.Contains(mapped.MutablePreview))
                                                : left is MegaCrit.Sts2.Core.Models.CardModel liveCard
                                                && right is MegaCrit.Sts2.Core.Models.CardModel predictedCard
                                                    ? ReferenceEquals(sim.State.FindCard(liveCard)?.MutablePreview, predictedCard)
                                                    : Equals(left, right);
                        if (!equal) throw new Exception($"Native scope scalar differs: {native.GetType().Name}.{field.Name}: actual={left} predicted={right}.");
                        compared++;
                    }
            }
        }
        GD.Print($"HEXTECH_NATIVE_SCOPE_COUNTERS_VERIFIED fields={compared} native_order=true original_proc_counters=true");
    }

    private static bool NativeCardLedgerEqual(System.Collections.IDictionary native, System.Collections.IDictionary predicted, CombatPredictionSimulator simulator)
    {
        var entries = native.Keys.Cast<MegaCrit.Sts2.Core.Models.CardModel>()
            .Where(card => simulator.State.FindCard(card) is not null).Select(card => (Card: card, Value: native[card])).ToArray();
        return entries.Length == predicted.Count && entries.All(entry => {
            var card = simulator.State.FindCard(entry.Card)!;
            return predicted.Contains(card.MutablePreview) && Equals(entry.Value, predicted[card.MutablePreview]);
        });
    }

    private static void AssertNativeGoldModels(CombatPredictionSimulator simulator, CombatState actual)
    {
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        foreach (var player in actual.Players)
            if (shadow.GetPlayerGold(player) != player.Gold)
                throw new Exception($"Native gold differs: expected={shadow.GetPlayerGold(player)} actual={player.Gold}.");
        GD.Print("HEXTECH_NATIVE_GOLD_VERIFIED actual_player_gold=true");
    }

    private static void AssertNativeVictoryGold(CombatPredictionSimulator simulator, CombatState actual,
        MegaCrit.Sts2.Core.Entities.Players.Player player, MegaCrit.Sts2.Core.Entities.Creatures.Creature[] enemies)
    {
        // Native victory cleanup clears card history/piles. A solver terminal
        // deliberately retains the final battle state for scoring. This test
        // checks the gold feedback outcome after cleanup; continuing battles
        // still use the complete stock snapshot comparison above.
        if (CombatManager.Instance.IsInProgress || enemies.Any(enemy => !enemy.IsDead)
            || enemies.Any(enemy => simulator.State.GetCreature(enemy).IsAlive))
            throw new Exception("Gold victory probe requires an actual and predicted completed victory.");
        var predicted = simulator.State.GetCreature(player.Creature);
        if (predicted.CurrentHp != player.Creature.CurrentHp || predicted.MaxHp != player.Creature.MaxHp
            || predicted.Block != player.Creature.Block)
            throw new Exception("Gold victory health/maxHP/block outcome differs after native cleanup.");
        AssertNativeGoldModels(simulator, actual);
        GD.Print("HEXTECH_NATIVE_VICTORY_GOLD_VERIFIED real_victory=true health_and_gold_match=true native_cleanup_not_solver_scoring_state=true");
    }

    private static void AssertNativePlayerDeath(CombatPredictionSimulator simulator, CombatState actual,
        MegaCrit.Sts2.Core.Entities.Players.Player player, MegaCrit.Sts2.Core.Entities.Creatures.Creature[] enemies)
    {
        if (CombatManager.Instance.IsInProgress || !player.Creature.IsDead || simulator.State.GetCreature(player.Creature).IsAlive)
            throw new Exception("Player death probe requires an actual and predicted completed death.");
        foreach (var enemy in enemies)
        {
            var predicted = simulator.State.GetCreature(enemy);
            if (predicted.CurrentHp != enemy.CurrentHp || predicted.MaxHp != enemy.MaxHp)
                throw new Exception($"Player death explosion differs: predicted={predicted.CurrentHp}/{predicted.MaxHp} actual={enemy.CurrentHp}/{enemy.MaxHp}.");
        }
        AssertNativeGoldModels(simulator, actual);
        GD.Print("HEXTECH_NATIVE_PLAYER_DEATH_VERIFIED death_reaction_enemy_health=true actual_terminal_cleanup=true");
    }
}
