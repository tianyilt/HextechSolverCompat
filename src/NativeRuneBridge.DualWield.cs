using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterDualWield()
    {
        RegisterState<DualWieldRune>();
        RuneMirrors.RegisterNativeBase<DualWieldRune>();
        RegisterNativeReplayHooks<DualWieldRune>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<DualWieldRune>((relic, context) =>
            Invoke(relic, context.Simulator, model => model.ModifyDamageMultiplicativeCompat(
                context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview)));
    }
}
