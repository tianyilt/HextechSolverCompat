using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyNativeColorDiscovery(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        using var request = Request();
        int optionIndex = request.RootElement.GetProperty("hextechNativeColorOption").GetInt32();
        bool victory = request.RootElement.TryGetProperty("hextechNativeColorVictory", out var flag) && flag.GetBoolean();
        var combat = scenario.CombatState;
        var player = scenario.Player;
        if (player.Relics.OfType<ColorDiscoveryRune>().Any()) throw new Exception("ColorDiscovery must be obtained after the fixture is playable.");
        await RelicCmd.Obtain(ModelDb.AllRelics.Single(relic => relic.Id.Entry == "COLOR_DISCOVERY_RUNE").ToMutable(), player);
        var rune = player.Relics.OfType<ColorDiscoveryRune>().Single();
        await rune.BeforeCombatStart();
        if (rune.SavedOfferedThisCombat || !rune.SavedPendingRewardCardId.Equals(ModelId.none))
            throw new Exception("Native ColorDiscovery did not reset its opening state.");
        var oldCards = player.PlayerCombatState!.AllCards.ToArray();
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var parentKey = Key(parent);
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        void Isolation()
        {
            if (Key(parent) != parentKey || Key(sibling) != parentKey || Stamp(parent) != before || Stamp(sibling) != before || ContinuationStamp.CaptureLive(combat).StateText != live)
                throw new Exception("ColorDiscovery selection mutated a parent, sibling or live battle.");
        }
        var partial = parent.Fork();
        var partialCombat = (SimulatedCombatState)partial.State.CombatState;
        var emptyCursor = partialCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try { partialCombat.PrepareBeforeHandDraw(partial, player, emptyCursor); }
        finally { partialCombat.EndActionChoices(); }
        if (!partial.HasPendingChoice || partial.TakeExecutionContinuation() is not null)
            throw new Exception("ColorDiscovery did not suspend as a replayable generated-card choice.");
        var pending = partialCombat.PendingTurnStartChoice!;
        var spec = TurnStartChoiceSupport.BuildPendingSpec(partial, partialCombat, player);
        if (spec.Options.Count != 3 || optionIndex < 0 || optionIndex >= spec.Options.Count)
            throw new Exception("ColorDiscovery did not generate three stable original options.");
        var selectedId = spec.Options[optionIndex].Preview.CanonicalId();
        var choice = CardChoiceSupport.BuildRequestedChoice(spec, [selectedId.Entry]) with
        { SourceId = pending.SourceId, ContextId = pending.ContextId, Timing = pending.Timing };
        Isolation();
        var child = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var cursor = shadow.BeginActionChoices([choice]);
        try
        {
            if (shadow.PrepareBeforeHandDraw(child, player, cursor) || child.HasPendingChoice)
                throw new Exception("ColorDiscovery explicit opening choice did not finish.");
        }
        finally { shadow.EndActionChoices(); }
        Isolation();
        var fork = child.Fork();
        if (Key(fork) != Key(child) || Key(child) == parentKey || Stamp(fork) != Stamp(child) || Stamp(child) == before)
            throw new Exception("ColorDiscovery reward state was missing or changed across a fork.");
        var selector = new PlannedCardSelector([choice]);
        using (CardSelectCmd.PushSelector(selector, localOnly: true))
        {
            await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
            selector.ReconcileImplicitChoices(player);
            selector.AssertConsumed();
        }
        foreach (var enemy in combat.Enemies)
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, enemy),
                UnattendedTestRunner.CaptureActual(combat, player, enemy), "ColorDiscovery", "NativeOpeningChoice");
        AssertNativeScalarModels(child, combat);
        var generated = player.PlayerCombatState.AllCards.Except(oldCards).ToArray();
        if (generated.Length != 1 || !generated[0].CanonicalId().Equals(selectedId)
            || generated[0].EnergyCost.GetWithModifiers(CostModifiers.All) != 0
            || !rune.SavedOfferedThisCombat || !rune.SavedPendingRewardCardId.Equals(selectedId))
            throw new Exception("ColorDiscovery native free hand card or pending reward identity differs.");
        var repeatedCursor = shadow.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            if (shadow.PrepareBeforeHandDraw(child, player, repeatedCursor) || child.HasPendingChoice)
                throw new Exception("ColorDiscovery reoffered its selection in the same combat.");
        }
        finally { shadow.EndActionChoices(); }
        await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
        if (player.PlayerCombatState.AllCards.Except(oldCards).Count() != 1)
            throw new Exception("ColorDiscovery generated another card on repeated opening.");
        AssertNativeScalarModels(child, combat);
        if (victory)
        {
            var room = player.RunState.CurrentRoom as CombatRoom ?? throw new Exception("ColorDiscovery victory requires the real combat room.");
            var finisher = player.PlayerCombatState.Hand.Cards.First(card => card.Id.Entry == "STRIKE_NECROBINDER");
            if (!finisher.TryManualPlay(combat.Enemies.First())) throw new Exception("ColorDiscovery finishing Strike was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            for (int i = 0; i < 180 && CombatManager.Instance.IsInProgress; i++) await runner.NextFrameAsync();
            if (CombatManager.Instance.IsInProgress || !rune.SavedPendingRewardCardId.Equals(ModelId.none))
                throw new Exception("ColorDiscovery native victory did not consume the pending reward ID.");
            var rewards = (Dictionary<Player, List<Reward>>)AccessTools.Field(typeof(CombatRoom), "_extraRewards").GetValue(room)!;
            var reward = rewards[player].OfType<ColorDiscoveryCardReward>().Single();
            if (!reward.Cards.Single().CanonicalId().Equals(selectedId) || ReferenceEquals(reward.Cards.Single(), generated[0]))
                throw new Exception("ColorDiscovery victory reward is not a fresh copy of the selected card.");
            int count = rewards[player].Count;
            await rune.AfterCombatVictory(room);
            if (rewards[player].Count != count)
                throw new Exception("ColorDiscovery duplicated its consumed victory reward.");
        }
        else for (int i = 0; i < 2; i++) await runner.AssertReportRoundAsync(combat, player);
        GD.Print($"HEXTECH_NATIVE_COLOR_DISCOVERY_VERIFIED option={optionIndex} selected={selectedId} options=3 free=true pending_reward=true explicit_choice=true replay=true fork=true native_snapshots=true no_repeat=true native_victory={victory}");
        return new(victory, player.PlayerCombatState.TurnNumber, true, false, false, false);
    }
}

