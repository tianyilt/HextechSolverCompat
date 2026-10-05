using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyAutomationDraw(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, JsonElement request)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var native = player.Creature.GetPower<AutomationPower>() ?? throw new Exception("Automation probe has no native power.");
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        int Counter(CombatPredictionSimulator sim)
        {
            var power = ((SimulatedCombatState)sim.State.CombatState).EffectivePowers().OfType<AutomationPower>().Single();
            return sim.StateStore.Get(power, () => new AutomationPredictionState(power)).CardsLeft;
        }
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var key = Key(parent);
        int before = native.DisplayAmount;
        // Change just the hidden counter first: its influence must appear in
        // the complete search key and continuation, even without a drawn card.
        var probe = parent.Fork();
        var probePower = ((SimulatedCombatState)probe.State.CombatState).EffectivePowers().OfType<AutomationPower>().Single();
        probe.StateStore.Get(probePower, () => new AutomationPredictionState(probePower)).CardsLeft = before + 1;
        if (Key(probe) == key || Stamp(probe) == stamp)
            throw new Exception($"Automation counter is absent from state identity: key_changed={Key(probe) != key} continuation_changed={Stamp(probe) != stamp} counter={Counter(probe)}.");
        var amounts = request.GetProperty("amounts").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        foreach (int amount in amounts)
        {
            child.Draw(player, amount, fromHandDraw: false);
            if (Key(parent) != key || Key(sibling) != key || Stamp(parent) != stamp || Stamp(sibling) != stamp
                || Counter(parent) != before || Counter(sibling) != before || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("Automation draw leaked to parent, sibling or live combat.");
        }
        foreach (int amount in amounts) sibling.Draw(player, amount, fromHandDraw: false);
        if (Key(sibling) != Key(child) || Stamp(sibling) != Stamp(child))
            throw new Exception("Automation sibling replay is not deterministic.");
        foreach (int amount in amounts)
        {
            await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), amount, player, fromHandDraw: false);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child,
            (SimulatedCombatState)child.State.CombatState, player, enemy),
            UnattendedTestRunner.CaptureActual(combat, player, enemy), "HextechAutomation", "NativeDrawCommands");
        if (Counter(child) != native.DisplayAmount || native.DisplayAmount != request.GetProperty("expectedCounter").GetInt32())
            throw new Exception($"Automation counter differs: predicted={Counter(child)} native={native.DisplayAmount}.");
        GD.Print($"HEXTECH_AUTOMATION_DRAW_VERIFIED counter={native.DisplayAmount} full_key=true continuation=true parent=true sibling=true live=true full_native_snapshot=true rng=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
