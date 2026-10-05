using CombatSolver;
using Godot;
using HextechRunes;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeSoulProjection(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var cloud = player.Creature.GetPower<HextechCloudDragonSoulPower>()
            ?? throw new Exception("Soul projection probe requires the native acquired Cloud power.");
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string live = ContinuationStamp.CaptureLive(combat).StateText;
        // Include the native starting relic and every other hand-draw hook.
        // Silent's RingOfTheSnake contributes on this first-turn root.
        int expected = Math.Max(0, (int)MegaCrit.Sts2.Core.Hooks.Hook.ModifyHandDraw(combat, player, 5, out _));
        if (PersistentPowerSupport.GetModifiedHandDraw((SimulatedCombatState)parent.State.CombatState, player, 5) != expected)
            throw new Exception("Soul projection disagrees with the native baseline before live mutation.");
        int hp = player.Creature.CurrentHp, amount = cloud._amount;
        try
        {
            player.Creature.SetCurrentHpInternal(0);
            cloud._amount = amount + 50;
            foreach (var sim in new[] { parent, sibling })
            {
                int actual = PersistentPowerSupport.GetModifiedHandDraw((SimulatedCombatState)sim.State.CombatState, player, 5);
                if (actual != expected)
                    throw new Exception($"Cloud soul projection read live state: nativeBaseline={expected} capturedBranch={actual}.");
            }
        }
        finally { player.Creature.SetCurrentHpInternal(hp); cloud._amount = amount; }
        if (ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Soul projection probe did not restore live state.");
        GD.Print("HEXTECH_NATIVE_SOUL_PROJECTION_VERIFIED frozen_hp=true frozen_power_amount=true independent_siblings=true live_restored=true");
    }
}
