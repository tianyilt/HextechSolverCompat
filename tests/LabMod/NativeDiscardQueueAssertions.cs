using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static readonly FieldInfo NativePendingDiscards = typeof(SellOffRune).GetField("_pendingDiscardedCards", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object NativeDiscardState(CombatPredictionSimulator sim, SellOffRune relic)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.NativeRuneState", true)!;
        return typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type).Invoke(null, [sim, relic])!;
    }
    private static SellOffRune NativeDiscardModel(object state)
        => (SellOffRune)state.GetType().GetField("Model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private static void VerifyNativeDiscardQueueFork(UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var sibling = parent.Fork();
        var child = parent.Fork();
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(player, sim,
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        object State(CombatPredictionSimulator sim) => NativeDiscardState(sim,
            ((SimulatedCombatState)sim.State.CombatState).RelicsOf(player).OfType<SellOffRune>().Single());
        var key = Key(parent);
        string before = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var state = State(child);
        var queue = (Queue<CardModel>)NativePendingDiscards.GetValue(NativeDiscardModel(state))!;
        var cards = child.State.GetPlayerCombatState(player).Hand.Cards.Take(2).ToArray();
        if (cards.Length != 2) throw new Exception("Discard queue boundary requires two distinct cards.");
        queue.Enqueue(cards[0].MutablePreview);
        queue.Enqueue(cards[1].MutablePreview);
        state.GetType().GetMethod("CaptureQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [child]);
        if (Key(child) == key || Stamp(child) == before) throw new Exception("Pending discard references were absent from key/stamp.");
        var fork = child.Fork();
        if (Key(fork) != Key(child) || Stamp(fork) != Stamp(child)) throw new Exception("Pending discard fork changed its state.");
        var forkQueue = (Queue<CardModel>)NativePendingDiscards.GetValue(NativeDiscardModel(State(fork)))!;
        var childQueue = (Queue<CardModel>)NativePendingDiscards.GetValue(NativeDiscardModel(state))!;
        if (ReferenceEquals(childQueue, forkQueue)) throw new Exception("Discard queue was shallow cloned.");
        var branchCards = fork.State.GetPlayerCombatState(player).Hand.Cards.Take(2).ToArray();
        for (int index = 0; index < 2; index++)
            if (!ReferenceEquals(forkQueue.ElementAt(index), branchCards[index].MutablePreview)
                || ReferenceEquals(forkQueue.ElementAt(index), childQueue.ElementAt(index)))
                throw new Exception("Pending discard reference did not remap to its own branch card.");
        forkQueue.Dequeue();
        var forkState = State(fork);
        forkState.GetType().GetMethod("CaptureQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(forkState, [fork]);
        if (Key(fork) == Key(child) || Stamp(fork) == Stamp(child) || childQueue.Count != 2
            || Key(parent) != key || Key(sibling) != key || Stamp(parent) != before || Stamp(sibling) != before
            || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Discard queue mutation leaked or did not change key/stamp.");
        GD.Print("HEXTECH_NATIVE_DISCARD_QUEUE_VERIFIED ordered_refs=true deep_fork=true branch_remap=true key=true stamp=true parent=true sibling=true live=true");
    }

    private static void AssertNativeDiscardModels(CombatPredictionSimulator sim, CombatState actual)
    {
        var shadow = (SimulatedCombatState)sim.State.CombatState;
        foreach (var player in actual.Players)
        {
            var native = player.Relics.OfType<SellOffRune>().Single();
            var predicted = NativeDiscardModel(NativeDiscardState(sim, shadow.RelicsOf(player).OfType<SellOffRune>().Single()));
            for (Type? type = typeof(SellOffRune); type is not null && type.Assembly == typeof(ModEntry).Assembly; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object? left = field.GetValue(native), right = field.GetValue(predicted);
                    bool same = field == NativePendingDiscards
                        ? ((Queue<CardModel>)left!).Count == 0 && ((Queue<CardModel>)right!).Count == 0
                        : typeof(ICombatState).IsAssignableFrom(field.FieldType) ? (left is null) == (right is null) : Equals(left, right);
                    if (!same) throw new Exception($"Native SellOff hidden field differs: {field.Name}.");
                }
        }
        GD.Print("HEXTECH_NATIVE_DISCARD_COUNTERS_VERIFIED native_proc_ordinal=true reentrancy_guard=false queue_drained=true");
    }
}
