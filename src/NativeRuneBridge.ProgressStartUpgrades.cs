using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeProgressStartUpgrades(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ArcanePunchRune), "GainEnergyForAttackThreshold"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DevilsDanceRune), "GainMaxHpForAttackThreshold"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp), [typeof(Creature), typeof(decimal)]), nameof(GainNativeMaxHp)));
        RegisterNativeAttackProgress<ArcanePunchRune>(); RegisterNativeAttackProgress<DevilsDanceRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MakeItMineRune), nameof(AbstractModel.AfterPlayerTurnStart)),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.Method(typeof(OstyCmd), nameof(OstyCmd.Summon),
                [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        RegisterState<MakeItMineRune>(); RuneMirrors.RegisterNativeBase<MakeItMineRune>();
        RegisterNativeStartCallback<MakeItMineRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TranscendentEvilRune), "AfterSideTurnStart"),
            SingleNativePowerSite<FocusPower>(),
            Site(AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.AddSlots)), nameof(AddNativeOrbSlots)));
        RegisterState<TranscendentEvilRune>(); RuneMirrors.RegisterNativeBase<TranscendentEvilRune>();
        RegisterNativeSideStartCallback<TranscendentEvilRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechRoundInterval), "TryClaimRound"));

        var royaltiesAmount = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == nameof(Creature.GetPowerAmount)
            && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(RoyaltiesPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RoyaltiesUpgradeRune), nameof(AbstractModel.AfterPlayerTurnStart)),
            Site(combat, nameof(NativeBranchCombat)),
            new NativeCallSite(royaltiesAmount, AccessTools.Method(typeof(NativeRuneBridge), nameof(GetPowerAmount)).MakeGenericMethod(typeof(RoyaltiesPower)), 1),
            Site(NativeGainGold, nameof(GainNativeGold)));
        RegisterState<RoyaltiesUpgradeRune>(); RuneMirrors.RegisterNativeBase<RoyaltiesUpgradeRune>();
        RegisterNativeStartCallback<RoyaltiesUpgradeRune>();

        PatchEventCallback(harmony, NativeLambda(typeof(SubroutineUpgradeRune), "BeforeHandDraw"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SubroutineUpgradeRune), "BeforeHandDraw"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards)), nameof(NativeOwnedCombatCards)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SubroutineUpgradeRune), "TryConsumeCombatStartMove"));
        RegisterBeforeHandDrawRune<SubroutineUpgradeRune>();
        RegisterGeneratedBeforeHandDrawRune<SendThemInRune>(harmony);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechStableCombatSpawns), "CreateMinionCard"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RoyalTrialRune), nameof(AbstractModel.AfterCardPlayed)),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardsToCombat"), nameof(AddNativeGeneratedCards)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(RoyalTrialRune), "ShouldGenerateMinions"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(RoyalTrialRune), "CreateRandomMinionCard"));
        RegisterStableState<RoyalTrialRune>(); RuneMirrors.RegisterNativeBase<RoyalTrialRune>();
        RegisterAfterCardPlayedCallback<RoyalTrialRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ReanimateUpgradeRune), "RefreshReanimateCostsInHand"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(AccessTools.DeclaredMethod(typeof(CardModel), nameof(CardModel.InvokeEnergyCostChanged)), nameof(SkipNativeCostRefresh)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ReanimateUpgradeRune), nameof(AbstractModel.AfterDeath)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ReanimateUpgradeRune), "ShouldCountDeath"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ReanimateUpgradeRune), nameof(AbstractModel.TryModifyEnergyCostInCombat)));
        RegisterNativeDeathCallback<ReanimateUpgradeRune>();
        RegisterNativeQueryCallbacks<ReanimateUpgradeRune>(NativeQueries.Energy);

        RegisterNativeReceivedRune<EternalArmorUpgradeRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(EternalArmorUpgradeRune), nameof(AbstractModel.AfterCardPlayed)));
        RegisterAfterCardPlayedCallback<EternalArmorUpgradeRune>();
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "TriggerBaseSideTurnStart"),
            nameof(NativeEternalPlatingDecrement));
    }

    private static void RegisterNativeAttackProgress<T>() where T : HextechRelicBase
    {
        RegisterState<T>(); RuneMirrors.RegisterNativeBase<T>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), nameof(AbstractModel.AfterCardPlayed)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), nameof(AbstractModel.AfterCardPlayedLate)));
        RegisterAfterCardPlayedCallback<T>();
        AfterCardPlayedMirrors.LateRegistry.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardPlayedLate(
                new ThrowingPlayerChoiceContext(), context.CardPlay)), typeof(T)));
    }

    private static void RegisterNativeStartCallback<T>() where T : HextechRelicBase
        => AfterPlayerTurnStartMirrors.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));

    private static void SkipNativeCostRefresh(CardModel card)
    { if (_simulator is null) card.InvokeEnergyCostChanged(); }

    private static void NativeEternalPlatingDecrement(SimulatedCombatState __instance,
        CombatPredictionSimulator simulator, Creature owner, ref bool decrementPlating)
    {
        if (!decrementPlating || owner.Player is not { } player || __instance.GetPower<PlatingPower>(owner) is not { Amount: > 0 } plating
            || __instance.RelicsOf(player).OfType<EternalArmorUpgradeRune>().SingleOrDefault() is not { } rune) return;
        bool suppress = Invoke(rune, simulator, model => model.TryModifyPowerAmountReceived(ModelDb.Power<PlatingPower>(),
            owner, -plating.DynamicVars["Decrement"].BaseValue, null, out decimal changed) && changed == 0m);
        if (suppress) decrementPlating = false;
    }
}
