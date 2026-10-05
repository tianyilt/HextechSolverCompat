using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    // Exercise the original fallback hook after a direct amount change. This
    // deliberately bypasses the received query, as other native hooks may do.
    private static async Task VerifyNativeAttributeChange(UnattendedTestRunner.ScenarioContext scenario,
        UnattendedTestRunner runner)
    {
        var live = scenario.CombatState;
        var player = scenario.Player;
        var source = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "DEFEND_IRONCLAD");
        if (player.Creature.GetPowerAmount<StrengthPower>() != 0 || player.Creature.GetPowerAmount<DexterityPower>() != 2)
            throw new Exception("Attribute fallback requires the completed native Strength-to-Dexterity conversion.");
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string before = Stamp(parent), nativeBefore = ContinuationStamp.CaptureLive(live).StateText;
        void Change(CombatPredictionSimulator sim)
        {
            var shadow = (SimulatedCombatState)sim.State.CombatState;
            var card = sim.State.FindCard(source) ?? throw new Exception("Attribute source is absent from the branch.");
            shadow.SetAmount<StrengthPower>(player.Creature, 3);
            var power = shadow.GetPower<StrengthPower>(player.Creature)!;
            shadow.BeginCardPowerApplication(card.MutablePreview);
            try { shadow.RecordPowerAmountChange(power, 3, player.Creature); }
            finally { shadow.CompleteCardPowerApplication(card.MutablePreview); }
            var bridge = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "HextechSolverCompat")
                .GetType("HextechSolverCompat.PowerSourceBridge", throwOnError: true)!;
            var captured = (PredictedCard?[])bridge.GetMethod("CapturePendingCards", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [shadow])!;
            if (captured.Length != 1 || !ReferenceEquals(captured[0], card))
                throw new Exception("Power-change metadata did not retain the exact branch source card.");
            PowerLifecycleSupport.ResolvePowerAmountChanges(sim, shadow);
            if (sim.HasPendingChoice) throw new Exception("Attribute fallback unexpectedly suspended.");
        }
        Change(child);
        Change(sibling);
        if (Stamp(child) == before || Stamp(child) != Stamp(sibling) || Stamp(parent) != before
            || ContinuationStamp.CaptureLive(live).StateText != nativeBefore)
            throw new Exception("Attribute fallback leaked across branches, lost its continuation, or shared mutable state.");
        var nativePower = ModelDb.Power<StrengthPower>().ToMutable();
        nativePower.Applier = player.Creature;
        nativePower.ApplyInternal(player.Creature, 3, silent: true);
        await Hook.AfterPowerAmountChanged(live, new BlockingPlayerChoiceContext(), nativePower, 3, player.Creature, source);
        foreach (var enemy in live.Enemies)
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, (SimulatedCombatState)child.State.CombatState, player, enemy),
                UnattendedTestRunner.CaptureActual(live, player, enemy), "HextechNativeAttributeChange", "OriginalFallbackWithCardSource");
        if (player.Creature.GetPowerAmount<DexterityPower>() != 5)
            throw new Exception("Original attribute fallback did not convert the direct three-point change.");
        GD.Print("HEXTECH_NATIVE_ATTRIBUTE_CHANGE_VERIFIED original_callback=true exact_card_source=true independent_branches=true snapshots=true");
    }
}
