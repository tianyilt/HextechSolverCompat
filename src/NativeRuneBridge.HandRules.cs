using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static CombatPredictionState? _nativeKeywordState;

    private static void RegisterNativeHandRules(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DuffsVintageRune), "BeforeTurnEnd"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        RegisterState<DuffsVintageRune>(); RegisterNativeEndTurn<DuffsVintageRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DuffsVintageRune), "ShouldFlush"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SmokestackUpgradeRune), "AfterCardDrawn"),
            new NativeCallSite(typeof(Creature).GetMethods().Single(method => method.Name == nameof(Creature.GetPower)
                && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(SmokestackPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(SmokestackPower)), 1),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount)), nameof(NativeSmokestackAmount)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature)]), nameof(NativeInfernoDamage)));
        RegisterState<SmokestackUpgradeRune>(); RuneMirrors.RegisterNativeBase<SmokestackUpgradeRune>();
        AfterCardDrawnMirrors.Registry.Register<SmokestackUpgradeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardDrawn(new ThrowingPlayerChoiceContext(),
                context.Card.MutablePreview, context.FromHandDraw)), typeof(SmokestackUpgradeRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BlueCandleMedkitRune), "CanAffect"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBlueCandlePile)));
        RegisterState<BlueCandleMedkitRune>(); RuneMirrors.RegisterNativeBase<BlueCandleMedkitRune>();
        RegisterNativeQueryCallbacks<BlueCandleMedkitRune>(NativeQueries.LateEnergy | NativeQueries.Stars);
        RegisterNativeResultLocation<BlueCandleMedkitRune>();
        foreach (var name in new[] { "TryModifyEnergyCostInCombatLate", "TryModifyStarCost",
            "ModifyCardPlayResultPileTypeAndPositionCompat", "TryModifyKeywordsInCombat", "AllowsPlaying" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BlueCandleMedkitRune), name));
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), nameof(EnterNativeKeywordQuery));
        var finalizer = AccessTools.Method(typeof(NativeRuneBridge), nameof(LeaveNativeKeywordQuery));
        foreach (var name in new[] { "GetKeywords", "HasKeyword" })
        {
            var target = AccessTools.DeclaredMethod(typeof(CombatPredictedCardExtensions), name);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            NativeCallbackContracts.AddScope(target, prefix, finalizer);
        }
    }

    private static int NativeSmokestackAmount(PowerModel power)
        => _simulator is null ? power.Amount
            : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<SmokestackPower>(power.Owner);

    private static void EnterNativeKeywordQuery(CombatPredictionState state, out CombatPredictionState? __state)
    { __state = _nativeKeywordState; _nativeKeywordState = state; }
    private static void LeaveNativeKeywordQuery(CombatPredictionState? __state) => _nativeKeywordState = __state;

    private static CardPile? NativeBlueCandlePile(CardModel card)
    {
        if (_simulator is not null) return NativeBranchCardPile(card);
        if (_nativeKeywordState is not { } state) return card.Pile;
        foreach (var pile in state.GetPlayerCombatState(card.Owner).AllPiles)
            if (pile.Cards.Any(predicted => predicted.References(card)))
                return card.Owner.PlayerCombatState!.AllPiles.Single(native => native.Type == pile.Type);
        return null;
    }
}
