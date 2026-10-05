using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterOpeningFormCards()
    {
        RegisterOpeningForm<DemonFormUpgradeRune>();
        RegisterOpeningForm<EchoFormUpgradeRune>();
        RegisterOpeningForm<VoidFormUpgradeRune>();
        RegisterOpeningForm<SerpentFormUpgradeRune>();
        RegisterOpeningForm<ReaperFormUpgradeRune>();
    }

    private static void RegisterOpeningForm<T>() where T : HextechRelicBase
    {
        OpeningFormBoundary.Register<T>();
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterPlayerTurnStartMirrors.RegisterLate<T>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStartLate(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
    }
}
