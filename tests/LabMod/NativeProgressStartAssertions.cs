using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task VerifyNativeProgressStart(UnattendedTestRunner runner,
        UnattendedTestRunner.ScenarioContext scenario, CombatPredictionSimulator simulator, CombatRootSnapshot root, JsonElement settings)
    {
        var combat = scenario.CombatState; var player = scenario.Player;
        string phase = settings.GetProperty("phase").GetString()!;
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var parent = simulator.Fork(); var sibling = simulator.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string parentStamp = Stamp(parent), siblingStamp = Stamp(sibling);
        for (int repeat = 0; repeat < settings.GetProperty("repeat").GetInt32(); repeat++)
        {
            string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
            switch (phase)
            {
                case "player-start": HookMirrors.AfterPlayerTurnStart(simulator, player, new TurnStartChoiceCursor([])); break;
                case "side-start": shadow.TriggerRelicsAfterSideTurnStart(simulator, CombatSide.Player, [player.Creature]); break;
                case "before-draw": shadow.PrepareRelicsBeforeHandDraw(simulator, player, new TurnStartChoiceCursor([])); break;
                default: throw new Exception("Unknown reviewed progress-start fixture phase.");
            }
            if (simulator.HasPendingChoice || ContinuationStamp.CaptureLive(combat).StateText != liveBefore
                || Stamp(parent) != parentStamp || Stamp(sibling) != siblingStamp)
                throw new Exception("Progress-start branch changed its live, parent or sibling state, or suspended without a choice.");
            foreach (var rune in player.Relics.OfType<HextechRelicBase>())
            {
                if (phase == "player-start" && rune is MakeItMineRune)
                    await rune.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), player);
                if (phase == "side-start" && rune is TranscendentEvilRune)
                    await rune.AfterSideTurnStart(CombatSide.Player, combat);
                if (phase == "before-draw" && rune is SubroutineUpgradeRune or SendThemInRune)
                    await rune.BeforeHandDraw(player, new ThrowingPlayerChoiceContext(), combat);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            foreach (var enemy in combat.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechProgressStart", phase + ":" + repeat);
            AssertNativeScalarModels(simulator, combat);
        }
        GD.Print($"HEXTECH_NATIVE_PROGRESS_START_VERIFIED phase={phase} native_callbacks=true repeated_start=true branch_live_parent_sibling_unchanged=true");
    }
}
