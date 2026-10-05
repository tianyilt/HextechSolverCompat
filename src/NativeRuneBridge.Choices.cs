using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    // Only a simulator-created pending choice may suspend a reviewed native
    // callback. Its original async stack cannot be serialized; replay the whole
    // enclosing action from the parent with the selected choice plan instead.
    private sealed class NativeCallbackChoicePause(CombatPredictionSimulator simulator) : Exception
    {
        internal CombatPredictionSimulator Simulator { get; } = simulator;
    }

    private static void PauseNativeChoice(CombatPredictionSimulator simulator)
    {
        if (!simulator.HasPendingChoice) return;
        simulator.RejectExecutionContinuation();
        throw new NativeCallbackChoicePause(simulator);
    }

    private static void AcceptNativeChoicePause(NativeCallbackChoicePause pause)
    {
        if (!pause.Simulator.HasPendingChoice)
            throw new PredictionUnsupportedException("Native callback lost its pending execution choice.");
        pause.Simulator.RejectExecutionContinuation();
    }
}
