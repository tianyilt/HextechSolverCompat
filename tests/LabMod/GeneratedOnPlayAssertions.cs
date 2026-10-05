using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyGeneratedOnPlay(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var steps = NativeSequence(request.RootElement, player);
        if (steps.Any(step => player.PlayerCombatState!.AllCards.Any(card => card.Id.Entry == step.CardId)))
            throw new Exception("Generated OnPlay regression must capture a root without any of the tested card types.");
        foreach (var card in new[] { typeof(CrashLanding), typeof(Scrawl), typeof(Dredge), typeof(Pillage) })
        {
            var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(card)!;
            var machine = AccessTools.Method(onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext");
            GD.Print($"HEXTECH_HAND_SIZE_COMPOSITION_DETAIL card={card.Name} patches={AdaptedCardOnPlayMirrors.DescribeActual(machine, Harmony.GetPatchInfo(machine), includeIndex: true)}");
        }
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        VerifyNativeHandSizeRoot(combat, player, root, simulator);
        string initialStamp = ContinuationStamp.CaptureLive(combat).StateText;
        string frozenPatchStamp = shadow.AdaptedOnPlay!.Stamp;
        VerifyHandSizeMachineComposition(combat, shadow);
        for (int index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var canonical = ModelDb.AllCards.Single(card => card.Id.Entry == step.CardId);
            var generated = shadow.CreateCard(canonical, player);
            var predicted = PredictedCard.FromGenerated(generated);
            if (!simulator.AddGeneratedCardToCombat(predicted, PileType.Hand, player).Success || simulator.HasPendingChoice)
                throw new Exception("Generated fixture could not add its predicted card.");
            if (index == 0 && ContinuationStamp.CaptureLive(combat).StateText != initialStamp)
                throw new Exception("Generated advice changed live combat before native generation.");
            var actual = combat.CreateCard(canonical, player);
            await CardPileCmd.AddGeneratedCardsToCombat([actual], PileType.Hand, player);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var target = step.Enemy ? combat.Enemies.First() : null;
            PlannedCardSelector? nativeSelector = null;
            if (step.ChoiceCardIds is { } requested)
            {
                // CardCmd moves the played card out of Hand before asking for
                // a selection. Inspect that count on a detached branch too.
                var choiceFork = simulator.Fork();
                int position = Array.IndexOf(simulator.State.GetPlayerCombatState(player).Hand.Cards.ToArray(), predicted);
                var choiceHand = choiceFork.State.GetPlayerCombatState(player).Hand;
                var choiceCard = choiceHand.Cards[position];
                choiceHand.Remove(choiceCard);
                var spec = CardChoiceSupport.GetSpec(choiceFork, choiceCard)
                    ?? throw new Exception("Generated selection requires a reviewed choice specification.");
                nativeSelector = new PlannedCardSelector(CardChoiceSupport.BuildRequestedChoice(spec, requested));
                nativeSelector.CaptureBefore(player);
            }
            UnattendedTestRunner.PlaySimulatedCard(simulator, shadow, predicted, target, combat.Enemies, step.ChoiceCardIds);
            if (simulator.HasPendingChoice) throw new Exception("Generated fixture requires a complete selection plan.");
            using (nativeSelector is null ? null : CardSelectCmd.PushSelector(nativeSelector, localOnly: true))
            {
                if (!actual.TryManualPlay(target)) throw new Exception($"Native generated {step.CardId} was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                nativeSelector?.ReconcileImplicitChoices(player);
                nativeSelector?.AssertConsumed();
            }
            foreach (var enemy in combat.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(simulator, shadow, player, enemy),
                    UnattendedTestRunner.CaptureActual(combat, player, enemy), "GeneratedOnPlay", $"GeneratedPlay:{step.CardId}");
            if (shadow.AdaptedOnPlay!.Stamp != frozenPatchStamp)
                throw new Exception("Generated card changed its frozen OnPlay composition.");
            if (request.RootElement.TryGetProperty("hextechNativeExpectedGeneratedHandCount", out var expectedHand)
                && player.PlayerCombatState!.Hand.Cards.Count != expectedHand.GetInt32())
                throw new Exception("Generated native draw did not reach the explicitly expected hand boundary.");
        }
        GD.Print($"HEXTECH_GENERATED_ONPLAY_VERIFIED absent_from_root=true generated_native=true full_snapshots=true frozen=true plays={steps.Length}");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }

    private static void VerifyHandSizeMachineComposition(MegaCrit.Sts2.Core.Combat.CombatState combat, SimulatedCombatState shadow)
    {
        string before = ContinuationStamp.CaptureLive(combat).StateText;
        string frozen = shadow.AdaptedOnPlay!.Stamp;
        var extra = AccessTools.Method(typeof(FixtureAssertions), nameof(UnreviewedCardPostfix));
        var harmony = new Harmony("HextechCompatLab.UnreviewedHandSizeMachine");
        foreach (var cardType in new[] { typeof(CrashLanding), typeof(Scrawl), typeof(Dredge), typeof(Pillage) })
        {
            var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(cardType)!;
            var machine = onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
            var target = AccessTools.Method(machine, "MoveNext");
            var patches = Harmony.GetPatchInfo(target)!;
            bool baseLib = AccessTools.TypeByName("BaseLib.Patches.Hooks.CardOnPlay_MaxHandSizePatch") is not null;
            if (patches.Transpilers.Count != (baseLib ? 2 : 1))
                throw new Exception("Native hand-size fixture did not exercise the expected framework composition.");
            GD.Print($"HEXTECH_HAND_SIZE_NATIVE_COMPOSITION card={cardType.Name} transpilers={patches.Transpilers.Count} owners={string.Join(',', patches.Transpilers.Select(p => p.owner))}");
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(extra));
                bool rejected = false;
                try { _ = ContinuationStamp.CaptureLive(combat); }
                catch (PredictionUnsupportedException) { rejected = true; }
                if (!rejected || shadow.AdaptedOnPlay!.Stamp != frozen)
                    throw new Exception("Hand-size contract accepted an additional unknown patch or changed frozen worker state.");
            }
            finally { harmony.Unpatch(target, extra); }
            if (ContinuationStamp.CaptureLive(combat).StateText != before)
                throw new Exception("Hand-size patch restoration changed combat or RNG.");
        }
        GD.Print("HEXTECH_HAND_SIZE_PATCH_VERIFIED reviewed_supported=true unknown_rejected=true frozen=true restored=true machines=4");
    }
}
