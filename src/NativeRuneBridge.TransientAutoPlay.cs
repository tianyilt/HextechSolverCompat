using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    // This membership exists only during an awaited native callback. A pause
    // replays the enclosing action from its root; it never forks this stack.
    [ThreadStatic] private static HashSet<CardModel>? _nativeTransientCards;

    private static void RegisterNativeTransientAutoPlay(Harmony harmony)
    {
        var transient = AccessTools.Method(typeof(HextechAutoPlayHelper), "AutoPlayTransientCardAndCleanup");
        NativeCallbackContracts.Add(transient);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AutoPatrolRune), "AfterPlayerTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsOstyAlive)), nameof(NativeBranchOstyAlive)),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Osty)), nameof(NativeBranchOsty)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), nameof(NativeBranchProgress)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)), nameof(NativeBranchEnding)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(AddNativeRawCard)),
            Site(transient, nameof(AutoPlayNativeTransientCard)));
        RegisterState<AutoPatrolRune>();
        RuneMirrors.RegisterNativeBase<AutoPatrolRune>();
        AfterPlayerTurnStartMirrors.Register<AutoPatrolRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(AutoPatrolRune)));
    }

    private static Creature? NativeBranchOsty(Player player)
        => _simulator is { } sim ? sim.State.GetOsty(player) : player.Osty;
    private static bool NativeBranchOstyAlive(Player player)
        => _simulator is { } sim ? sim.State.GetOsty(player) is { } osty && sim.State.GetCreature(osty).IsAlive : player.IsOstyAlive;

    private static Task AutoPlayNativeTransientCard(PlayerChoiceContext context, CardModel card, Creature? target,
        AutoPlayType type, bool skipXCapture, bool skipCardPileVisuals)
    {
        if (_simulator is null)
            return HextechAutoPlayHelper.AutoPlayTransientCardAndCleanup(context, card, target, type, skipXCapture, skipCardPileVisuals);
        var sim = _simulator;
        var cards = _nativeTransientCards ??= [];
        cards.Add(card);
        try { return AutoPlayNativeCard(context, card, target, type, skipXCapture, skipCardPileVisuals); }
        finally
        {
            cards.Remove(card);
            // Native finally runs after the awaited play/selection completes.
            // Pending choices expose that earlier boundary and use full replay.
            if (!sim.HasPendingChoice && sim.State.FindCard(card) is { } predicted
                && predicted.GetPile(sim.State)?.Type == PileType.Hand)
                sim.RemoveFromCombat(predicted);
        }
    }
}
