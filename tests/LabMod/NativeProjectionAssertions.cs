using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeProjectionFreeze(UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        using var request = Request();
        var steps = NativeSequence(request.RootElement, player);
        void Apply(CombatPredictionSimulator sim)
        {
            var shadow = (SimulatedCombatState)sim.State.CombatState;
            foreach (var step in steps)
                UnattendedTestRunner.PlaySimulatedCard(sim, shadow,
                    sim.State.GetPlayerCombatState(player).Hand.Cards.First(card => card.Preview.Id.Entry == step.CardId),
                    step.Enemy ? enemy : null, [enemy], null);
        }
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var baseline = parent.Fork();
        Apply(baseline);
        var child = parent.Fork();
        var stacks = player.Relics.OfType<GoldenSpatulaRune>().Single();
        int saved = stacks.SavedStacks, hp = enemy.CurrentHp, maxHp = enemy.MaxHp;
        var deck = (List<CardModel>)AccessTools.Field(typeof(CardPile), "_cards").GetValue(player.Deck)!;
        var originalDeck = deck.ToArray();
        try
        {
            stacks.SavedStacks += 100;
            deck.Add(player.PlayerCombatState!.Hand.Cards.First());
            enemy.SetMaxHpInternal(maxHp + 800);
            enemy.SetCurrentHpInternal(1);
            Apply(child);
        }
        finally
        {
            stacks.SavedStacks = saved;
            deck.Clear(); deck.AddRange(originalDeck);
            enemy.SetMaxHpInternal(maxHp); enemy.SetCurrentHpInternal(hp);
        }
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(baseline,
            (SimulatedCombatState)baseline.State.CombatState, player, enemy),
            UnattendedTestRunner.CaptureSimulated(child, (SimulatedCombatState)child.State.CombatState, player, enemy),
            "NativeProjection", "FrozenLiveInputs");
        if (Stamp(child) != Stamp(baseline) || Stamp(parent) != before || Stamp(sibling) != before
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Native coefficients read live deck/stacks/HP or leaked a branch change.");
        GD.Print("HEXTECH_NATIVE_PROJECTION_FREEZE_VERIFIED deck_count=true stacks=true enemy_max_hp=true parent=true sibling=true live=true full_snapshots=true");
    }
}
