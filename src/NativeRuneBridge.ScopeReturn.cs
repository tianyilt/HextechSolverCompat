using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeScopeReturnFamily(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(UniversalScopeRuneBase), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Relics)), nameof(NativeScopeRelics)),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)),
            Site(AccessTools.Method(typeof(HextechAutoPlayHelper), "IsTransientAutoPlayCard"), nameof(NativeBranchTransientCard)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)),
            Site(AccessTools.Method(typeof(CardTransformUpgradeHelper), "RestoreUpgradeLevel"), nameof(RestoreNativeUpgradeLevel)),
            Site(NativeGainEnergy, nameof(GainNativeEnergy)),
            Site(AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainStars)), nameof(GainNativeStars)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(UniversalScopeRuneBase), "RollTrigger"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(UniversalScopeRuneBase), "CombineChancePercent"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatHooks), "GetResourceSpend"));
        var getRelic = typeof(Player).GetMethods().Single(method => method.Name == nameof(Player.GetRelic) && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, AccessTools.Method(typeof(StardustUpgradeRune), "ShouldPreserveStars"),
            new NativeCallSite(getRelic.MakeGenericMethod(typeof(StardustUpgradeRune)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeResourceRelic)).MakeGenericMethod(typeof(StardustUpgradeRune)), 1));
        RegisterNativeScope<UniversalScopeRune>();
        RegisterNativeScope<MoreUniversalScopeRune>();
        RegisterNativeScope<MostUniversalScopeRune>();
    }

    private static void RegisterNativeScope<T>() where T : UniversalScopeRuneBase
    {
        RegisterStableState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        RegisterAfterCardPlayedCallback<T>();
    }

    private static IReadOnlyList<RelicModel> NativeScopeRelics(Player player)
        => _simulator is not { } sim ? player.Relics : ((SimulatedCombatState)sim.State.CombatState).RelicsOf(player)
            .Select(relic => relic is UniversalScopeRuneBase
                ? (RelicModel)ModelPredictionStateMirrors.Get<NativeRuneState>(sim, relic).Model : relic).ToArray();

    private static bool NativeBranchTransientCard(CardModel card)
        => _simulator is null ? HextechAutoPlayHelper.IsTransientAutoPlayCard(card)
            : _nativeTransientCards?.Contains(card) == true
                || _simulator.State.FindCard(card) is { } predicted && _nativeTransientCards?.Any(predicted.References) == true;

    private static T? NativeResourceRelic<T>(Player player) where T : RelicModel
        => _simulator is null ? player.GetRelic<T>()
            : ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(player).OfType<T>().FirstOrDefault();

    private static Task<CardPileAddResult> MoveNativeCard(CardModel card, PileType pile,
        CardPilePosition position, AbstractModel? source, bool skipVisuals)
    {
        if (_simulator is null) return CardPileCmd.Add(card, pile, position, source, skipVisuals);
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native move card is absent in the branch.");
        var oldPile = NativeBranchCardPile(card);
        var result = _simulator.AddToPile(predicted, pile, position);
        PauseNativeChoice(_simulator);
        return Task.FromResult(new CardPileAddResult { success = result.Success, cardAdded = card,
            oldPile = oldPile, targetPile = pile, modifyingModels = [] });
    }

    private static void RestoreNativeUpgradeLevel(CardModel card, int captured)
    {
        if (_simulator is null) { CardTransformUpgradeHelper.RestoreUpgradeLevel(card, captured); return; }
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native upgrade restoration target is absent in the branch.");
        int steps = CardTransformUpgradeHelper.GetUpgradeRestorationSteps(card.CurrentUpgradeLevel, captured, card.MaxUpgradeLevel);
        for (int step = 0; step < steps && predicted.Preview.IsUpgradable; step++) predicted.Upgrade();
    }
}
