using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeDamageAmount = AccessTools.Method(typeof(HextechGameApiCompat), "Damage",
        [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay)]);

    private static void RegisterQuantumComputing(Harmony harmony)
    {
        var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(QuantumComputingCard))
            ?? throw new InvalidOperationException("QuantumComputing OnPlay missing.");
        var machine = onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("QuantumComputing async callback shape changed.");
        var moveNext = AccessTools.Method(machine, "MoveNext");
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteQuantumCommands)));
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteHeal)));
        RegisterNativeTokenCard<QuantumComputingCard>(onPlay);
        RuneMirrors.RegisterNativeBase<QuantumComputingRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteQuantumCommands(IEnumerable<CodeInstruction> instructions)
    {
        var nativeAnim = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim),
            [typeof(Creature), typeof(string), typeof(float)]);
        var nativePulse = AccessTools.Method(typeof(HextechCombatVfx), "QuantumPulse");
        var nativeWait = AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait),
            [typeof(float), typeof(float), typeof(bool), typeof(CancellationToken)]);
        int damage = 0, anim = 0, pulse = 0, wait = 0;
        foreach (var instruction in instructions)
        {
            string? replacement = null;
            if (instruction.Calls(NativeDamageAmount)) { damage++; replacement = nameof(DamageNativeAmount); }
            else if (instruction.Calls(nativeAnim)) { anim++; replacement = nameof(QuantumAnim); }
            else if (instruction.Calls(nativePulse)) { pulse++; replacement = nameof(QuantumPulse); }
            else if (instruction.Calls(nativeWait)) { wait++; replacement = nameof(QuantumWait); }
            if (replacement is not null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), replacement);
            }
            yield return instruction;
        }
        if (damage != 1 || anim != 1 || pulse != 1 || wait != 1)
            throw new InvalidOperationException($"QuantumComputing commands changed: damage={damage}, anim={anim}, pulse={pulse}, wait={wait}.");
    }

    private static Task<IEnumerable<DamageResult>> DamageNativeAmount(PlayerChoiceContext context, Creature target,
        decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (_simulator is null) return HextechGameApiCompat.Damage(context, target, amount, props, dealer, cardSource, cardPlay);
        var source = cardSource is null ? null : _simulator.State.FindCard(cardSource)
            ?? throw new PredictionUnsupportedException("Native damage card source is not captured.");
        var results = _simulator.Damage([target], amount, props, dealer, source, cardPlay);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<DamageResult>>(results);
    }
    // These exact reviewed presentation call sites retain their native behavior
    // during actual play. Simulation schedules no UI callbacks or timers.
    private static Task QuantumAnim(Creature owner, string animation, float duration)
        => _simulator is null ? CreatureCmd.TriggerAnim(owner, animation, duration) : Task.CompletedTask;
    private static Task QuantumWait(float low, float high, bool fastMode, CancellationToken cancellation)
        => _simulator is null ? Cmd.CustomScaledWait(low, high, fastMode, cancellation) : Task.CompletedTask;
    private static void QuantumPulse(Creature owner, IReadOnlyList<Creature> targets)
    {
        if (_simulator is null) HextechCombatVfx.QuantumPulse(owner, targets);
    }
}
