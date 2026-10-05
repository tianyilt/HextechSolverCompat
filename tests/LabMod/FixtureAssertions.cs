using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static int? _expectedHealedHp;
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(UnattendedTestRunner.Executor), "ExecuteAsync"),
            prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(BeforeExecute)),
            postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(AfterExecute)));
        InstallAttackResultProbe(harmony);
        InstallRevivalHpProbe(harmony);
        InstallEnemyRepeatProbe(harmony);
        InstallNativeRewardOpeningProbe(harmony);
        InstallNativeHandSizeProbe(harmony);
        harmony.Patch(AccessTools.Method(typeof(UnattendedTestRunner), "AssertSnapshotEqual"),
            prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(TraceEnemyTurnSnapshots)));
    }

    private static JsonDocument Request() => JsonDocument.Parse(File.ReadAllText(
        System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_FIXTURE")!));

    private static bool BeforeExecute(UnattendedTestRunner.Executor __instance, UnattendedTestRunner.ScenarioContext scenario,
        ref Task<UnattendedTestRunner.ExecutionOutcome> __result)
    {
        using var request = Request();
        _expectedHealedHp = null;
        if (request.RootElement.TryGetProperty("hextechUnknownGuardProbe", out var unknownGuard))
        {
            __result = VerifyUnknownGuard(scenario, unknownGuard.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeKeywordSetup", out var keywordSetup))
            SetupNativeKeywords(scenario, keywordSetup);
        if (request.RootElement.TryGetProperty("hextechNativeKeywordIsolation", out var keywordIsolation) && keywordIsolation.GetBoolean())
            VerifyNativeKeywordIsolation(scenario);
        if (request.RootElement.TryGetProperty("initialPlayerGold", out var initialGold))
            scenario.Player.Gold = initialGold.GetInt32();
        if (request.RootElement.TryGetProperty("hextechInitialNineDragonStacks", out var nineStacks))
            scenario.Player.Relics.OfType<NineDragonPowerRune>().Single().SavedStacks = nineStacks.GetInt32();
        if (request.RootElement.TryGetProperty("hextechNativeProgressStart", out var progressStart)
            && progressStart.TryGetProperty("stacks", out var progressStacks))
        {
            foreach (var rune in scenario.Player.Relics.OfType<MakeItMineRune>()) rune.SavedStacks = progressStacks.GetInt32();
            foreach (var rune in scenario.Player.Relics.OfType<TranscendentEvilRune>()) rune.SavedStacks = progressStacks.GetInt32();
        }
        if (request.RootElement.TryGetProperty("hextechInitialMaxHpBases", out var maxHpBases))
            foreach (var property in maxHpBases.EnumerateObject())
                AccessTools.Property(scenario.Player.Relics.Single(relic => relic.Id.Entry == property.Name).GetType(), "SavedBaseMaxHp")
                    .SetValue(scenario.Player.Relics.Single(relic => relic.Id.Entry == property.Name), property.Value.GetInt32());
        if (request.RootElement.TryGetProperty("hextechNativeEnemyHpDisplay", out var hpDisplay))
            foreach (var enemy in scenario.CombatState.Enemies)
                enemy.HpDisplay = Enum.Parse<MegaCrit.Sts2.Core.Entities.Creatures.HpDisplay>(hpDisplay.GetString()!);
        if (request.RootElement.TryGetProperty("hextechNativePotionPrepare", out var potionPrepare))
        {
            foreach (var potion in scenario.Player.PotionSlots.ToArray()) potion?.Discard();
            foreach (var id in potionPrepare.EnumerateArray())
                UnattendedTestRunner.InjectPotionForTest(scenario.Player, id.GetString()!);
        }
        if (request.RootElement.TryGetProperty("hextechNativePotionFill", out var potionFill))
            while (scenario.Player.PotionSlots.Any(potion => potion is null))
                UnattendedTestRunner.InjectPotionForTest(scenario.Player, potionFill.GetString()!);
        if (request.RootElement.TryGetProperty("hextechNativeJeweledPendingQueryProbe", out var jeweledPending) && jeweledPending.GetBoolean())
        {
            var rune = scenario.Player.Relics.OfType<JeweledGauntletRune>().Single();
            var ordinal = AccessTools.Field(typeof(JeweledGauntletRune), "_replayRollsThisCombat");
            var queryOrdinalBefore = ordinal.GetValue(rune);
            var cards = scenario.Player.PlayerCombatState!.Hand.Cards.ToArray();
            foreach (var card in cards.Concat(cards.Reverse()))
                rune.ModifyCardPlayCount(card, scenario.CombatState.Enemies.First(), 1);
            if (!Equals(queryOrdinalBefore, ordinal.GetValue(rune))) throw new Exception("Native jeweled query consumed a play ordinal.");
            GD.Print("HEXTECH_NATIVE_JEWELED_QUERY_PREPARED multiple_pending_cards=true peek_only=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeEnemyDrawProgress", out var enemyDrawProgress))
        {
            var tracking = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single().CombatTracking;
            foreach (var entry in enemyDrawProgress.EnumerateObject())
                ((Dictionary<ulong, int>)AccessTools.Field(typeof(HextechMayhemCombatTrackingState), entry.Name).GetValue(tracking)!)
                    [scenario.Player.NetId] = entry.Value.GetInt32();
        }
        if (request.RootElement.TryGetProperty("hextechNativeDrawProgress", out var drawProgress))
            scenario.Player.Relics.OfType<NightstalkingRune>().Single().SavedCardsDrawnThisCombat = drawProgress.GetInt32();
        if (request.RootElement.TryGetProperty("hextechNativeDrawProgressByRelic", out var drawProgressByRelic))
            foreach (var entry in drawProgressByRelic.EnumerateObject())
                AccessTools.Property(scenario.Player.Relics.Single(r => r.Id.Entry == entry.Name).GetType(), "SavedCardsDrawnThisCombat")
                    .SetValue(scenario.Player.Relics.Single(r => r.Id.Entry == entry.Name), entry.Value.GetInt32());
        if (request.RootElement.TryGetProperty("hextechNativeOpeningProbe", out var nativeOpening))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeOpening(runner, scenario, nativeOpening.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeCardAcquisitionProbe", out var acquisition))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeCardAcquisition(runner, scenario, acquisition.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativePrimitiveProbe", out var primitive))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativePrimitive(runner, scenario, primitive.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeTurnResourceProbe", out _))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeTurnResources(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeBeforeHandDrawProbe", out _))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeBeforeHandDraw(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeGrowthProbe", out var growthProbe))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeGrowth(runner, scenario, growthProbe.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechInitialGoldenSpatulaStacks", out var initialStacks))
            scenario.Player.Relics.OfType<GoldenSpatulaRune>().Single().SavedStacks = initialStacks.GetInt32();
        if (request.RootElement.TryGetProperty("hextechNativeProjectionFreezeProbe", out var projectionProbe) && projectionProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            VerifyNativeProjectionFreeze(runner, scenario);
        }
        if (request.RootElement.TryGetProperty("hextechInitialOsty", out var initialOsty))
        {
            var osty = scenario.Player.Osty ?? throw new Exception("Osty boundary fixture requires Necrobinder.");
            osty.SetMaxHpInternal(initialOsty.GetProperty("maxHp").GetInt32());
            osty.SetCurrentHpInternal(initialOsty.GetProperty("hp").GetInt32());
        }
        if (request.RootElement.TryGetProperty("hextechNativeEventContractProbe", out var eventContract) && eventContract.GetBoolean())
            VerifyNativeEventContract(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeTransientExpectedPlays", out var transientPlays))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeTransientAutoPlay(runner, scenario, transientPlays.GetInt32());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechGeneratedReactionSteps", out var generatedReactions))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyGeneratedReactions(runner, scenario, generatedReactions.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeEnemyDebuffTurnsProbe", out var debuffTurnsProbe) && debuffTurnsProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeEnemyDebuffTurns(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeEnemyPeriodicProbe", out var periodicProbe) && periodicProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeEnemyPeriodic(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeEnemyTurnProbe", out var enemyTurnProbe) && enemyTurnProbe.GetBoolean())
            VerifyEnemyTurnRoot(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeOrbSetup", out var orbSetup))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeOrbSetup(runner, scenario, orbSetup.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechOpeningFormProbe", out var formProbe))
            VerifyOpeningFormRoot(scenario, formProbe.Clone());
        if (request.RootElement.TryGetProperty("hextechPowerExpiryContractProbe", out var expiryProbe) && expiryProbe.GetBoolean())
            VerifyPowerExpiryContract(scenario);
        if (request.RootElement.TryGetProperty("hextechKnowledgeCounter", out var knowledgeCounter))
            AccessTools.Field(typeof(MegaCrit.Sts2.Core.Models.Monsters.KnowledgeDemon), "_curseOfKnowledgeCounter")
                .SetValue(scenario.CombatState.Enemies.Single().Monster, knowledgeCounter.GetInt32());
        if (request.RootElement.TryGetProperty("hextechEnemyRepeatProbe", out var repeatProbe))
            VerifyEnemyRepeatRoot(scenario, repeatProbe.Clone());
        if (request.RootElement.TryGetProperty("hextechGeneratedOnPlayProbe", out var generatedOnPlay) && generatedOnPlay.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyGeneratedOnPlay(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNeutralNegativeProbe", out var neutralNegative) && neutralNegative.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNeutralNegative(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeSlyCards", out var slyCards))
        {
            foreach (var id in slyCards.EnumerateArray())
                scenario.Player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == id.GetString()).GiveSingleTurnSly();
            GD.Print("HEXTECH_NATIVE_SLY_SETUP_VERIFIED native_setter=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeLocalCosts", out var localCosts))
            SetNativeLocalCosts(scenario, localCosts);
        if (request.RootElement.TryGetProperty("hextechExpectedRelics", out var relicChecks))
        {
            var ids = scenario.Player.Relics.Select(relic => relic.Id.Entry).ToHashSet(StringComparer.Ordinal);
            foreach (var id in relicChecks.GetProperty("present").EnumerateArray())
                if (!ids.Contains(id.GetString()!)) throw new Exception($"Native acquisition did not obtain {id}.");
            foreach (var id in relicChecks.GetProperty("absent").EnumerateArray())
                if (ids.Contains(id.GetString()!)) throw new Exception($"Native acquisition did not consume {id}.");
            GD.Print("HEXTECH_NATIVE_RELIC_ACQUISITION_VERIFIED present_and_consumed=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeDupeProbe", out var dupeFlag) && dupeFlag.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeDupe(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechInitialOsty", out var earlyOstySetup))
        {
            var osty = scenario.Player.Osty ?? throw new Exception("Osty setup requires an existing summoned Osty.");
            osty.SetMaxHpInternal(earlyOstySetup.GetProperty("maxHp").GetInt32());
            osty.SetCurrentHpInternal(earlyOstySetup.GetProperty("hp").GetInt32());
        }
        if (request.RootElement.TryGetProperty("hextechSummonProbe", out var summonProbe))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifySummonRunes(runner, scenario, summonProbe.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechAutomationDrawProbe", out var automationProbe))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyAutomationDraw(runner, scenario, automationProbe.Clone());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeColorDiscoveryProbe", out _))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeColorDiscovery(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeHailOpeningProbe", out var hailOpening) && hailOpening.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeHailOpening(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeDoubleVisionRewardProbe", out var doubleReward) && doubleReward.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeDoubleVisionRewards(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeRounds", out var nativeRounds))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeRounds(runner, scenario, nativeRounds.GetInt32());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemySpawnProbe", out var spawn) && spawn.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemySpawn(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemyRevivalProbe", out var revival) && revival.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemyRevival(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechCaptureRootOnly", out var rootOnly) && rootOnly.GetBoolean())
        {
            _ = CombatRootSnapshot.Capture(scenario.CombatState);
            __result = Task.FromResult(new UnattendedTestRunner.ExecutionOutcome(false,
                scenario.Player.PlayerCombatState!.TurnNumber, true, false, false, false));
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechExpectedCardUpgrade", out var expectedUpgrade))
        {
            var hand = scenario.Player.PlayerCombatState!.Hand.Cards;
            if (hand.Count != 1 || hand[0].CurrentUpgradeLevel != expectedUpgrade.GetInt32())
                throw new Exception("Fixture card upgrade level was not actually applied.");
        }
        if (request.RootElement.TryGetProperty("hextechRemoveRelics", out var removeRelics))
            foreach (var id in removeRelics.EnumerateArray())
                scenario.Player.RemoveRelicInternal(scenario.Player.Relics.Single(r => r.Id.Entry == id.GetString()), silent: true);
        if (request.RootElement.TryGetProperty("hextechAttackResultProbe", out var attackProbe) && attackProbe.GetBoolean())
            BeginAttackResultProbe();
        if (request.RootElement.TryGetProperty("hextechOverkillSurvivorProbe", out var survivorProbe) && survivorProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyOverkillWithSurvivor(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechOwnerDebuffProbe", out var ownerDebuff))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyOwnerDebuffs(runner, scenario, ownerDebuff.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemyDebuffProbe", out var enemyDebuff))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemyDebuffs(runner, scenario, enemyDebuff.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeNearDeathProbe", out var nearDeath))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeNearDeath(runner, scenario, nearDeath.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemyDamageProcProbe", out var damageProc))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemyDamageProcs(runner, scenario, damageProc.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechPlayerDamageProbe", out var playerDamage))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyPlayerDamage(runner, scenario, playerDamage.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechProtectiveTurnProbe", out var protectiveTurns))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyProtectiveTurns(runner, scenario, protectiveTurns.GetInt32());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechWhiteHoleProbe", out var whiteHole))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyWhiteHole(runner, scenario, whiteHole.GetBoolean());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeRuneForkProbe", out var nativeProbe) && nativeProbe.GetBoolean())
            VerifyNativeRuneFork(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeScalarProbe", out var scalarProbe))
            VerifyNativeScalar(scenario, scalarProbe);
        if (request.RootElement.TryGetProperty("hextechEnemyAttackCounterProbe", out var counterProbe) && counterProbe.GetBoolean())
            VerifyEnemyAttackCounter(scenario);
        if (request.RootElement.TryGetProperty("hextechEnemyExhaustSlotProbe", out var slotProbe) && slotProbe.GetBoolean())
            VerifyEnemyExhaustSlots(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeCostQueryProbe", out var costProbe) && costProbe.GetBoolean())
            VerifyNativeCardCosts(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeManualGateProbe", out var gateProbe) && gateProbe.GetBoolean())
            VerifyNativeManualGates(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeDiscardQueueProbe", out var queueProbe) && queueProbe.GetBoolean())
            VerifyNativeDiscardQueueFork(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeCallbackChoiceCard", out var nativeChoiceCard))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeCallbackChoice(runner, scenario, nativeChoiceCard.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeCleanseDampen", out var cleanseDampen) && cleanseDampen.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeCleanseDampen(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeMysteryProbe", out var mysteryProbe) && mysteryProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeMystery(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeNatureProbe", out var natureProbe) && natureProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeNatureTimer(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeDamageBoundaryProbe", out var damageBoundary) && damageBoundary.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeDamageBoundary(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeSweepingProbe", out var sweeping) && sweeping.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeSweepingBlade(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeVitalSparkProbe", out var vitalProbe) && vitalProbe.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeVitalSpark(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeTokenForkProbe", out var tokenProbe) && tokenProbe.GetBoolean())
            VerifyNativeTokenFork(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeTheftProbe", out var theft) && theft.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeThieving(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeSolidTimeProbe", out var solidTime) && solidTime.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeSolidTime(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeAuxiliaryProbe", out var auxiliary) && auxiliary.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeAuxiliary(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeGeneratedTokensProbe", out var generatedTokens) && generatedTokens.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeGeneratedTokens(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativeTokenActualProbe", out var tokenActual) && tokenActual.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyNativeTokenActual(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechNativePowerQueryProbe", out var queryProbe) && queryProbe.GetBoolean())
            VerifyNativePowerQuery(scenario);
        if (request.RootElement.TryGetProperty("hextechJudicatorBranchProbe", out var judicatorProbe) && judicatorProbe.GetBoolean())
            VerifyJudicatorBranch(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeOrbForkProbe", out var orbProbe) && orbProbe.GetBoolean())
            VerifyNativeOrbFork(scenario);
        if (request.RootElement.TryGetProperty("hextechUnsupportedManualProbe", out var unsupported))
        {
            __result = VerifyUnsupportedManual(scenario, unsupported.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechRequireEthereal", out var ethereal) && ethereal.GetBoolean() &&
            !scenario.Player.PlayerCombatState!.Hand.Cards.Any(c => c.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Ethereal)))
            throw new Exception("Fixture requires a real Ethereal card. Available canonical cards: " + string.Join(',',
                MegaCrit.Sts2.Core.Models.ModelDb.AllCards.Where(c => c.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Ethereal)).Select(c => c.Id.Entry)));
        if (request.RootElement.TryGetProperty("hextechEnemySustainProbe", out var sustain))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemySustain(runner, scenario, sustain.GetDecimal());
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemyCoefficientProbe", out var coefficients) && coefficients.GetBoolean())
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemyCoefficients(runner, scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechEnemyOpeningProbe", out var openingEnemy))
        {
            var runner = (UnattendedTestRunner)AccessTools.Field(__instance.GetType(), "<runner>P").GetValue(__instance)!;
            __result = VerifyEnemyOpening(runner, scenario, openingEnemy.GetString()!);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechInitialExpected", out var initial))
        {
            foreach (var item in initial.EnumerateObject())
            {
                int actual = Observe(scenario, item.Name);
                if (actual != item.Value.GetInt32()) throw new Exception($"Initial Hextech effect {item.Name}: expected={item.Value}, actual={actual}");
                GD.Print($"HEXTECH_INITIAL_VERIFIED {item.Name}={actual}");
            }
        }
        if (request.RootElement.TryGetProperty("hextechOneTurn", out var oneTurn) && oneTurn.GetBoolean())
        {
            __result = OneTurn(scenario);
            return false;
        }
        if (request.RootElement.TryGetProperty("hextechExpectedHealingMultiplier", out var multiplier))
        {
            decimal baseHeal = scenario.Player.PlayerCombatState!.Hand.Cards.Single().DynamicVars.Heal.BaseValue;
            _expectedHealedHp = Math.Min(scenario.Player.Creature.MaxHp,
                scenario.Player.Creature.CurrentHp + (int)Math.Floor(baseHeal * multiplier.GetDecimal()));
            GD.Print($"HEXTECH_HEAL_INPUT base={baseHeal} expected_hp={_expectedHealedHp}");
        }
        if (request.RootElement.TryGetProperty("hextechMountainSoulForkProbe", out var mountain) && mountain.GetBoolean())
            VerifyMountainSoulFork(scenario);
        if (request.RootElement.TryGetProperty("hextechProjectionProbe", out var projection))
            VerifyProjection(scenario, projection);
        if (request.RootElement.TryGetProperty("hextechPatchCompositionProbe", out var patchProbe) && patchProbe.GetBoolean())
            VerifyPatchComposition(scenario);
        if (!request.RootElement.TryGetProperty("hextechForkProbe", out var flag) || !flag.GetBoolean()) return true;
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var parentCombat = (SimulatedCombatState)parent.State.CombatState;
        var live = scenario.Player.Relics.OfType<SwiftAndSafeRune>().Single();
        var cloned = parentCombat.RelicsOf(scenario.Player).OfType<SwiftAndSafeRune>().Single();
        var stateType = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.DrawCountState", throwOnError: true)!;
        var getState = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType);
        var countField = stateType.GetField("Count", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object GetState(CombatPredictionSimulator sim) => getState.Invoke(null, [sim, cloned])!;
        object before = GetState(parent), after = GetState(child), untouched = GetState(sibling);
        if (ReferenceEquals(before, after) || ReferenceEquals(after, untouched)) throw new Exception("Fork shared mutable counter state.");
        static StateFingerprint Fingerprint(CombatPredictionSimulator simulator)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)simulator.State.CombatState).AppendFingerprint(ref builder, simulator);
            return builder.Finish();
        }
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            scenario.Player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var originalKey = Fingerprint(parent);
        string originalStamp = Stamp(parent);
        int original = (int)countField.GetValue(before)!;
        countField.SetValue(after, original + 1);
        if (Fingerprint(child) == originalKey || Stamp(child) == originalStamp ||
            Fingerprint(parent) != originalKey || Stamp(parent) != originalStamp ||
            Fingerprint(sibling) != originalKey || Stamp(sibling) != originalStamp)
            throw new Exception("Fork mutation did not change only the child's full key and continuation.");
        int liveCount = live.SavedCardsDrawnThisCombat;
        try
        {
            live.SavedCardsDrawnThisCombat = liveCount + 13;
            if (Fingerprint(parent) != originalKey || Stamp(parent) != originalStamp ||
                (int)countField.GetValue(before)! != original)
                throw new Exception("Captured root read back live draw state.");
        }
        finally { live.SavedCardsDrawnThisCombat = liveCount; }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Fork probe changed the live combat, piles or RNG.");
        GD.Print("HEXTECH_FORK_VERIFIED root_frozen=true parent_isolated=true sibling_isolated=true key=true continuation=true live_rng_unchanged=true");
        return true;
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemySustain(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, decimal amount)
    {
        var live = scenario.CombatState;
        var player = scenario.Player;
        var enemy = live.Enemies.Single();
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(live), BattleDamageTracker.Observe(live),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null));
        StateFingerprint Key(CombatPredictionSimulator s)
        {
            return driver.BuildStateKey(root.StartTurnNumber, s.State.GetCreature(player.Creature),
                s.State.GetPlayerCombatState(player), (SimulatedCombatState)s.State.CombatState, s, 0, new HashSet<uint>());
        }
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var originalKey = Key(parent);
        string originalStamp = Stamp(parent);
        child.Heal(enemy, amount);
        if (Key(child) == originalKey || Stamp(child) == originalStamp ||
            Key(parent) != originalKey || Stamp(parent) != originalStamp ||
            Key(sibling) != originalKey || Stamp(sibling) != originalStamp ||
            ContinuationStamp.CaptureLive(live).StateText != before)
            throw new Exception("Enemy heal/queue branch mutated its parent, sibling or live state, or omitted its key/continuation.");
        var sim = (SimulatedCombatState)child.State.CombatState;
        await CreatureCmd.Heal(enemy, amount);
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, sim, player, enemy),
            UnattendedTestRunner.CaptureActual(live, player, enemy), "HextechEnemySustain", "Heal");
        child.GainBlock(enemy, amount, ValueProp.Unpowered);
        await CreatureCmd.GainBlock(enemy, amount, ValueProp.Unpowered, null);
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, sim, player, enemy),
            UnattendedTestRunner.CaptureActual(live, player, enemy), "HextechEnemySustain", "Block");
        GD.Print($"HEXTECH_ENEMY_SUSTAIN_VERIFIED hp={enemy.CurrentHp} block={enemy.Block} branch_key=true continuation=true live_rng_unchanged=true");
        // Root is recaptured with any delayed heal still pending. A real full
        // round then verifies capture, next-turn consumption and queue removal.
        await runner.AssertReportRoundAsync(live, player);
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }

    private static void UnreviewedCardPostfix() { }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyUnsupportedManual(
        UnattendedTestRunner.ScenarioContext scenario, string expected)
    {
        var player = scenario.Player;
        var combat = scenario.CombatState;
        if (!ContinuationStamp.CaptureLive(combat).StateText.StartsWith("hextech_prediction_unavailable:", StringComparison.Ordinal))
            throw new Exception("Unsupported live observation did not produce an explicit unavailable stamp.");
        bool rejected = false;
        try { _ = CombatRootSnapshot.Capture(combat); }
        catch (PredictionUnsupportedException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { rejected = true; }
        if (!rejected) throw new Exception("Unsupported content was accepted for search.");
        int hp = combat.Enemies.Single().CurrentHp;
        var card = player.PlayerCombatState!.Hand.Cards.Single();
        if (!card.TryManualPlay(combat.Enemies.Single())) throw new Exception("Unsupported search blocked manual play.");
        await MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (combat.Enemies.Single().CurrentHp != hp - 6)
            throw new Exception("Manual Strike did not resolve after search rejection.");
        int turn = player.PlayerCombatState.TurnNumber;
        CombatManager.Instance.OnEndedTurnLocally();
        MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, turn));
        await MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        long deadline = System.Environment.TickCount64 + 15_000;
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
        {
            if (System.Environment.TickCount64 > deadline) throw new Exception("Native turn failed to resume after unsupported search.");
            var host = NGame.Instance!;
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (!CombatManager.Instance.IsInProgress) throw new Exception("Unsupported manual fixture unexpectedly ended combat.");
        GD.Print($"HEXTECH_UNSUPPORTED_MANUAL_VERIFIED rejected=true played=true turn={player.PlayerCombatState.TurnNumber}");
        return new(false, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }

    private static void VerifyPatchComposition(UnattendedTestRunner.ScenarioContext scenario)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var frozen = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!;
        string frozenStamp = frozen.Stamp;
        var target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(MegaCrit.Sts2.Core.Models.Cards.Survivor))!;
        var extra = AccessTools.Method(typeof(FixtureAssertions), nameof(UnreviewedCardPostfix));
        var harmony = new Harmony("HextechCompatLab.UnreviewedComposition");
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(extra));
            if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText == liveBefore ||
                frozen.Stamp != frozenStamp || ((SimulatedCombatState)child.State.CombatState).AdaptedOnPlay!.Stamp != frozenStamp)
                throw new Exception("OnPlay patch change failed to invalidate live continuation or changed a frozen branch.");
            bool rejected = false;
            try { _ = CombatRootSnapshot.Capture(scenario.CombatState); }
            catch (PredictionUnsupportedException) { rejected = true; }
            if (!rejected) throw new Exception("An unreviewed Survivor patch combination was accepted.");
        }
        finally { harmony.Unpatch(target, extra); }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("OnPlay patch composition probe changed live combat or RNG after restoration.");
        GD.Print("HEXTECH_PATCH_COMPOSITION_VERIFIED unknown_rejected=true root_frozen=true child_frozen=true restored=true");
    }

    private static void VerifyMountainSoulFork(UnattendedTestRunner.ScenarioContext scenario)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        var live = scenario.Player.Relics.OfType<MountainSoulRune>().Single();
        var clone = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(scenario.Player).OfType<MountainSoulRune>().Single();
        var stateType = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.MountainSoulState", throwOnError: true)!;
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType);
        object State(CombatPredictionSimulator s) => get.Invoke(null, [s, clone])!;
        StateFingerprint Key(CombatPredictionSimulator s)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)s.State.CombatState).AppendFingerprint(ref builder, s);
            return builder.Finish();
        }
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            scenario.Player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var originalKey = Key(parent);
        string originalStamp = Stamp(parent);
        foreach (string name in new[] { "Damaged", "PreviousTurn" })
        {
            var child = parent.Fork();
            var field = stateType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            object childState = State(child);
            if (ReferenceEquals(State(parent), childState) || ReferenceEquals(State(sibling), childState))
                throw new Exception("Mountain Soul shared mutable state across branches.");
            field.SetValue(childState, !(bool)field.GetValue(childState)!);
            if (Key(child) == originalKey || Stamp(child) == originalStamp ||
                Key(parent) != originalKey || Stamp(parent) != originalStamp ||
                Key(sibling) != originalKey || Stamp(sibling) != originalStamp)
                throw new Exception($"Mountain Soul field {name} did not isolate fingerprint and continuation changes.");
        }
        bool damaged = live.SavedTookUnblockedDamageSinceLastTurn, previous = live.SavedHasPreviousTurn;
        try
        {
            live.SavedTookUnblockedDamageSinceLastTurn = !damaged;
            live.SavedHasPreviousTurn = !previous;
            if (Key(parent) != originalKey || Stamp(parent) != originalStamp)
                throw new Exception("Mountain Soul root read mutable live fields after capture.");
        }
        finally
        {
            live.SavedTookUnblockedDamageSinceLastTurn = damaged;
            live.SavedHasPreviousTurn = previous;
        }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Mountain Soul branch probe changed the live combat.");
        GD.Print("HEXTECH_MOUNTAIN_FORK_VERIFIED fields=2 root_frozen=true siblings=true key=true continuation=true live_rng_unchanged=true");
    }

    private static void VerifyProjection(UnattendedTestRunner.ScenarioContext scenario, JsonElement expected)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        (int draw, int energy) Read(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return (PersistentPowerSupport.GetModifiedHandDraw(combat, scenario.Player, 5),
                PersistentPowerSupport.GetModifiedMaxEnergy(combat, scenario.Player));
        }
        var baseline = (expected.GetProperty("draw").GetInt32(), expected.GetProperty("energy").GetInt32());
        if (Read(parent) != baseline) throw new Exception($"Projection root expected={baseline} actual={Read(parent)}");
        int liveRound = scenario.CombatState.RoundNumber;
        List<(object relic, PropertyInfo property, object? value)> saved = [];
        try
        {
            scenario.CombatState.RoundNumber += 20;
            foreach (var relic in scenario.Player.Relics)
            {
                foreach (string member in new[] { "SavedStacks", "SavedCombatVictories" })
                {
                    var property = relic.GetType().GetProperty(member);
                    if (property?.PropertyType != typeof(int) || property.SetMethod == null) continue;
                    object? value = property.GetValue(relic);
                    saved.Add((relic, property, value));
                    property.SetValue(relic, (int)value! + 100);
                }
            }
            if (Read(parent) != baseline || Read(sibling) != baseline)
                throw new Exception("Frozen draw/energy query read the live round or live rune stacks.");
        }
        finally
        {
            scenario.CombatState.RoundNumber = liveRound;
            foreach (var (relic, property, value) in saved) property.SetValue(relic, value);
        }
        if (expected.TryGetProperty("branchMaxHp", out var hp))
        {
            child.State.GetCreature(scenario.Player.Creature).SetMaxHp(hp.GetInt32());
            var wanted = (expected.GetProperty("childDraw").GetInt32(), expected.GetProperty("childEnergy").GetInt32());
            if (Read(child) != wanted || Read(parent) != baseline || Read(sibling) != baseline)
                throw new Exception($"Branch HP projection expected={wanted} actual={Read(child)}");
        }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Projection probe changed real combat or random state.");
        GD.Print($"HEXTECH_PROJECTION_VERIFIED draw={baseline.Item1} energy={baseline.Item2} root_frozen=true sibling=true live_rng_unchanged=true");
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> OneTurn(UnattendedTestRunner.ScenarioContext scenario)
    {
        var host = NGame.Instance!;
        var combat = scenario.CombatState;
        var player = scenario.Player;
        async Task WaitFor(Func<bool> done)
        {
            long deadline = System.Environment.TickCount64 + 15_000;
            while (!done())
            {
                if (System.Environment.TickCount64 > deadline) throw new Exception("One-turn controller fixture timed out.");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        SolverController.BeginCombat(combat);
        SolverController.SetAutomaticCalculationEnabled(false, persist: false);
        string beforeSearch = ContinuationStamp.CaptureLive(combat).StateText;
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        await WaitFor(() => !SolverController.IsSearching && SolverController.CanExecuteCurrentTurn);
        if (ContinuationStamp.CaptureLive(combat).StateText != beforeSearch)
            throw new Exception("Advice search changed live combat or RNG.");
        SolverController.RequestDeploy(host, combat);
        await WaitFor(() => player.PlayerCombatState is { TurnNumber: 2, Phase: PlayerTurnPhase.Play }
            && !SolverController.IsDeploying);
        string turnTwo = ContinuationStamp.CaptureLive(combat).StateText;
        await Task.Delay(500);
        if (SolverController.FullAutoEnabled || !CombatManager.Instance.IsInProgress ||
            combat.Enemies.Single().CurrentHp != 12 || SolverController.UnexpectedReplanCountForTesting != 0 ||
            ContinuationStamp.CaptureLive(combat).StateText != turnTwo)
            throw new Exception("One-turn mode crossed its boundary or disagreed with prediction.");
        GD.Print("HEXTECH_ONE_TURN_VERIFIED advice_live_rng_unchanged=true turn=2 enemy_hp=12 full_auto=false replans=0");
        return new(false, 2, true, false, false, false);
    }

    private static int Observe(UnattendedTestRunner.ScenarioContext scenario, string name)
    {
        var player = scenario.Player;
        if (name.StartsWith("power:", StringComparison.Ordinal))
            return player.Creature.Powers.Where(p => p.Id.Entry == name[6..]).Sum(p => p.Amount);
        if (name.StartsWith("enemyThreshold:", StringComparison.Ordinal))
        {
            var tracking = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single().CombatTracking;
            string field = name[15..];
            object value = AccessTools.Field(typeof(HextechMayhemCombatTrackingState), field).GetValue(tracking)!;
            return value is HashSet<uint> set ? set.Count : ((Dictionary<uint, int>)value).Values.Sum();
        }
        if (name.StartsWith("enemyDraw:", StringComparison.Ordinal))
        {
            var tracking = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single().CombatTracking;
            return ((Dictionary<ulong, int>)AccessTools.Field(typeof(HextechMayhemCombatTrackingState), name[10..]).GetValue(tracking)!).Values.Sum();
        }
        if (name.StartsWith("enemyPowerTotal:", StringComparison.Ordinal))
            return scenario.CombatState.Enemies.SelectMany(enemy => enemy.Powers).Where(p => p.Id.Entry == name[16..]).Sum(p => p.Amount);
        if (name.StartsWith("enemyPower:", StringComparison.Ordinal))
            return scenario.CombatState.Enemies.Single().Powers.Where(p => p.Id.Entry == name[11..]).Sum(p => p.Amount);
        return name switch
        {
            "playerBlock" => player.Creature.Block,
            "playerHp" => player.Creature.CurrentHp,
            "playerMaxHp" => player.Creature.MaxHp,
            "playerGold" => player.Gold,
            "enemyHp" => scenario.CombatState.Enemies.Single().CurrentHp,
            "enemyHpTotal" => scenario.CombatState.Enemies.Sum(enemy => enemy.CurrentHp),
            "enemyBlock" => scenario.CombatState.Enemies.Single().Block,
            "artifact" => player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.ArtifactPower>()?.Amount ?? 0,
            "strength" => player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>()?.Amount ?? 0,
            "thorns" => player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.ThornsPower>()?.Amount ?? 0,
            "focus" => player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.FocusPower>()?.Amount ?? 0,
            "stars" => player.PlayerCombatState!.Stars,
            "handCount" => player.PlayerCombatState!.Hand.Cards.Count,
            "freeHandLocalNonX" => player.PlayerCombatState!.Hand.Cards.Count(card =>
                !card.EnergyCost.CostsX && card.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.Local) == 0),
            "exhaustCount" => player.PlayerCombatState!.ExhaustPile.Cards.Count,
            "discardCount" => player.PlayerCombatState!.DiscardPile.Cards.Count,
            "playerEnergy" => player.PlayerCombatState!.Energy,
            "handUpgradeTotal" => player.PlayerCombatState!.Hand.Cards.Sum(card => card.CurrentUpgradeLevel),
            "afflictedCardCount" => player.PlayerCombatState!.AllCards.Count(card => card.Affliction is not null),
            "orbCapacity" => player.PlayerCombatState!.OrbQueue.Capacity,
            "lightningOrbs" => player.PlayerCombatState!.OrbQueue.Orbs.OfType<MegaCrit.Sts2.Core.Models.Orbs.LightningOrb>().Count(),
            "darkEvoke" => (int)player.PlayerCombatState!.OrbQueue.Orbs.OfType<MegaCrit.Sts2.Core.Models.Orbs.DarkOrb>().Sum(o => o.EvokeVal),
            "ostyHp" => player.Osty?.CurrentHp ?? 0,
            "ostyMaxHp" => player.Osty?.MaxHp ?? 0,
            "drawCount" => player.Relics.OfType<SwiftAndSafeRune>().Single().SavedCardsDrawnThisCombat,
            "archmageRolls" => (int)AccessTools.Field(typeof(ArchmageRune), "_freeCardRollsThisCombat").GetValue(player.Relics.OfType<ArchmageRune>().Single())!,
            "twiceThriceAttacks" => player.Relics.OfType<TwiceThriceRune>().Single().SavedAttacksPlayedThisCombat,
            _ => throw new Exception($"Unknown expected observation {name}")
        };
    }

    private static void AfterExecute(UnattendedTestRunner.ScenarioContext scenario,
        ref Task<UnattendedTestRunner.ExecutionOutcome> __result) => __result = VerifyOutcome(__result, scenario);

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyOutcome(
        Task<UnattendedTestRunner.ExecutionOutcome> pending, UnattendedTestRunner.ScenarioContext scenario)
    {
        var outcome = await pending;
        if (_expectedHealedHp is int hp)
        {
            if (scenario.Player.Creature.CurrentHp != hp) throw new Exception($"Healing multiplier expected HP {hp}, actual {scenario.Player.Creature.CurrentHp}");
            GD.Print($"HEXTECH_EFFECT_VERIFIED healing_hp={hp}");
        }
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechExpectedCardVariable", out var variableChecks))
        {
            foreach (var check in variableChecks.EnumerateArray())
            {
                var card = scenario.Player.PlayerCombatState!.AllCards.Single(c => c.Id.Entry == check.GetProperty("cardId").GetString());
                string variable = check.GetProperty("variable").GetString()!;
                decimal actual = card.DynamicVars[variable].BaseValue;
                if (actual != check.GetProperty("value").GetDecimal())
                    throw new Exception($"Card variable {card.Id.Entry}.{variable} actual={actual}, expected={check.GetProperty("value")}");
                GD.Print($"HEXTECH_CARD_VARIABLE_VERIFIED card={card.Id.Entry} variable={variable} value={actual}");
            }
        }
        if (request.RootElement.TryGetProperty("hextechExpected", out var expected))
        {
            foreach (var item in expected.EnumerateObject())
            {
                int actual = Observe(scenario, item.Name);
                if (actual != item.Value.GetInt32()) throw new Exception($"Hextech effect did not occur: {item.Name} expected={item.Value} actual={actual}");
                GD.Print($"HEXTECH_EFFECT_VERIFIED {item.Name}={actual}");
            }
        }
        if (request.RootElement.TryGetProperty("hextechAttackResultProbe", out var attackResultProbe) && attackResultProbe.GetBoolean())
            VerifyAttackResults();
        return outcome;
    }
}
