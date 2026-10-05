using CombatSolver;
using CombatSolver.Engine.Common;
using Godot;
using HarmonyLib;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeEventContract(UnattendedTestRunner.ScenarioContext scenario)
    {
        var live = scenario.CombatState;
        string stamp = ContinuationStamp.CaptureLive(live).StateText;
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        var frozen = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp;
        var patch = new Harmony("HextechCompatLab.UnreviewedNativeEvent");
        var target = AccessTools.DeclaredMethod(typeof(RenewalRune), "AfterCardDiscarded");
        try
        {
            patch.Patch(target, postfix: new HarmonyMethod(typeof(FixtureAssertions), nameof(UnreviewedExpiryPostfix)));
            if (ContinuationStamp.CaptureLive(live).StateText == stamp
                || ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!.Stamp != frozen
                || ((SimulatedCombatState)sibling.State.CombatState).AdaptedOnPlay!.Stamp != frozen)
                throw new Exception("Native event patch changed frozen composition or did not invalidate a live route.");
            try { _ = CombatRootSnapshot.Capture(live); throw new Exception("Unknown native event patch was accepted."); }
            catch (PredictionUnsupportedException error) when (error.Message.Contains("Unreviewed native callback composition")) { }
        }
        finally { patch.Unpatch(target, HarmonyPatchType.Postfix, patch.Id); }
        if (ContinuationStamp.CaptureLive(live).StateText != stamp) throw new Exception("Native event contract probe did not restore composition.");
        GD.Print("HEXTECH_NATIVE_EVENT_CONTRACT_VERIFIED unknown_rejected=true frozen_parent=true frozen_sibling=true live_stamp_restored=true");
    }
}
