using System.Reflection;
using System.Collections;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using NativeCounters = HextechRunes.HextechSelfUpgradeCardStore.Counters;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static bool GrowthCard(CardModel card) => card is Claw or Sow or Reap or IronWave;
    private static object GrowthState(CombatPredictionSimulator sim)
        => AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.NativeSelfUpgradeState", true)!
            .GetMethod("Require", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [sim])!;
    private static CardModel[] GrowthDeck(object state)
        => ((IEnumerable<CardModel>)state.GetType().GetProperty("DeckCards", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!).ToArray();
    private static System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, NativeCounters> GrowthTable(object state)
        => (System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, NativeCounters>)state.GetType()
            .GetField("Table", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
    private static string GrowthValues(CardModel card, NativeCounters? counters)
        => $"{card.Id.Entry}:{card.CurrentUpgradeLevel}:{counters?.Damage ?? 0}:{counters?.Block ?? 0}:"
            + string.Join(';', card.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value.BaseValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static void AssertNativeGrowthModels(CombatPredictionSimulator sim, MegaCrit.Sts2.Core.Combat.CombatState actual)
    {
        var state = GrowthState(sim);
        var predicted = GrowthDeck(state);
        var live = actual.Players.Single().Deck.Cards.Where(GrowthCard).ToArray();
        if (live.Length != predicted.Length) throw new Exception("Native permanent growth deck count differs.");
        for (int i = 0; i < live.Length; i++)
        {
            HextechSelfUpgradeCardStore.BonusByCard.TryGetValue(live[i], out var lc);
            GrowthTable(state).TryGetValue(predicted[i], out var pc);
            if (ReferenceEquals(live[i], predicted[i]) || ReferenceEquals(lc, pc)
                || GrowthValues(live[i], lc) != GrowthValues(predicted[i], pc))
                throw new Exception($"Native persistent growth differs at deck instance {i} ({live[i].Id.Entry}).");
        }
        GD.Print("HEXTECH_NATIVE_GROWTH_MODELS_VERIFIED persistent_variables=true instance_counters=true detached_deck=true native_commands=true");
    }
    private static void VerifyGrowthFork(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator(); var sibling = parent.Fork(); var child = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var key = Key(parent);
        var state = GrowthState(child); var ghost = GrowthDeck(state).First();
        var counters = GrowthTable(state).GetValue(ghost, _ => new());
        counters.Damage += 17;
        if (Key(child) == key || Stamp(child) == before) throw new Exception("Hidden permanent counters absent from key/stamp.");
        var fork = child.Fork(); var forkState = GrowthState(fork); var forkGhost = GrowthDeck(forkState).First();
        GrowthTable(forkState).TryGetValue(forkGhost, out var forkCounters);
        if (ReferenceEquals(forkGhost, ghost) || ReferenceEquals(forkCounters, counters)
            || Key(fork) != Key(child) || Stamp(fork) != Stamp(child))
            throw new Exception("Permanent growth ghost/counters failed deep fork.");
        forkCounters!.Damage++;
        if (Key(fork) == Key(child) || Stamp(fork) == Stamp(child)
            || Key(parent) != key || Key(sibling) != key || Stamp(parent) != before || Stamp(sibling) != before
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Permanent growth counter mutation leaked to parent/sibling/live state.");

        using var request = Request();
        var step = NativeSequence(request.RootElement, player).First();
        void PlayOne(CombatPredictionSimulator sim)
        {
            var card = sim.State.GetPlayerCombatState(player).Hand.Cards.First(card => card.Preview.Id.Entry == step.CardId);
            UnattendedTestRunner.PlaySimulatedCard(sim, (SimulatedCombatState)sim.State.CombatState,
                card, step.Enemy ? combat.Enemies.Single() : null, combat.Enemies, null);
        }
        var baseline = parent.Fork(); PlayOne(baseline);
        var frozen = parent.Fork();
        var deckCard = player.Deck.Cards.First(GrowthCard);
        HextechSelfUpgradeCardStore.BonusByCard.TryGetValue(deckCard, out var liveCounters);
        int oldDamage = liveCounters!.Damage;
        decimal oldBase = deckCard.DynamicVars.Damage.BaseValue;
        try
        {
            liveCounters.Damage += 100; deckCard.DynamicVars.Damage.BaseValue += 100;
            PlayOne(frozen);
            if (Key(frozen) != Key(baseline) || Stamp(frozen) != Stamp(baseline)
                || Key(parent) != key || Stamp(parent) != before)
                throw new Exception("Native permanent growth read changed live deck values/counters after capture.");
        }
        finally { liveCounters.Damage = oldDamage; deckCard.DynamicVars.Damage.BaseValue = oldBase; }
        if (ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Native growth frozen-input probe failed to restore live state.");
        GD.Print("HEXTECH_NATIVE_GROWTH_FORK_VERIFIED hidden_counter_key=true hidden_counter_stamp=true ghost_deep_fork=true counter_deep_fork=true parent=true sibling=true live=true");
    }
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeGrowth(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement setup)
    {
        var combat = scenario.CombatState; var player = scenario.Player; var enemy = combat.Enemies.Single();
        var originals = player.PlayerCombatState!.AllCards.Where(GrowthCard).ToArray();
        for (int i = 0; i < originals.Length; i++)
        {
            var card = originals[i];
            if (card.DeckVersion is { } deck && card.IsUpgraded && !deck.IsUpgraded)
                CardCmd.Upgrade(deck, MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle.None);
            HextechSelfUpgradeCardStore.AddDamageOnPlay(card, 4 + i);
            if (card is IronWave) HextechSelfUpgradeCardStore.AddBlockOnPlay(card, 2 + i);
        }
        if (setup.TryGetProperty("sharedDeckCopy", out var duplicate) && duplicate.GetBoolean())
        {
            var source = originals.First();
            var copy = combat.CloneCard(source); copy.DeckVersion = source.DeckVersion;
            await CardPileCmd.Add(copy, PileType.Hand);
        }
        VerifyGrowthFork(scenario);
        if (setup.TryGetProperty("dampen", out var dampenFlag) && dampenFlag.GetBoolean())
        {
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            shadow.ApplyDampen(simulator, player.Creature, enemy);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            var dampen = await PowerCmd.Apply<DampenPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null)
                ?? throw new Exception("Native Dampen application failed.");
            dampen.AddCaster(enemy);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "NativeGrowth", "DampenKeepsCounters");
            AssertNativeGrowthModels(simulator, combat);
            var fork = simulator.Fork();
            shadow.RemoveDampenCaster(enemy);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            await PowerCmd.Remove(dampen);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "NativeGrowth", "DampenRestoreUpgrade");
            AssertNativeGrowthModels(simulator, combat);
            if (fork.State.GetPlayerCombatState(player).AllCards.Where(card => GrowthCard(card.Preview)).Any(card => card.Preview.IsUpgraded))
                throw new Exception("Dampen upgrade restoration leaked into sibling.");
            GD.Print("HEXTECH_NATIVE_GROWTH_DAMPEN_VERIFIED native_power=true downgrade_keeps_growth=true restore_upgrade=true sibling=true full_snapshots=true");
        }
        VerifyNativeTokenFork(scenario);
        return await VerifyNativeTokenActual(runner, scenario);
    }
}
