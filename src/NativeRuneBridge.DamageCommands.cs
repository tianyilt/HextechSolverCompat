using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeDamageVar = AccessTools.Method(typeof(HextechGameApiCompat), "Damage",
        [typeof(PlayerChoiceContext), typeof(Creature), typeof(DamageVar), typeof(Creature), typeof(CardModel), typeof(CardPlay)]);

    private static void RegisterSearingAttack(Harmony harmony)
    {
        var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(SearingAttackCard))
            ?? throw new InvalidOperationException("SearingAttack OnPlay missing.");
        harmony.Patch(onPlay, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteSingleDamageVar)));
        RegisterNativeTokenCard<SearingAttackCard>(onPlay);
        RuneMirrors.RegisterNativeBase<SearingAttackRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteSingleDamageVar(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeDamageVar))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(DamageNativeVar));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"SearingAttack damage calls changed: {count}.");
    }

    private static Task<IEnumerable<DamageResult>> DamageNativeVar(PlayerChoiceContext context, Creature target,
        DamageVar damage, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (_simulator is null) return HextechGameApiCompat.Damage(context, target, damage, dealer, cardSource, cardPlay);
        var source = cardSource is null ? null : _simulator.State.FindCard(cardSource)
            ?? throw new PredictionUnsupportedException("Native damage card source is not captured.");
        var results = _simulator.Damage([target], damage.BaseValue, damage.Props, dealer, source, cardPlay);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<DamageResult>>(results);
    }
}
