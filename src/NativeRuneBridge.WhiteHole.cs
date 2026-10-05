using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeGainEnergy = AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy),
        [typeof(decimal), typeof(Player)]);

    private static void RegisterWhiteHole(Harmony harmony)
    {
        var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(WhiteHoleCard))
            ?? throw new InvalidOperationException("WhiteHole OnPlay missing.");
        harmony.Patch(onPlay, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteDraw)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WhiteHoleCard), "AfterCardDrawn"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteEnergyGain)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(WhiteHoleCard), "AfterCardDrawn"),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEnergyGain)));
        AfterCardDrawnMirrors.Registry.Register<WhiteHoleCard>((card, context) =>
        {
            if (!context.IntrinsicCardHandled && context.Card.References(card))
            {
                context.IntrinsicCardHandled = true;
                HandleWhiteHoleDraw(context);
            }
        });
        RegisterNativeTokenCard<WhiteHoleCard>(onPlay);
        RuneMirrors.RegisterNativeBase<WhiteHoleRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteEnergyGain(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeGainEnergy))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GainNativeEnergy));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"WhiteHole energy command changed: {count}.");
    }

    private static Task GainNativeEnergy(decimal amount, Player player)
    {
        if (_simulator is null) return PlayerCmd.GainEnergy(amount, player);
        if (amount != decimal.Truncate(amount) || amount < int.MinValue || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Native energy amount is not a reviewed integer.");
        _simulator.GainEnergy(player, (int)amount);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }

    internal static void HandleWhiteHoleDraw(AfterCardDrawnMirrorContext context)
    {
        if (context.Card.Preview is not WhiteHoleCard) return;
        var previous = _simulator;
        _simulator = context.Simulator;
        try
        {
            var card = (WhiteHoleCard)context.Card.MutablePreview;
            RequireCompleted(card.AfterCardDrawn(new ThrowingPlayerChoiceContext(), card, context.FromHandDraw), typeof(WhiteHoleCard));
        }
        finally { _simulator = previous; }
    }
}
