using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeDrawSustainFamily(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HastyScribbleRune), "AfterPlayerTurnStartLate"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(NativeDraw, nameof(Draw)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HastyScribbleRune), "CalculateCardsToDraw"));
        RegisterState<HastyScribbleRune>();
        RuneMirrors.RegisterNativeBase<HastyScribbleRune>();
        AfterPlayerTurnStartMirrors.RegisterLate<HastyScribbleRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStartLate(new ThrowingPlayerChoiceContext(), context.Player)), typeof(HastyScribbleRune)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(LifeFlowRune), "AfterCardExhausted"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeEnemyMaxHp)),
            Site(NativeHeal, nameof(HealNative)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(TurnScopedRelicBase), "BeforeSideTurnStart"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(LifeFlowRune), "ResetTurnScopedState"));
        RegisterState<LifeFlowRune>();
        RuneMirrors.RegisterNativeBase<LifeFlowRune>(beforeTurn: (rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(LifeFlowRune)));
        AfterCardExhaustedMirrors.Registry.Register<LifeFlowRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardExhausted(new ThrowingPlayerChoiceContext(),
                context.Card.MutablePreview, context.CausedByEthereal)), typeof(LifeFlowRune)));
    }
}
