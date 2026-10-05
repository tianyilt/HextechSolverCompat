using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeBuffReactions(Harmony harmony)
    {
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 5 && method.GetParameters()[0].ParameterType == typeof(Creature));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FuriousGlareRune), "AfterPowerAmountChanged"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            new NativeCallSite(apply.MakeGenericMethod(typeof(StrengthPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(typeof(StrengthPower)), 1));
        RegisterState<FuriousGlareRune>();
        RuneMirrors.RegisterNativeBase<FuriousGlareRune>();

        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(GrowingStrongerRune), "AfterPowerAmountChanged"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GrowingStrongerRune), "PickCardToMakeFree"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GrowingStrongerRune), "PickCardToMakeFreeFromCandidates"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)));
        var pickerPredicate = AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechFreeCardPicker), "<>c"), "<Pick>b__4_0");
        PatchEventCallback(harmony, pickerPredicate,
            Site(AccessTools.Method(typeof(CardModel), nameof(CardModel.CostsEnergyOrStars)), nameof(NativeCardCostsEnergyOrStars)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(HextechFreeCardPicker))) NativeCallbackContracts.Add(method);
        RegisterStableState<GrowingStrongerRune>();
        RuneMirrors.RegisterNativeBase<GrowingStrongerRune>();
    }

    private static bool NativeCardCostsEnergyOrStars(CardModel card, bool includeGlobalModifiers)
    {
        if (_simulator is null) return card.CostsEnergyOrStars(includeGlobalModifiers);
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native free-card picker requested an uncaptured card.");
        if (!includeGlobalModifiers)
            return card.EnergyCost.GetWithModifiers(CostModifiers.Local) > 0 || card.CurrentStarCost > 0;
        var player = _simulator.State.GetPlayerCombatState(card.Owner);
        return !card.EnergyCost.CostsX && predicted.GetEnergyCostValueWithModifiers(_simulator) > 0
            || !card.HasStarCostX && predicted.GetStarCostWithModifiers(_simulator, player) > 0;
    }

    private static void InvokeNativeBuffChange(HextechRelicBase rune, CombatPredictionSimulator simulator,
        SimulatedPowerAmountChange change, bool? sourceKnown, PredictedCard? source)
    {
        if (sourceKnown is null)
            throw new PredictionUnsupportedException("Native buff callback requires captured power provenance.");
        RequireCompleted(Invoke(rune, simulator, model => model.AfterPowerAmountChanged(
            new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, source?.MutablePreview)), rune.GetType());
    }
}
