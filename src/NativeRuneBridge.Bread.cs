using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterBreadRunes()
    {
        RegisterFirstTypedReplay<BreadAndButterRune>();
        RegisterFirstTypedReplay<BreadAndCheeseRune>();
        RegisterFirstTypedReplay<BreadAndJamRune>();
        RegisterState<BreadSandwichRune>();
        RuneMirrors.RegisterNativeBase<BreadSandwichRune>();
        RegisterNativeReplayHooks<BreadSandwichRune>();
    }

    private static void RegisterFirstTypedReplay<T>() where T : FirstTypedCardReplayRuneBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>((relic, context) =>
            RequireCompleted(Invoke(relic, context.Simulator, model =>
                model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(T)));
        RegisterNativeReplayHooks<T>();
    }

    private static void RegisterNativeReplayHooks<T>() where T : HextechRelicBase
    {
        // The modify query is read-only with respect to consuming this turn's
        // proc. Consume only when the engine selects this modifier's callback,
        // retaining native order with Burst/Duplication/ThrowingAxe/etc.
        ModifyCardPlayCountMirrors.Registry.Register<T>((relic, context) =>
        {
            var previous = _nativeBeforePlayCard;
            _nativeBeforePlayCard = context.Card;
            try { return Invoke(relic, context.Simulator, model =>
                model.ModifyCardPlayCount(context.Card.MutablePreview, context.Target, context.PlayCount)); }
            finally { _nativeBeforePlayCard = previous; }
        });
        ModifyCardPlayCountMirrors.AfterRegistry.Register<T>((relic, context) =>
        {
            var previous = _nativeBeforePlayCard;
            _nativeBeforePlayCard = context.Card;
            try { RequireCompleted(Invoke(relic, context.Simulator, model =>
                model.AfterModifyingCardPlayCount(context.Card.MutablePreview)), typeof(T)); }
            finally { _nativeBeforePlayCard = previous; }
        });
    }
}
