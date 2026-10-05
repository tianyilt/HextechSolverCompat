using CombatSolver;
using CombatSolver.Engine.Common;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void UnreviewedExpiryPostfix() { }

    private static void VerifyPowerExpiryContract(UnattendedTestRunner.ScenarioContext scenario)
    {
        var live = scenario.CombatState;
        string stamp = ContinuationStamp.CaptureLive(live).StateText;
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var frozen = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp;
        var target = AccessTools.DeclaredMethod(typeof(RagePower), "AfterSideTurnEnd");
        var harmony = new Harmony("HextechCompatLab.UnreviewedExpiry");
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(UnreviewedExpiryPostfix)));
            if (ContinuationStamp.CaptureLive(live).StateText == stamp
                || ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp != frozen
                || ((SimulatedCombatState)child.State.CombatState).AdaptedOnPlay!.Stamp != frozen)
                throw new Exception("Unknown power expiry patch did not invalidate live routes or changed a frozen root.");
            try
            {
                _ = CombatRootSnapshot.Capture(live);
                throw new Exception("Unknown power expiry patch was accepted by root capture.");
            }
            catch (PredictionUnsupportedException error) when (error.Message.Contains("Unreviewed power-expiry")) { }
        }
        finally { harmony.Unpatch(target, HarmonyPatchType.Postfix, harmony.Id); }
        if (ContinuationStamp.CaptureLive(live).StateText != stamp)
            throw new Exception("Power expiry probe failed to restore the native patch composition.");
        GD.Print("HEXTECH_POWER_EXPIRY_CONTRACT_VERIFIED unknown_rejected=true frozen_parent=true frozen_child=true live_stamp_restored=true");
    }
}
