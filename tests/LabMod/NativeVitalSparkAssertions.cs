using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeVitalSpark(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var actual = scenario.CombatState; var player = scenario.Player;
        using var request = Request();
        int hexAmount = request.RootElement.GetProperty("hextechNativeVitalExpectedAmount").GetInt32();
        if ((player.Creature.GetPower<HextechVitalSparkPower>()?.Amount ?? 0) != hexAmount)
            throw new Exception("Original Arcane Punch opening did not apply the expected tier.");
        int combined = hexAmount + actual.Creatures.SelectMany(creature => creature.Powers)
            .OfType<VitalSparkPower>().Sum(power => power.Amount);
        var skills = player.PlayerCombatState!.AllCards.Where(card => card.Type == CardType.Skill).ToArray();
        if (skills.Length == 0 || skills.Any(card => combined == 0 ? card.Affliction is Tainted
            : card.Affliction is not Tainted { Amount: var amount } || amount != combined))
            throw new Exception("Native opening/fixture entry did not preserve the combined Tainted amount.");
        var root = CombatRootSnapshot.Capture(actual); var parent = root.ForkSimulator();
        var child = parent.Fork(); var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(actual).StateText;
        static void Generate(CombatPredictionSimulator simulator, MegaCrit.Sts2.Core.Entities.Players.Player owner)
        {
            var cards = new[] { PredictedCard.Create(ModelDb.Card<DefendSilent>(), owner),
                PredictedCard.Create(ModelDb.Card<StrikeSilent>(), owner) };
            var results = simulator.AddGeneratedCardsToCombat(cards, PileType.Draw, owner, CardPilePosition.Bottom);
            if (results.Any(result => !result.Success) || simulator.HasPendingChoice)
                throw new Exception("Native Vital Spark generated-card branch did not complete.");
        }
        Generate(child, player);
        if (Stamp(parent) != before || Stamp(sibling) != before || ContinuationStamp.CaptureLive(actual).StateText != live
            || Stamp(child) == before)
            throw new Exception("Vital Spark generated-card branch changed parent/sibling/live or omitted continuation state.");
        Generate(parent, player);
        CardModel[] generated = [actual.CreateCard<DefendSilent>(player), actual.CreateCard<StrikeSilent>(player)];
        await CardPileCmd.AddGeneratedCardsToCombat(generated, PileType.Draw, player, CardPilePosition.Bottom);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (combined > 0 && generated[0].Affliction is not Tainted { Amount: var generatedAmount }
            || combined > 0 && generated[0].Affliction!.Amount != combined
            || generated[1].Affliction is Tainted)
            throw new Exception("Original generated skill/attack Tainted rule did not occur.");
        foreach (var enemy in actual.Enemies)
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(parent,
                (SimulatedCombatState)parent.State.CombatState, player, enemy),
                UnattendedTestRunner.CaptureActual(actual, player, enemy), "NativeVitalSpark", "GeneratedSkillAndAttack");
        AssertNativeScalarModels(parent, actual);
        VerifyNativeTokenFork(scenario);
        var outcome = await VerifyNativeTokenActual(runner, scenario);
        GD.Print($"HEXTECH_NATIVE_VITAL_SPARK_VERIFIED tier_amount={hexAmount} combined_amount={combined} generated_skill=true attack_exempt=true native_opening=true fork_isolation=true full_snapshots=true");
        return outcome;
    }
}
