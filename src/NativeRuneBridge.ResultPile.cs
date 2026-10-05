using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterResultPileRunes()
    {
        RegisterResultPileRune<BloodlettingUpgradeRune>();
        RegisterResultPileRune<DualcastUpgradeRune>();
        RegisterResultPileRune<DirgeUpgradeRune>();
        RegisterResultPileRune<ForgottenSoulRune>();
    }

    private static void RegisterResultPileRune<T>() where T : HextechRelicBase
    {
        // These four exact callbacks only change the destination and a boolean
        // used by the selected modifier's after callback. Keep that boolean in
        // detached branch state even when another listener overrides the result.
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>(afterResult: (relic, context) =>
            RequireCompleted(Invoke(relic, context.Simulator, model =>
                model.AfterModifyingCardPlayResultPileOrPositionCompat(context.Card.MutablePreview,
                    context.Location.pileType, context.Location.position)), typeof(T)));
        RegisterNativeResultLocation<T>();
    }

    private static void RegisterNativeResultLocation<T>() where T : HextechRelicBase
    {
        ModifyCardPlayResultLocationMirrors.Registry.Register<T>((relic, context) =>
        {
            var location = context.Location;
            var result = Invoke(relic, context.Simulator, model =>
                model.ModifyCardPlayResultPileTypeAndPositionCompat(context.Card.MutablePreview,
                    context.IsAutoPlay, context.Resources, location.pileType, location.position));
            location.pileType = result.Item1;
            location.position = result.Item2;
            return location;
        });
    }
}
