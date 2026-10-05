using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static bool _mysteryObserverInstalled;
    private static readonly List<string> OwnedMysterySelections = [];
    private static readonly List<string> NativeMysterySelections = [];
    private static void ObserveNativeMysteryTransform(IEnumerable<CardTransformation> transformations)
    {
        var bridge = AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge");
        bool owned = AccessTools.Field(bridge, "_simulator").GetValue(null) is not null;
        var selections = owned ? OwnedMysterySelections : NativeMysterySelections;
        using var request = Request();
        bool trace = request.RootElement.TryGetProperty("hextechNativeMysteryTrace", out var tracing) && tracing.GetBoolean();
        var simulator = AccessTools.Field(bridge, "_simulator").GetValue(null) as CombatPredictionSimulator;
        foreach (var transform in transformations)
        {
            var old = transform.Original; var replacement = transform.Replacement
                ?? throw new Exception("Mystery did not choose a fixed native replacement.");
            if (replacement.CurrentUpgradeLevel != Math.Min(old.CurrentUpgradeLevel, replacement.MaxUpgradeLevel))
                throw new Exception("Mystery did not preserve the chosen card's upgrade level.");
            selections.Add(HextechStableRandom.CardKey(old) + "->" + HextechStableRandom.CardKey(replacement));
            int priority = old.IsBasicStrikeOrDefend ? 2 : old.Rarity == CardRarity.Basic ? 1 : 0;
            selections.Add("priority:" + priority);
            if (trace)
            {
                var cards = owned ? simulator!.State.GetPlayerCombatState(old.Owner).DrawPile.Cards.Select(card => card.Preview).ToArray()
                    : old.Owner.PlayerCombatState!.DrawPile.Cards.ToArray();
                GD.Print($"HEXTECH_MYSTERY_TRACE owned={owned} selection={old.Id}->{replacement.Id} draw={string.Join(',', cards.Select(card => card.Id.Entry))}");
            }
            if (old.Pile?.Type == PileType.Exhaust)
                throw new Exception("Mystery transformed a card in Exhaust.");
        }
    }
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeMystery(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var actual = scenario.CombatState; var player = scenario.Player;
        var modifier = actual.Modifiers.OfType<HextechMayhemModifier>().Single();
        if (actual.RoundNumber != 1) throw new Exception("Mystery opening probe requires actual native round one.");
        // The real initial opening already ran before the fixture replaces the
        // piles. Re-arm only this test counter to exercise the reconstructed
        // representative piles through the exact original opening callback.
        modifier.CombatTracking.GlobalProcsThisCombat.Remove("enemy-mystery-opening-transform");
        if (!_mysteryObserverInstalled)
        {
            new Harmony("HextechCompatLab.MysteryTransformObserver").Patch(
                AccessTools.DeclaredMethod(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "TransformNativeMysteryCards"),
                prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveNativeMysteryTransform)));
            _mysteryObserverInstalled = true;
        }
        OwnedMysterySelections.Clear(); NativeMysterySelections.Clear();
        var root = CombatRootSnapshot.Capture(actual);
        var parent = root.ForkSimulator(); var sibling = parent.Fork(); var child = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(actual), BattleDamageTracker.Observe(actual),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), actual, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(actual).StateText;
        var initialKey = Key(parent);
        void Invoke(CombatPredictionSimulator sim)
        {
            var combat = (SimulatedCombatState)sim.State.CombatState;
            BeforeSideTurnStartMirrors.Invoke(combat.Modifiers.OfType<HextechMayhemModifier>().Single(),
                new BeforeSideTurnStartMirrorContext { Simulator = sim, Side = CombatSide.Player, Participants = [player.Creature] });
            if (sim.HasPendingChoice) throw new Exception("Mystery fixed opening unexpectedly opened a choice.");
        }
        Invoke(child);
        var firstSelection = OwnedMysterySelections.ToArray();
        if (Stamp(parent) != before || Stamp(sibling) != before || ContinuationStamp.CaptureLive(actual).StateText != live
            || Stamp(child) == before || Key(child).Equals(initialKey) || !Key(child.Fork()).Equals(Key(child)))
            throw new Exception("Mystery opening omitted fork/key/stamp or changed parent/sibling/live state.");
        OwnedMysterySelections.Clear(); Invoke(parent);
        if (!firstSelection.SequenceEqual(OwnedMysterySelections) || Stamp(parent) != Stamp(child))
            throw new Exception("Mystery sibling replay did not choose the same native cards/replacements.");
        await modifier.BeforeSideTurnStart(new BlockingPlayerChoiceContext(), CombatSide.Player, [player.Creature], actual);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (!OwnedMysterySelections.SequenceEqual(NativeMysterySelections))
            throw new Exception("Mystery original chosen-card order or stable replacements differed from prediction.");
        using var request = Request();
        int count = request.RootElement.GetProperty("hextechNativeMysteryExpectedCount").GetInt32();
        if (NativeMysterySelections.Count != count * 2)
            throw new Exception($"Mystery did not transform the requested tier/clamped card count: {NativeMysterySelections.Count / 2} != {count}.");
        void AssertFull(string phase)
        {
            if (request.RootElement.TryGetProperty("hextechNativeMysteryTrace", out var tracing) && tracing.GetBoolean())
                GD.Print($"HEXTECH_MYSTERY_TRACE phase={phase} owned_draw={string.Join(',', parent.State.GetPlayerCombatState(player).DrawPile.Cards.Select(card => card.Preview.Id.Entry))} native_draw={string.Join(',', player.PlayerCombatState!.DrawPile.Cards.Select(card => card.Id.Entry))}");
            foreach (var enemy in actual.Enemies)
                runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(parent,
                    (SimulatedCombatState)parent.State.CombatState, player, enemy),
                    UnattendedTestRunner.CaptureActual(actual, player, enemy), "NativeMystery", phase);
        }
        AssertFull("OriginalOpeningTransform");
        OwnedMysterySelections.Clear(); NativeMysterySelections.Clear();
        Invoke(parent);
        await modifier.BeforeSideTurnStart(new BlockingPlayerChoiceContext(), CombatSide.Player, [player.Creature], actual);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (OwnedMysterySelections.Count != 0 || NativeMysterySelections.Count != 0)
            throw new Exception("Mystery repeated its opening transform.");
        AssertFull("RepeatedOpeningNoNewTransform");
        var outcome = await VerifyNativeRounds(runner, scenario, 2);
        GD.Print($"HEXTECH_NATIVE_MYSTERY_VERIFIED native_count={count} original_selection_order=true stable_replacements=true upgrades=true exhaust_exempt=true once_per_combat=true frozen_pools=true fork_key_stamp=true native_rounds=2");
        return outcome;
    }
}
