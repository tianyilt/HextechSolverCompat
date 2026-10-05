using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNeutralNegative(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var target = player.Creature;
        int artifact = target.GetPowerAmount<ArtifactPower>();
        await HextechPowerCmdCompat.Apply<HextechTemporarySlowPower>(target, -6, target, null, true);
        if (artifact <= 0 || target.GetPowerAmount<ArtifactPower>() != artifact
            || target.GetPowerAmount<HextechTemporarySlowPower>() != -6
            || target.GetPowerAmount<HextechPlayerSlowPower>() != -6)
            throw new Exception("Native negative neutral setup consumed Artifact or did not grant both powers.");
        var livePower = target.GetPower<HextechTemporarySlowPower>()!;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        StateFingerprint Key(CombatPredictionSimulator sim)
        {
            StateFingerprintBuilder writer = new();
            ((SimulatedCombatState)sim.State.CombatState).AppendFingerprint(ref writer, sim);
            return writer.Finish();
        }
        string stamp = Stamp(parent), liveStamp = ContinuationStamp.CaptureLive(combat).StateText;
        var key = Key(parent);
        foreach (var field in typeof(HextechTemporarySlowPower).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var child = parent.Fork();
            var power = ((SimulatedCombatState)child.State.CombatState).GetMutablePower<HextechTemporarySlowPower>(target)!;
            object saved = field.GetValue(livePower)!;
            object changed = field.FieldType == typeof(bool) ? !(bool)saved : (int)saved + 1;
            field.SetValue(power, changed);
            if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Stamp(parent) != stamp
                || Key(sibling) != key || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(combat).StateText != liveStamp)
                throw new Exception($"Neutral hidden power state leaked or omitted {field.Name}.");
            try
            {
                field.SetValue(livePower, changed);
                if (Key(parent) != key || Stamp(parent) != stamp || ContinuationStamp.CaptureLive(combat).StateText == liveStamp)
                    throw new Exception($"Neutral root state was not frozen: {field.Name}.");
            }
            finally { field.SetValue(livePower, saved); }
        }
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        GD.Print("HEXTECH_NEUTRAL_NEGATIVE_VERIFIED native_negative=true artifact_retained=true hidden_key=true hidden_continuation=true detached=true native_expiry=true");
        return outcome;
    }
}
