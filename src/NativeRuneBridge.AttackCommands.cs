using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeAttackExecute = AccessTools.Method(typeof(AttackCommand), nameof(AttackCommand.Execute), [typeof(PlayerChoiceContext)]);
    private static readonly MethodInfo NativeDecimalBlock = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
        [typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardPlay), typeof(bool)]);

    private static void RegisterBodySlamCommands(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(BodySlamUpgradeRune), "PlayUpgraded");
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("BodySlam upgrade callback shape changed.");
        harmony.Patch(AccessTools.Method(stateMachine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteBodySlamCommands)));
        RegisterState<BodySlamUpgradeRune>();
        RuneMirrors.RegisterNativeBase<BodySlamUpgradeRune>();
    }

    internal static void PlayBodySlam(BodySlamUpgradeRune rune, CardOnPlayMirrorContext context)
        => RequireCompleted(Invoke(rune, context.Simulator, model => model.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (BodySlam)context.Card.MutablePreview, context.CardPlay)), typeof(BodySlamUpgradeRune));

    private static IEnumerable<CodeInstruction> RewriteBodySlamCommands(IEnumerable<CodeInstruction> instructions)
    {
        int attacks = 0, blocks = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeAttackExecute))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ExecuteNativeAttack));
                attacks++;
            }
            else if (instruction.Calls(NativeDecimalBlock))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GainNativeBlock));
                blocks++;
            }
            yield return instruction;
        }
        if (attacks != 1 || blocks != 1)
            throw new InvalidOperationException($"BodySlam commands changed: attacks={attacks}, blocks={blocks}.");
    }

    private static Task<AttackCommand> ExecuteNativeAttack(AttackCommand command, PlayerChoiceContext context)
    {
        if (_simulator is null) return command.Execute(context);
        _simulator.ExecuteAttack(command);
        PauseNativeChoice(_simulator);
        return Task.FromResult(command);
    }

    private static IEnumerable<CodeInstruction> RewriteNativeAttack(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeAttackExecute))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ExecuteNativeAttack));
                count++;
            }
            yield return instruction;
        }
        if (count != 1)
            throw new InvalidOperationException($"Reviewed native attack command changed: {count}.");
    }

    private static Task<decimal> GainNativeBlock(Creature creature, decimal amount, ValueProp props, CardPlay? cardPlay, bool fast)
    {
        if (_simulator is null) return CreatureCmd.GainBlock(creature, amount, props, cardPlay, fast);
        var source = cardPlay is null ? null : _simulator.State.FindCard(cardPlay.Card);
        decimal result = _simulator.GainBlock(creature, amount, props, source, cardPlay);
        PauseNativeChoice(_simulator);
        return Task.FromResult(result);
    }

    private static Task<decimal> GainNativeVarBlock(Creature creature,
        MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar block, CardPlay? play, bool fast)
        => _simulator is null ? CreatureCmd.GainBlock(creature, block, play, fast)
            : GainNativeBlock(creature, block.BaseValue, block.Props, play, fast);
}
