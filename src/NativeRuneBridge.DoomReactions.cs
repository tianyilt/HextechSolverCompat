using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterDoomReactionFamily(Harmony harmony)
    {
        var taunt = AccessTools.DeclaredMethod(typeof(TauntRune), nameof(TauntRune.AfterPowerAmountChanged));
        harmony.Patch(AccessTools.Method(taunt.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteDraw)));
        var pact = AccessTools.DeclaredMethod(typeof(OminousPactRune), "HandleDoomApplied");
        harmony.Patch(AccessTools.Method(pact.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNativeSummon)));
        RegisterState<TauntRune>();
        RegisterState<OminousPactRune>();
        RuneMirrors.RegisterNativeBase<TauntRune>();
        RuneMirrors.RegisterNativeBase<OminousPactRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteNativeSummon(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var native = AccessTools.Method(typeof(OstyCmd), nameof(OstyCmd.Summon),
            [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]);
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(SummonNativeForIgnoredResult));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed native summon callback changed: {count}.");
    }

    private static Task<SummonResult> SummonNativeForIgnoredResult(PlayerChoiceContext context, Player player,
        decimal amount, AbstractModel source)
    {
        if (_simulator is null) return OstyCmd.Summon(context, player, amount, source);
        if (amount != decimal.Truncate(amount) || amount <= 0 || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Native Doom summon requires a positive integral amount.");
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        combat.SummonOsty(_simulator, player, (int)amount);
        PauseNativeChoice(_simulator);
        // Only the reviewed callback discarding this result is redirected here.
        // The live source is retained by the native fallback; audited BoneGuard
        // and Plaster simulation listeners inspect player/amount only.
        return Task.FromResult(new SummonResult(combat.GetOsty(player)
            ?? throw new PredictionUnsupportedException("Native summon did not create a branch Osty."), amount));
    }
}
