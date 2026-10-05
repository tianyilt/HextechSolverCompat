using CombatSolver;
using Godot;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeManualGates(UnattendedTestRunner.ScenarioContext scenario)
    {
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var simulator = root.ForkSimulator();
        using var request = Request();
        var expectedBlocked = request.RootElement.GetProperty("hextechNativeBlockedCards").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet();
        string live = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        string before = ContinuationStamp.CapturePredicted(scenario.Player, simulator, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        foreach (var card in scenario.Player.PlayerCombatState!.Hand.Cards)
        {
            bool actual = card.CanPlay(), predicted = simulator.CanPlay(simulator.State.FindCard(card)!);
            if (actual != predicted || actual == expectedBlocked.Contains(card.Id.Entry))
                throw new Exception($"Native manual gate differs: {card.Id.Entry}: actual={actual} predicted={predicted}.");
        }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != live
            || ContinuationStamp.CapturePredicted(scenario.Player, simulator, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText != before)
            throw new Exception("Manual cost gate changed observed state.");
        GD.Print("HEXTECH_NATIVE_MANUAL_GATES_VERIFIED native_can_play=true original_effective_cost=true x_cost=true state_unchanged=true");
    }
}
