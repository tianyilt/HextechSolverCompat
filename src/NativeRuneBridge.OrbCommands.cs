using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeOrbChannel = AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Channel),
        [typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player)]);

    private static void RegisterOrbTurnRunes(Harmony harmony)
    {
        RegisterOrbTurnRune<EmergenceRune>(harmony);
        RegisterOrbTurnRune<HappyAccidentRune>(harmony);
        RegisterNativeOrbPassiveFamily(harmony);
    }

    private static void RegisterOrbTurnRune<T>(Harmony harmony) where T : HextechRelicBase
    {
        var callback = AccessTools.Method(typeof(T), nameof(HextechRelicBase.AfterPlayerTurnStart));
        var stateMachine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeof(T).Name} async callback shape changed.");
        harmony.Patch(AccessTools.Method(stateMachine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteOrbChannel)));
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterPlayerTurnStartMirrors.Register<T>((relic, context) => RequireCompleted(
            Invoke(relic, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
    }

    private static IEnumerable<CodeInstruction> RewriteOrbChannel(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeOrbChannel))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ChannelOrb));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Orb command changed: expected 1, found {count}.");
    }

    private static Task ChannelOrb(PlayerChoiceContext context, OrbModel orb, Player player)
    {
        if (_simulator is null) return OrbCmd.Channel(context, orb, player);
        bool completed = _simulator.OrbChannel(player, orb);
        PauseNativeChoice(_simulator);
        if (!completed) throw new PredictionUnsupportedException("Native orb callback could not complete in the current branch.");
        return Task.CompletedTask;
    }
}
