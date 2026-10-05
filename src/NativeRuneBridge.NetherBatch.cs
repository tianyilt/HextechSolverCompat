using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeNetherBatch(Harmony harmony)
    {
        // Preserve the native distinct snapshot, ordering and finally guard.
        // A nested unresolved choice rejects opaque async continuation through
        // the shared relay. The solver then replays this whole action with the
        // selected plan; it never resumes an incomplete CLR callback stack.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NetherSoulRune), "AfterSideTurnEndLate"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat), 3),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding), 2),
            Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeBranchPileCards)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Models.CardModel), "Pile"), nameof(NativeBranchCardPile)),
            Site(AccessTools.DeclaredMethod(typeof(HextechAutoPlayHelper), "AutoPlayOrMoveToResultPile"), nameof(AutoPlayNativeCard)));
        foreach (string name in new[] { "SnapshotEtherealCards", "IsPlayableEtherealCard" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(NetherSoulRune), name));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechRuneTargeting), "FirstHittableEnemy"));
        RegisterState<NetherSoulRune>(); RuneMirrors.RegisterNativeBase<NetherSoulRune>();
        AfterSideTurnEndLateMirrors.Register<NetherSoulRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, owned => owned.AfterSideTurnEndLate(
                new ThrowingPlayerChoiceContext(), context.Side, context.Participants)), typeof(NetherSoulRune)));
    }
}
