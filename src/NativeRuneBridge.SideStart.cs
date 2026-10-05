using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record SideStartScope(CombatPredictionSimulator Simulator, CombatSide Side, IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> Participants);
    [ThreadStatic] private static SideStartScope? _sideStartScope;
    private static readonly Dictionary<Type, Action<HextechRelicBase, CombatPredictionSimulator, CombatSide>> NativeSideStart = [];

    private static void RegisterSideStartBlockFamily(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.TriggerRelicsAfterSideTurnStart)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(EnterSideStart)),
            finalizer: new HarmonyMethod(typeof(NativeRuneBridge), nameof(LeaveSideStart)));
        harmony.Patch(AccessTools.Method(typeof(RelicPredictionStateSupport), nameof(RelicPredictionStateSupport.ResetAfterSideTurnStart)),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(DispatchNativeSideStart)));
        RegisterSideStartBlock<MiserableFateRune>(harmony, ownBeforeEnd: true);
        RegisterSideStartBlock<MirageRune>(harmony);
        RegisterSideStartTemporaryLoss<DecayRune>(harmony);
    }


    private static void RegisterSideStartBlock<T>(Harmony harmony, bool ownBeforeEnd = false) where T : HextechRelicBase
    {
        RegisterState<T>();
        if (ownBeforeEnd)
            RuneMirrors.RegisterNativeBase<T>(beforeEnd: (rune, context) =>
                RequireCompleted(Invoke(rune, context.Simulator, model => model.BeforeTurnEnd(
                    new ThrowingPlayerChoiceContext(), context.Side)), typeof(T)));
        else RuneMirrors.RegisterNativeBase<T>();
        var callback = AccessTools.DeclaredMethod(typeof(T), "AfterSideTurnStart");
        harmony.Patch(callback, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteSideStartBlock)));
        var query = typeof(T).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Single(method => method.Name.StartsWith("<AfterSideTurnStart>b__", StringComparison.Ordinal));
        harmony.Patch(query, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerAmount)));
        RegisterNativeSideStartCallback<T>();
    }

    private static void RegisterSideStartTemporaryLoss<T>(Harmony harmony) where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        PatchPowerCallback<T>(harmony, "AfterSideTurnStart", 2, 1);
        var callback = AccessTools.DeclaredMethod(typeof(T), "AfterSideTurnStart");
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        harmony.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerAmount)));
        RegisterNativeSideStartCallback<T>();
    }

    private static void RegisterNativeSideStartCallback<T>() where T : HextechRelicBase
        => NativeSideStart.Add(typeof(T), (rune, simulator, side) =>
            RequireCompleted(Invoke((T)rune, simulator, model => model.AfterSideTurnStart(side, simulator.State.CombatState)), typeof(T)));

    private static IEnumerable<CodeInstruction> RewriteSideStartBlock(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeDecimalBlock))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GainNativeBlock));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed side-start block command changed: {count} call sites.");
    }

    private static void EnterSideStart(CombatPredictionSimulator simulator, CombatSide side, IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> participants, out SideStartScope? __state)
    {
        __state = _sideStartScope;
        _sideStartScope = new(simulator, side, participants);
    }
    private static void LeaveSideStart(SideStartScope? __state) => _sideStartScope = __state;
    private static void DispatchNativeSideStart(CombatPredictionSimulator simulator, RelicModel relic)
    {
        // Run at the existing ordered per-relic dispatch position, after its
        // reset and before the next relic. A method-wide postfix would reverse
        // ordering against vanilla reactions and block modifiers.
        if (_sideStartScope is { } scope && ReferenceEquals(scope.Simulator, simulator)
            && relic is HextechRelicBase rune && NativeSideStart.TryGetValue(rune.GetType(), out var callback))
            callback(rune, simulator, scope.Side);
        if (_sideStartScope is { } auxiliaryScope && ReferenceEquals(auxiliaryScope.Simulator, simulator)
            && AuxiliarySideStart.TryGetValue(relic.GetType(), out var auxiliary))
            auxiliary(relic, simulator, auxiliaryScope.Side, auxiliaryScope.Participants);
    }
}
