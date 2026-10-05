using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeAuxiliary(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState; var player = scenario.Player;
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeLegacyPlatingProbe", out var legacySetup) && legacySetup.GetBoolean())
        {
            // The legacy save model is registered but deliberately absent from
            // ModelDb.AllRelics (current pools); resolve its actual saved ID.
            var forge = (SilverPlatingForge)ModelDb.GetById<RelicModel>(ModelDb.GetId(typeof(SilverPlatingForge))).ToMutable();
            forge.SavedStackCount = 2;
            await MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain(forge, player);
        }
        if (request.RootElement.TryGetProperty("hextechNativeAuxiliaryEntry", out var entry) && entry.GetBoolean())
        {
            foreach (var relic in player.Relics.Where(r => r is HextechForgeBase or OrobasPlusRelicBase))
                await relic.BeforeCombatStart();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (request.RootElement.TryGetProperty("hextechNativeLegacyPlatingProbe", out var legacy) && legacy.GetBoolean())
        {
            var forge = player.Relics.OfType<SilverPlatingForge>().Single();
            if (forge.StackCount != 2 || player.Creature.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.PlatingPower>() != 8)
                throw new Exception("Original old-save SilverPlatingForge did not apply its two stacks of four Plating.");
            GD.Print("HEXTECH_NATIVE_LEGACY_PLATING_VERIFIED original_entry=true stacks=2 plating=8");
        }
        if (request.RootElement.TryGetProperty("hextechNativeAuxiliarySideStart", out var side) && side.GetBoolean())
        {
            var root = CombatRootSnapshot.Capture(combat);
            var simulator = root.ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            var parent = simulator.Fork(); var sibling = simulator.Fork();
            string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player,
                sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
            string parentBefore = Stamp(parent), siblingBefore = Stamp(sibling);
            if (!shadow.TriggerRelicsAfterSideTurnStart(simulator, CombatSide.Player, [player.Creature])
                || simulator.HasPendingChoice || ContinuationStamp.CaptureLive(combat).StateText != liveBefore
                || Stamp(parent) != parentBefore || Stamp(sibling) != siblingBefore)
                throw new Exception("Auxiliary opening changed live/parent/sibling state or did not complete.");
            // Execute the same native per-relic stage, in its real listener order.
            foreach (var relic in player.Relics)
                await relic.AfterSideTurnStart(CombatSide.Player, [player.Creature], combat);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            foreach (var enemy in combat.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechAuxiliary", "OriginalOpening");
            AssertNativeScalarModels(simulator, combat);
        }
        VerifyNativeTokenFork(scenario);
        var result = await VerifyNativeTokenActual(runner, scenario);
        GD.Print("HEXTECH_NATIVE_AUXILIARY_VERIFIED original_callbacks=true stack_counts=true full_snapshots=true fork_key_stamp_rng=true opening_and_future_turns=true");
        return result;
    }
}
