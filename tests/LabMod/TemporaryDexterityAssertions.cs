using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task VerifyTemporaryDexterityBoundary(UnattendedTestRunner.ScenarioContext scenario)
    {
        var live = scenario.CombatState;
        var player = scenario.Player;
        var enemy = live.Enemies.First();
        var wrapper = enemy.GetPower<HextechTemporaryDexterityLossPower>()
            ?? throw new Exception("Temporary stat boundary requires a real native enemy Dexterity-loss wrapper.");
        int amount = wrapper.Amount;
        int dexterity = enemy.GetPowerAmount<DexterityPower>();
        string liveStamp = ContinuationStamp.CaptureLive(live).StateText;
        // This real callback must be inert for a participant set excluding its
        // owner. It is safe to call without advancing or faking a combat phase.
        await wrapper.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Player, [player.Creature]);
        if (ContinuationStamp.CaptureLive(live).StateText != liveStamp)
            throw new Exception("Native enemy temporary Dexterity changed at the player participant boundary.");
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string frozen = Stamp(parent);
        var combat = (SimulatedCombatState)child.State.CombatState;
        if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(child, combat, [player.Creature]))
            throw new Exception("Temporary Dexterity participant probe unexpectedly suspended.");
        if (combat.GetAmount<HextechTemporaryDexterityLossPower>(enemy) != amount
            || combat.GetAmount<DexterityPower>(enemy) != dexterity)
            throw new Exception("Enemy temporary Dexterity expired at the simulated player boundary.");
        if (combat.EffectivePowers().OfType<TemporaryDexterityPower>().Any(power => power.Owner == player.Creature))
            throw new Exception("Player temporary Dexterity did not expire at its own participant boundary.");
        if (Stamp(parent) != frozen || Stamp(sibling) != frozen || ContinuationStamp.CaptureLive(live).StateText != liveStamp)
            throw new Exception("Temporary Dexterity boundary leaked into a parent, sibling or live combat.");
        GD.Print("HEXTECH_TEMPORARY_DEXTERITY_BOUNDARY_VERIFIED native_callback=true enemy_retained_at_player_end=true player_expired=true parent=true sibling=true live=true");
    }
}
