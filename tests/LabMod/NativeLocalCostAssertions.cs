using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using System.Text.Json;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void SetNativeLocalCosts(UnattendedTestRunner.ScenarioContext scenario, JsonElement items)
    {
        int count = 0;
        foreach (var item in items.EnumerateArray())
        {
            var card = scenario.Player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == item.GetProperty("cardId").GetString());
            int amount = item.GetProperty("amount").GetInt32();
            switch (item.GetProperty("mode").GetString())
            {
                case "set-turn": card.EnergyCost.SetThisTurn(amount, reduceOnly: false); break;
                case "set-until-played": card.EnergyCost.SetUntilPlayed(amount, reduceOnly: false); break;
                case "add-turn": card.EnergyCost.AddThisTurn(amount, reduceOnly: false); break;
                case "star-turn": card.SetStarCostThisTurn(amount); break;
                default: throw new Exception("Unknown native local cost mode.");
            }
            count++;
        }
        if (count == 0) throw new Exception("Native local cost fixture was empty.");
        GD.Print($"HEXTECH_NATIVE_LOCAL_COST_VERIFIED native_setters=true count={count}");
    }

    private static void VerifyNativeCardCosts(UnattendedTestRunner.ScenarioContext scenario)
    {
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var simulator = root.ForkSimulator();
        string live = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        string stamp = ContinuationStamp.CapturePredicted(scenario.Player, simulator, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        foreach (var card in scenario.Player.PlayerCombatState!.Hand.Cards)
        {
            int actual = card.EnergyCost.GetWithModifiers(CostModifiers.All);
            int predicted = simulator.State.FindCard(card)!.GetEnergyCostValueWithModifiers(simulator);
            if (actual != predicted) throw new Exception($"Native cost query {card.Id}: actual={actual}, predicted={predicted}.");
            int actualStars = card.GetStarCostWithModifiers();
            int predictedStars = simulator.State.FindCard(card)!.GetStarCostWithModifiers(simulator,
                simulator.State.GetPlayerCombatState(scenario.Player));
            // Native -1 means the card has no star cost, whereas the solver's
            // payment helper normalizes this absence to zero. Compare payment
            // amounts, preserving native X/current-resource behavior.
            if (Math.Max(0, actualStars) != predictedStars)
                throw new Exception($"Native star cost query {card.Id}: actual={actualStars}, predicted={predictedStars}.");
        }
        if (ContinuationStamp.CaptureLive(scenario.CombatState).StateText != live
            || ContinuationStamp.CapturePredicted(scenario.Player, simulator, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText != stamp)
            throw new Exception("Energy cost query mutated live or predicted state.");
        GD.Print("HEXTECH_NATIVE_COST_QUERY_VERIFIED all_hand=true native_costs=true negative_costs=true state_unchanged=true");
    }
}
