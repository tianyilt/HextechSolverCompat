using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeLoseBlock = AccessTools.Method(typeof(HextechRuneApiCompat), "LoseBlock",
        [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(Creature)]);

    private static void RegisterBlockLossRunes(Harmony harmony)
    {
        NativeCallbackContracts.Add(NativeLoseBlock);
        var method = AccessTools.Method(typeof(GiantSerpentsFangRune), nameof(GiantSerpentsFangRune.AfterDamageGiven));
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("GiantSerpentsFang callback shape changed.");
        harmony.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteBlockLoss)));
        RegisterNativeDamageHook<GiantSerpentsFangRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteBlockLoss(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeLoseBlock))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(LoseNativeBlock));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"GiantSerpentsFang block commands changed: {count}.");
    }

    private static Task LoseNativeBlock(PlayerChoiceContext context, Creature target, decimal amount, Creature? remover)
    {
        if (_simulator is null) return CreatureCmd.LoseBlock(context, target, amount, remover);
        if (amount != decimal.Truncate(amount))
            throw new PredictionUnsupportedException("Native block-loss bridge only accepts audited integral amounts.");
        var state = _simulator.State.GetCreature(target);
        if (_simulator.IsOverOrEnding || state.IsDead || amount <= 0m) return Task.CompletedTask;
        int before = state.Block;
        state.DamageBlock(amount, default(ValueProp));
        if (before > 0 && state.Block <= 0)
            HookMirrors.AfterBlockBroken(_simulator, target, remover);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }
}
