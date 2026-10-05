using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static CardPlay? _nativeResourcePlay;

    private static void RegisterFinalForm(Harmony harmony)
    {
        // This audited callback asks the global native play-cost stack for the
        // effective paid X/non-X value. Supply this branch's immutable CardPlay
        // resources while executing it; never consult the live stack.
        harmony.Patch(AccessTools.Method(typeof(HextechCombatHooks), nameof(HextechCombatHooks.GetEnergyCostForCurrentCardPlay)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(CurrentNativePlayCost)));
        PatchPowerCallback<FinalFormRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed), 2, 1);
        var callback = AccessTools.Method(typeof(FinalFormRune), nameof(FinalFormRune.AfterCardPlayed));
        var moveNext = AccessTools.Method(callback.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext");
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteDraw)));
        RegisterState<FinalFormRune>();
        RuneMirrors.RegisterNativeBase<FinalFormRune>((relic, context) =>
            RequireCompleted(Invoke(relic, context.Simulator, model =>
                model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(FinalFormRune)));
        AfterCardPlayedMirrors.Registry.Register<FinalFormRune>((relic, context) =>
        {
            var previous = _nativeResourcePlay;
            _nativeResourcePlay = context.CardPlay;
            try
            {
                RequireCompleted(Invoke(relic, context.Simulator, model =>
                    model.AfterCardPlayed(new ThrowingPlayerChoiceContext(), context.CardPlay)), typeof(FinalFormRune));
            }
            finally { _nativeResourcePlay = previous; }
        });
    }

    private static bool CurrentNativePlayCost(CardModel card, ref decimal __result)
    {
        if (_simulator is null) return true;
        if (_nativeResourcePlay is not null && ReferenceEquals(_nativeResourcePlay.Card, card))
            __result = _nativeResourcePlay.Resources.EnergyValue;
        else if (FindNativePlayCost(card) is { } cost) __result = cost;
        else if (_nativeBeforePlayCard is { } before && before.References(card))
            __result = before.GetEnergyCostWithModifiers(_simulator, _simulator.State.GetPlayerCombatState(card.Owner));
        else throw new PredictionUnsupportedException("Native effective play cost requires matching captured branch resources or a scoped pre-play query.");
        return false;
    }
}
