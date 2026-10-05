using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Combat;
using System.Text.Json;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private sealed record NativePlayStep(string CardId, bool Enemy, int? ExpectedPlays = null, string[]? ChoiceCardIds = null);

    private static NativePlayStep[] ReadNativeSequence(JsonElement request, string property)
    {
        var items = request.GetProperty(property).EnumerateArray().Select(item =>
            new NativePlayStep(item.GetProperty("cardId").GetString()!,
                item.TryGetProperty("enemy", out var enemy) && enemy.GetBoolean(),
                item.TryGetProperty("expectedPlays", out var plays) ? plays.GetInt32() : null,
                item.TryGetProperty("choiceCardIds", out var choices)
                    ? choices.EnumerateArray().Select(choice => choice.GetString()!).ToArray() : null)).ToArray();
        if (items.Length is < 1 or > 16 || items.Any(item => string.IsNullOrWhiteSpace(item.CardId)))
            throw new Exception("Native sequence requires 1..16 explicit card IDs.");
        return items;
    }

    private static NativePlayStep[] NativeSequence(JsonElement request, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        if (request.TryGetProperty("hextechNativePlaySequence", out _))
            return ReadNativeSequence(request, "hextechNativePlaySequence");
        var card = player.PlayerCombatState!.Hand.Cards.Single();
        bool enemy = request.TryGetProperty("hextechNativePlayEnemy", out var target) && target.GetBoolean();
        return Enumerable.Repeat(new NativePlayStep(card.Id.Entry, enemy), NativePlayCount(request)).ToArray();
    }
    private static int NativePlayCount(System.Text.Json.JsonElement request)
    {
        int count = request.TryGetProperty("hextechNativePlayCount", out var value) ? value.GetInt32() : 1;
        if (count is < 1 or > 4) throw new Exception("Native replay fixture requires 1..4 plays.");
        return count;
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeDupe(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var player = scenario.Player;
        var original = player.PlayerCombatState!.Hand.Cards.Single();
        var dupe = original.CreateDupe(player);
        await CardPileCmd.RemoveFromCombat(original);
        await CardPileCmd.Add(dupe, PileType.Hand);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (!dupe.IsDupe || dupe.CurrentUpgradeLevel != original.CurrentUpgradeLevel
            || player.PlayerCombatState.Hand.Cards.Single() != dupe)
            throw new Exception("Native CreateDupe fixture setup did not complete.");
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        if (player.PlayerCombatState.AllCards.Contains(dupe))
            throw new Exception("Native duplicate survived its None result destination.");
        GD.Print("HEXTECH_NATIVE_DUPE_VERIFIED native_create_dupe=true removed_from_combat=true no_ghost_hand=true");
        return outcome;
    }

    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeTokenActual(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemies = combat.Enemies.ToArray();
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeEnemyLifecycleProbe", out var lifecycleProbe) && lifecycleProbe.GetBoolean())
            GD.Print("HEXTECH_NATIVE_LIFECYCLE_ROOT " + string.Join(",", enemies.Select(enemy => $"{enemy.Monster?.Id.Entry}:{enemy.CurrentHp}/{enemy.MaxHp}")));
        var steps = NativeSequence(request.RootElement, player);
        if (request.RootElement.TryGetProperty("hextechNativeGeneratedBatchProbe", out var generatedBatch) && generatedBatch.GetBoolean())
            await VerifyNativeGeneratedBatch(runner, scenario);
        if (request.RootElement.TryGetProperty("hextechNativeOmniOpening", out var omniOpening) && omniOpening.GetBoolean())
        {
            var before = player.PlayerCombatState!.AllCards.ToHashSet();
            await player.Relics.OfType<HextechRunes.OmniDragonSoulRune>().Single().BeforeCombatStart();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var generated = player.PlayerCombatState.AllCards.Where(card => !before.Contains(card)).ToArray();
            if (generated.Length != 3 || generated.Select(card => card.Id).Distinct().Count() != 3
                || generated.Any(card => !card.IsUpgraded || card.Owner != player || card.Pile?.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand))
                throw new Exception("Native Omni opening must generate three distinct upgraded owned dragon-soul cards in hand.");
            steps = generated.Select(card => new NativePlayStep(card.Id.Entry, false, 1)).Concat(steps).ToArray();
            GD.Print("HEXTECH_NATIVE_OMNI_OPENING_VERIFIED original_callback=true distinct_upgraded_cards=3 captured_after_opening=true");
        }

        bool starterVictory = request.RootElement.TryGetProperty("hextechNativeStarterVictoryProbe", out var starterProbe) && starterProbe.GetBoolean();
        var startingDeckLevels = player.Deck.Cards.ToDictionary(card => card, card => card.CurrentUpgradeLevel);
        HashSet<CardModel> playedDeckCards = [];
        var nativeRoot = CombatRootSnapshot.Capture(combat);
        var simulator = nativeRoot.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (request.RootElement.TryGetProperty("hextechNativeProgressStart", out var progressStart))
            await VerifyNativeProgressStart(runner, scenario, simulator, nativeRoot, progressStart);
        async Task PlaySequence(NativePlayStep[] sequence)
        {
            for (int play = 0; play < sequence.Length; play++)
            {
                var step = sequence[play];
                var card = player.PlayerCombatState!.Hand.Cards.FirstOrDefault(card => card.Id.Entry == step.CardId)
                    ?? throw new Exception($"Native sequence cannot find {step.CardId} in hand on play {play + 1}.");
                var target = step.Enemy ? enemies.First() : null;
                // Generated cards are distinct model instances in the two
                // engines. The preceding full snapshot establishes pile order;
                // resolve their actual hand position rather than assuming a
                // shared original reference from the initial root.
                var actualHand = player.PlayerCombatState.Hand.Cards.ToArray();
                var predictedHand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
                if (actualHand.Length != predictedHand.Count || actualHand.Where((actual, index) =>
                    actual.Id != predictedHand[index].Preview.Id
                    || actual.CurrentUpgradeLevel != predictedHand[index].Preview.CurrentUpgradeLevel).Any())
                    throw new Exception("Native sequence cannot align the actual and predicted hand before play.");
                var predictedCard = predictedHand[Array.IndexOf(actualHand, card)];
                if (starterVictory) playedDeckCards.Add(card.DeckVersion ?? card);
                PlannedCardSelector? nativeSelector = null;
                if (step.ChoiceCardIds is { } requested)
                {
                    var spec = CardChoiceSupport.GetSpec(simulator, predictedCard)
                        ?? throw new Exception("Explicit native selection has no reviewed card choice specification.");
                    // This pre-play inspection still sees the played card in
                    // Hand. Native CardCmd moves it to Play before a hand choice;
                    // the simulator's actual choice request already does likewise.
                    if (spec.SourcePile == PileType.Hand)
                        spec = spec with
                        {
                            Options = spec.Options.Where(option => !ReferenceEquals(option.Original, predictedCard.Original)).ToArray(),
                            SourceCards = spec.SourceCards.Where(option => !ReferenceEquals(option.Original, predictedCard.Original)).ToArray()
                        };
                    nativeSelector = new PlannedCardSelector(CardChoiceSupport.BuildRequestedChoice(spec, requested));
                    nativeSelector.CaptureBefore(player);
                }
                bool instantProbe = request.RootElement.TryGetProperty("hextechNativeInstantProbe", out var instantFlag) && instantFlag.GetBoolean();
                if (instantProbe) { InstallNativeInstantProbe(); _ownedInstantQueueCalls = 0; OwnedInstantQueueHps.Clear(); ActualInstantQueueHps.Clear(); AssertNativeInstantGlobalQueueEmpty(); }
                UnattendedTestRunner.PlaySimulatedCard(simulator, shadow, predictedCard, target, enemies, step.ChoiceCardIds);
                if (instantProbe) AssertNativeInstantGlobalQueueEmpty();
                if (simulator.HasPendingChoice)
                    throw new Exception("Native token fixture requires an explicit completed selection plan before snapshot comparison.");
                int before = CombatManager.Instance.History.CardPlaysStarted.Count(entry => ReferenceEquals(entry.CardPlay.Card, card));
                using (nativeSelector is null ? null : CardSelectCmd.PushSelector(nativeSelector, localOnly: true))
                {
                    if (!card.TryManualPlay(target)) throw new Exception($"Native token card was not playable on play {play + 1}.");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    if (nativeSelector is not null)
                    {
                        nativeSelector.ReconcileImplicitChoices(player);
                        nativeSelector.AssertConsumed();
                    }
                }
                int actualPlays = CombatManager.Instance.History.CardPlaysStarted.Count(entry => ReferenceEquals(entry.CardPlay.Card, card)) - before;
                if (step.ExpectedPlays is int expected && actualPlays != expected)
                    throw new Exception($"Native replay count for {step.CardId}: expected {expected}, actual {actualPlays}.");
                if (step.ExpectedPlays.HasValue)
                    GD.Print($"HEXTECH_NATIVE_PLAY_COUNT_VERIFIED card={step.CardId} executions={actualPlays}");
                if (starterVictory && !CombatManager.Instance.IsInProgress)
                {
                    // Native victory clears block and combat piles. Prediction
                    // retains its terminal battle state for scoring. This fixture's
                    // native Burning Blood heals after victory (audited pinned IL),
                    // before the deck-upgrade outcome is inspected.
                    var terminal = simulator.State.GetCreature(player.Creature);
                    int expectedAfterVictoryHp = Math.Min(terminal.MaxHp, terminal.CurrentHp
                        + player.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.BurningBlood>().Sum(relic => relic.DynamicVars.Heal.IntValue));
                    if (enemies.Any(enemy => !enemy.IsDead || simulator.State.GetCreature(enemy).IsAlive)
                        || expectedAfterVictoryHp != player.Creature.CurrentHp || terminal.MaxHp != player.Creature.MaxHp
                        || player.Creature.Block != 0)
                        throw new Exception($"Starter victory outcome differs after native cleanup: predictedHp={terminal.CurrentHp}/{terminal.MaxHp} actualHp={player.Creature.CurrentHp}/{player.Creature.MaxHp} actualBlock={player.Creature.Block} enemies="
                            + string.Join(',', enemies.Select(enemy => $"{enemy.CurrentHp}:{enemy.IsDead}:{simulator.State.GetCreature(enemy).CurrentHp}:{simulator.State.GetCreature(enemy).IsAlive}")));
                    AssertNativeGoldModels(simulator, combat);
                    foreach (var deckCard in player.Deck.Cards)
                    {
                        bool upgraded = playedDeckCards.Contains(deckCard)
                            && (HextechRunes.StrikeUpgradeRune.IsBasicStrike(deckCard)
                                && player.Relics.OfType<HextechRunes.StrikeUpgradeRune>().Any()
                                || HextechRunes.DefendUpgradeRune.IsBasicDefend(deckCard)
                                && player.Relics.OfType<HextechRunes.DefendUpgradeRune>().Any());
                        int expectedDeckLevel = startingDeckLevels[deckCard] + (upgraded ? 1 : 0);
                        if (deckCard.CurrentUpgradeLevel != expectedDeckLevel)
                            throw new Exception($"Native starter victory deck upgrade differs: {deckCard.Id.Entry} expected={expectedDeckLevel} actual={deckCard.CurrentUpgradeLevel}.");
                    }
                    GD.Print("HEXTECH_NATIVE_STARTER_VICTORY_VERIFIED original_cleanup=true played_deck_cards_upgrade_once=true untouched_cards_preserved=true");
                }
                else if (request.RootElement.TryGetProperty("hextechNativePlayerDeathProbe", out var playerDeath) && playerDeath.GetBoolean())
                    AssertNativePlayerDeath(simulator, combat, player, enemies);
                else if (request.RootElement.TryGetProperty("hextechNativeVictoryGoldProbe", out var victoryGold) && victoryGold.GetBoolean())
                    AssertNativeVictoryGold(simulator, combat, player, enemies);
                else foreach (var enemy in enemies)
                        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                            UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechNativeToken", $"NativePlayAllEnemies:{play + 1}");
                if (request.RootElement.TryGetProperty("hextechNativeGrowthProbe", out _))
                    AssertNativeGrowthModels(simulator, combat);
                AssertNativeGoldModels(simulator, combat);
                if (instantProbe) AssertNativeInstantAfterPlay(simulator, combat, enemies.First());
                AssertNativeDamageWitness(simulator, combat, player);
                AssertNativeSweepingWitness(combat);
                AssertNativeGeneratedPotionSlots(simulator, combat, player);
                if (CombatManager.Instance.IsInProgress && request.RootElement.TryGetProperty("hextechNativeScopeCounterProbe", out var scopeCounters) && scopeCounters.GetBoolean())
                    AssertNativeScalarModels(simulator, combat);
                if (request.RootElement.TryGetProperty("hextechNativeGeneratedTagsProbe", out var generatedTags) && generatedTags.GetBoolean())
                    AssertNativeGeneratedTags(simulator, combat);
                if (request.RootElement.TryGetProperty("hextechNativeDiscardQueueProbe", out var queueProbe) && queueProbe.GetBoolean())
                    AssertNativeDiscardModels(simulator, combat);
            }
        }
        if (request.RootElement.TryGetProperty("hextechNativePotionSequence", out var potionSequence))
        {
            foreach (var entry in potionSequence.EnumerateArray())
            {
                int slot = entry.GetProperty("slot").GetInt32();
                var target = entry.TryGetProperty("enemy", out var potionEnemy) && potionEnemy.GetBoolean() ? enemies.First() : null;
                var predictedPotion = shadow.GetPotionAtSlot(player, slot) ?? throw new Exception("Predicted potion slot missing.");
                if (!UseNativeTestPotion(simulator, shadow, predictedPotion, slot, target) || simulator.HasPendingChoice)
                    throw new Exception("Potion native differential requires completed use with no pending choice.");
                player.GetPotionAtSlotIndex(slot)!.EnqueueManualUse(target);
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                foreach (var enemy in enemies)
                    runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                        UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechNativePotion", "UsePotion:" + slot);
                AssertNativeScalarModels(simulator, combat);
            }
            GD.Print("HEXTECH_NATIVE_POTION_SEQUENCE_VERIFIED original_manual_potion_use=true full_snapshots=true");
        }
        await PlaySequence(steps);
        if (request.RootElement.TryGetProperty("hextechNativeCardLimitProbe", out var limitProbe) && limitProbe.GetBoolean())
        {
            var remaining = player.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == "STRIKE_SILENT");
            var predicted = simulator.State.GetPlayerCombatState(player).Hand.Cards[Array.IndexOf(player.PlayerCombatState.Hand.Cards.ToArray(), remaining)];
            var automatic = Enum.GetValues<AutoPlayType>().First(value => value != AutoPlayType.None);
            var modifier = combat.Modifiers.OfType<HextechRunes.HextechMayhemModifier>().Single();
            if (modifier.ShouldPlay(remaining, AutoPlayType.None)
                || CombatSolver.Engine.InCombat.Mirrors.HookMirrors.ShouldPlay(simulator, predicted, out _, AutoPlayType.None)
                || !modifier.ShouldPlay(remaining, automatic)
                || !CombatSolver.Engine.InCombat.Mirrors.HookMirrors.ShouldPlay(simulator, predicted, out _, automatic))
                throw new Exception("Native BackToBasics manual limit or automatic-play exemption differs.");
            GD.Print("HEXTECH_NATIVE_CARD_LIMIT_VERIFIED manual_blocked=true autoplay_allowed=true original_counter=true");
        }

        if (request.RootElement.TryGetProperty("hextechNativeSkillLimitProbe", out var skillProbe) && skillProbe.GetBoolean())
        {
            var remaining = player.PlayerCombatState!.Hand.Cards.First(card => card.Type == CardType.Skill);
            var predicted = simulator.State.GetPlayerCombatState(player).Hand.Cards[Array.IndexOf(player.PlayerCombatState.Hand.Cards.ToArray(), remaining)];
            var modifier = combat.Modifiers.OfType<HextechRunes.HextechMayhemModifier>().Single();
            foreach (var auto in Enum.GetValues<AutoPlayType>())
                if (modifier.ShouldPlay(remaining, auto) || CombatSolver.Engine.InCombat.Mirrors.HookMirrors.ShouldPlay(simulator, predicted, out _, auto))
                    throw new Exception("Native LivingFog skill limit must reject manual and automatic cards.");
            GD.Print("HEXTECH_NATIVE_SKILL_LIMIT_VERIFIED manual_and_autoplay_blocked=true source_counter=true");
        }

        if (request.RootElement.TryGetProperty("hextechNativeInfernalStateProbe", out var infernal) && infernal.GetBoolean())
            VerifyNativeInfernalStateIsolation(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeSoulProjectionProbe", out var soulProjection) && soulProjection.GetBoolean())
            VerifyNativeSoulProjection(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeMentalShieldProbe", out var mentalShield))
            await VerifyNativeMentalShield(runner, scenario, mentalShield.GetInt32());
        if (request.RootElement.TryGetProperty("hextechNativeAttributeChangeProbe", out var attributeChange) && attributeChange.GetBoolean())
            await VerifyNativeAttributeChange(scenario, runner);
        if (request.RootElement.TryGetProperty("hextechNativeExpectedAfterSequence", out var expectedSequence))
        {
            foreach (var check in expectedSequence.EnumerateObject())
            {
                int actual = Observe(scenario, check.Name);
                if (actual != check.Value.GetInt32()) throw new Exception($"Native event sequence {check.Name}: expected={check.Value} actual={actual}.");
            }
            GD.Print("HEXTECH_NATIVE_SEQUENCE_EFFECTS_VERIFIED native_effects=true");
        }
        if (request.RootElement.TryGetProperty("hextechTemporaryDexterityBoundaryProbe", out var dexterityBoundary)
            && dexterityBoundary.GetBoolean())
            await VerifyTemporaryDexterityBoundary(scenario);
        if (request.RootElement.TryGetProperty("hextechNativeRoundsAfterPlay", out var rounds))
        {
            for (int i = 0; i < rounds.GetInt32(); i++)
                if (request.RootElement.TryGetProperty("hextechNativeEndTurnOnlyRounds", out var endOnly) && endOnly.GetBoolean())
                    await VerifyNativeEndOnlyRound(runner, combat, player);
                else await runner.AssertReportRoundAsync(combat, player);
            GD.Print($"HEXTECH_NATIVE_POST_PLAY_ROUNDS_VERIFIED rounds={rounds.GetInt32()} full_native_snapshots=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeExpectedAfterRounds", out var expectedAfterRounds))
        {
            foreach (var check in expectedAfterRounds.EnumerateObject())
            {
                int actual = Observe(scenario, check.Name);
                if (actual != check.Value.GetInt32())
                    throw new Exception($"Native future-round effect {check.Name}: expected={check.Value} actual={actual}.");
            }
            GD.Print("HEXTECH_NATIVE_FUTURE_EFFECTS_VERIFIED native_effects=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativePlaySequenceAfterRounds", out _))
        {
            if (!request.RootElement.TryGetProperty("hextechNativeRoundsAfterPlay", out var afterRounds) || afterRounds.GetInt32() < 1)
                throw new Exception("Native post-round sequence requires an actual intervening round.");
            var after = ReadNativeSequence(request.RootElement, "hextechNativePlaySequenceAfterRounds");
            VerifyNativeTokenFork(scenario, after);
            simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            shadow = (SimulatedCombatState)simulator.State.CombatState;
            await PlaySequence(after);
            GD.Print($"HEXTECH_NATIVE_POST_ROUND_SEQUENCE_VERIFIED plays={after.Length} full_native_snapshots=true fork=true rng=true");
        }
        GD.Print($"HEXTECH_NATIVE_TOKEN_ACTUAL_VERIFIED enemies={combat.Enemies.Count} plays={steps.Length} all_enemy_snapshots=true rng=true native_manual_play=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }

    private static bool UseNativeTestPotion(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        MegaCrit.Sts2.Core.Models.PotionModel potion, int slot, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        // Match the pinned solver's real replay/search path. ManualUse alone
        // dispatches only the specialized OnUse registry, not ordinary potions.
        int historyStart = simulator.History.Entries.Count;
        return PotionExecutionSupport.Prepare(simulator, combat, potion, slot, target)
            && PotionExecutionSupport.Complete(simulator, combat, potion, target, null, historyStart, new HashSet<uint>());
    }

    private static void VerifyNativeTokenFork(UnattendedTestRunner.ScenarioContext scenario, NativePlayStep[]? sequenceOverride = null)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        using var request = Request();
        var steps = sequenceOverride ?? NativeSequence(request.RootElement, player);
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        string stamp = Stamp(parent);
        var key = Key(parent);
        void PlaySequence(CombatPredictionSimulator sim)
        {
            if (request.RootElement.TryGetProperty("hextechNativePotionSequence", out var potionSequence))
                foreach (var entry in potionSequence.EnumerateArray())
                {
                    var potion = ((SimulatedCombatState)sim.State.CombatState).GetPotionAtSlot(player, entry.GetProperty("slot").GetInt32())
                        ?? throw new Exception("Native fork potion slot missing.");
                    var target = entry.TryGetProperty("enemy", out var potionEnemy) && potionEnemy.GetBoolean() ? combat.Enemies.First() : null;
                    if (!UseNativeTestPotion(sim, (SimulatedCombatState)sim.State.CombatState, potion, entry.GetProperty("slot").GetInt32(), target) || sim.HasPendingChoice)
                        throw new Exception("Native fork potion use not complete.");
                }
            foreach (var step in steps)
            {
                var card = sim.State.GetPlayerCombatState(player).Hand.Cards.FirstOrDefault(card => card.Preview.Id.Entry == step.CardId)
                    ?? throw new Exception($"Predicted sequence cannot find {step.CardId} in hand.");
                UnattendedTestRunner.PlaySimulatedCard(sim, (SimulatedCombatState)sim.State.CombatState,
                    card, step.Enemy ? combat.Enemies.First() : null, combat.Enemies, step.ChoiceCardIds);
                if (sim.HasPendingChoice)
                    throw new Exception("Native token fork fixture requires an explicit completed selection plan.");
            }
        }
        PlaySequence(child);
        if (Key(child) == key || Stamp(child) == stamp
            || Key(parent) != key || Key(sibling) != key || Stamp(parent) != stamp || Stamp(sibling) != stamp
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Native token branch invariant failed: childKeyChanged=" + (Key(child) != key)
                + " childStampChanged=" + (Stamp(child) != stamp) + " parentKeyStable=" + (Key(parent) == key)
                + " siblingKeyStable=" + (Key(sibling) == key) + " parentStampStable=" + (Stamp(parent) == stamp)
                + " siblingStampStable=" + (Stamp(sibling) == stamp) + " liveStable=" + (ContinuationStamp.CaptureLive(combat).StateText == live)
                + " liveDifference=" + new ContinuationStamp(live).DescribeFirstDifference(ContinuationStamp.CaptureLive(combat)));

        if (request.RootElement.TryGetProperty("hextechNativeJeweledPendingQueryProbe", out var jeweledPending) && jeweledPending.GetBoolean())
        {
            var rune = player.Relics.OfType<HextechRunes.JeweledGauntletRune>().Single();
            var field = HarmonyLib.AccessTools.Field(rune.GetType(), "_pendingReplayRolls");
            var ordinal = HarmonyLib.AccessTools.Field(rune.GetType(), "_replayRollsThisCombat");
            var rolls = (Dictionary<MegaCrit.Sts2.Core.Models.CardModel, bool>)field.GetValue(rune)!;
            var saved = rolls.ToArray(); var savedOrdinal = ordinal.GetValue(rune);
            try
            {
                rolls.Clear(); foreach (var pair in saved.Reverse()) rolls.Add(pair.Key, !pair.Value);
                ordinal.SetValue(rune, 999);
                var replay = parent.Fork(); PlaySequence(replay);
                if (Key(replay) != Key(child) || Stamp(replay) != Stamp(child)
                    || Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp)
                    throw new Exception("Jeweled frozen pending-slot/ordinal fork invariant failed.");
            }
            finally
            {
                rolls.Clear(); foreach (var pair in saved) rolls.Add(pair.Key, pair.Value); ordinal.SetValue(rune, savedOrdinal);
            }
            GD.Print("HEXTECH_NATIVE_JEWELED_PENDING_VERIFIED multiple_card_slots=true frozen_ordinal=true detached_dictionary=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeSoulBirthFrozenProbe", out var soulFrozen) && soulFrozen.GetBoolean())
        {
            var field = HarmonyLib.AccessTools.Property(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "MonsterMaxHpBeforeModification");
            var saved = combat.Enemies.Select(enemy => (Enemy: enemy, Value: field.GetValue(enemy), Display: enemy.HpDisplay)).ToArray();
            try
            {
                foreach (var row in saved)
                {
                    field.SetValue(row.Enemy, 99999);
                    row.Enemy.HpDisplay = MegaCrit.Sts2.Core.Entities.Creatures.HpDisplay.InfiniteWithoutNumbers;
                }
                var replay = parent.Fork(); PlaySequence(replay);
                if (Key(replay) != Key(child) || Stamp(replay) != Stamp(child) || Key(parent) != key
                    || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp)
                    throw new Exception("SoulEater frozen metadata invariant failed: replayKey=" + (Key(replay) == Key(child))
                        + " replayStamp=" + (Stamp(replay) == Stamp(child)) + " parentKey=" + (Key(parent) == key)
                        + " parentStamp=" + (Stamp(parent) == stamp) + " siblingKey=" + (Key(sibling) == key)
                        + " siblingStamp=" + (Stamp(sibling) == stamp) + " replayDifference="
                        + new ContinuationStamp(Stamp(child)).DescribeFirstDifference(new ContinuationStamp(Stamp(replay)))
                        + " parentDifference=" + new ContinuationStamp(stamp).DescribeFirstDifference(new ContinuationStamp(Stamp(parent))));
            }
            finally
            {
                foreach (var row in saved) { field.SetValue(row.Enemy, row.Value); row.Enemy.HpDisplay = row.Display; }
            }
            GD.Print("HEXTECH_NATIVE_SOUL_BIRTH_FROZEN_VERIFIED initial_hp_and_display_captured=true immutable_metadata=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeGoldFrozenProbe", out var goldFrozen) && goldFrozen.GetBoolean())
        {
            int savedGold = player.Gold;
            var collector = player.Relics.OfType<HextechRunes.CollectorRune>().FirstOrDefault();
            var field = HarmonyLib.AccessTools.Field(typeof(HextechRunes.CollectorRune), "_creditedExecutions");
            var credited = collector is null ? null : (HashSet<MegaCrit.Sts2.Core.Entities.Creatures.Creature>)field.GetValue(collector)!;
            var savedTargets = credited?.ToArray();
            try
            {
                player.Gold += 777;
                credited?.UnionWith(combat.Enemies);
                var replay = parent.Fork();
                PlaySequence(replay);
                if (Key(replay) != Key(child) || Stamp(replay) != Stamp(child)
                    || Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp)
                    throw new Exception("Native gold/credited-execution root aliased live or sibling state.");
            }
            finally
            {
                player.Gold = savedGold;
                if (credited is not null) { credited.Clear(); credited.UnionWith(savedTargets!); }
            }
            GD.Print("HEXTECH_NATIVE_GOLD_FROZEN_VERIFIED live_gold_and_credited_targets_ignored=true sibling_and_parent_stable=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeEnemyDrawFrozenProbe", out var enemyDrawFrozen) && enemyDrawFrozen.GetBoolean())
        {
            var modifier = combat.Modifiers.OfType<HextechRunes.HextechMayhemModifier>().Single();
            var tracking = modifier.CombatTracking;
            var values = new[] { tracking.NightstalkingPlayerCardsDrawnThisCombat,
                tracking.WarmogsSpiritPlayerCardsDrawnThisCombat, tracking.SwiftAndSafePlayerCardsDrawnThisCombat };
            var copies = values.Select(dictionary => dictionary.ToArray()).ToArray();
            int tier = modifier.SavedMonsterHexStrengthTierFloor;
            try
            {
                foreach (var dictionary in values) dictionary[player.NetId] = dictionary.GetValueOrDefault(player.NetId) + 100;
                modifier.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
                var replay = parent.Fork();
                PlaySequence(replay);
                if (Key(replay) != Key(child) || Stamp(replay) != Stamp(child)
                    || Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != key || Stamp(sibling) != stamp)
                    throw new Exception("Captured enemy draw callbacks read live counts/tier or shared a branch dictionary.");
            }
            finally
            {
                modifier.SavedMonsterHexStrengthTierFloor = tier;
                for (int index = 0; index < values.Length; index++)
                { values[index].Clear(); foreach (var item in copies[index]) values[index].Add(item.Key, item.Value); }
            }
            if (ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Enemy draw frozen-state probe failed to restore the live battle.");
            GD.Print("HEXTECH_NATIVE_ENEMY_DRAW_FROZEN_VERIFIED counts=true tier=true full_key=true continuation=true replay=true live_restored=true");
        }
        if (request.RootElement.TryGetProperty("hextechNativeHealthFrozenProbe", out var healthFrozen) && healthFrozen.GetBoolean())
        {
            var modifier = combat.Modifiers.OfType<HextechRunes.HextechMayhemModifier>().Single();
            var tracking = modifier.CombatTracking;
            var enemy = combat.Enemies.Single();
            int hp = enemy.CurrentHp, maxHp = enemy.MaxHp, tier = modifier.SavedMonsterHexStrengthTierFloor;
            var counts = tracking.MikaelsBlessingTriggers.ToArray();
            var sets = new[] { tracking.EscapePlanTriggered, tracking.RepulsorTriggered, tracking.DawnTriggered,
                tracking.FeelTheBurnTriggered, tracking.FeelTheBurnPending };
            var copies = sets.Select(set => set.ToArray()).ToArray();
            string? last = tracking.LastEnemyThresholdTriggerKey;
            var frozenChild = parent.Fork();
            try
            {
                enemy.SetMaxHpInternal(maxHp + 500); enemy.SetCurrentHpInternal(1);
                modifier.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
                tracking.MikaelsBlessingTriggers[enemy.CombatId!.Value] = 2;
                foreach (var set in sets) { set.Clear(); set.Add(enemy.CombatId.Value); }
                tracking.LastEnemyThresholdTriggerKey = "mutated live tracking after root capture";
                PlaySequence(frozenChild);
                if (Key(frozenChild) != Key(child) || Stamp(frozenChild) != Stamp(child))
                    throw new Exception("Native health callback read moving live HP/tier/once/pending/heal-limit data.");
            }
            finally
            {
                enemy.SetMaxHpInternal(maxHp); enemy.SetCurrentHpInternal(hp);
                modifier.SavedMonsterHexStrengthTierFloor = tier;
                tracking.MikaelsBlessingTriggers.Clear();
                foreach (var pair in counts) tracking.MikaelsBlessingTriggers.Add(pair.Key, pair.Value);
                for (int i = 0; i < sets.Length; i++) { sets[i].Clear(); sets[i].UnionWith(copies[i]); }
                tracking.LastEnemyThresholdTriggerKey = last;
            }
            if (Key(parent) != key || Stamp(parent) != stamp || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Native frozen-health probe changed parent/live state.");
            GD.Print("HEXTECH_NATIVE_HEALTH_FROZEN_VERIFIED hp=true max_hp=true tier=true once_sets=true pending=true heal_limit=true duplicate_key=true parent=true live=true");
        }
        // Repeat from the sibling: random target selection and all resulting
        // state must be deterministic without sharing the first child's RNG.
        PlaySequence(sibling);
        if (Key(sibling) != Key(child) || Stamp(sibling) != Stamp(child)
            || Key(parent) != key || Stamp(parent) != stamp || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Native token random replay was not reproducible from independent sibling branches.");
        GD.Print("HEXTECH_NATIVE_TOKEN_FORK_VERIFIED full_key=true continuation=true parent_live_unchanged=true sibling_rng_reproducible=true");
    }
}
