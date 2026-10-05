using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeCardInstanceLedgers(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BloodDebtRune), "AfterCurrentHpChanged"),
            Site(dead, nameof(NativeBranchIsDead)));
        RegisterNativeHpChange<BloodDebtRune>();
        RegisterNativeQueryCallbacks<BloodDebtRune>(NativeQueries.DamageAdditive);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BloodDebtRune), "GrowAttacks"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BloodDebtRune), "AfterCloned"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EndlessRotationRune), "AfterShuffle"),
            Site(dead, nameof(NativeBranchIsDead)));
        RegisterState<EndlessRotationRune>(); RuneMirrors.RegisterNativeBase<EndlessRotationRune>();
        RegisterNativeQueryCallbacks<EndlessRotationRune>(NativeQueries.Stars);
        AfterShuffleMirrors.Registry.Register<EndlessRotationRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterShuffle(new ThrowingPlayerChoiceContext(), context.Player)), typeof(EndlessRotationRune)));
        AfterSideTurnEndLateMirrors.Register<EndlessRotationRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterSideTurnEndLate(new ThrowingPlayerChoiceContext(), context.Side, context.Participants)), typeof(EndlessRotationRune)));
        foreach (string name in new[] { "MakeFreeForTurn", "AfterCloned" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(EndlessRotationRune), name));

        var getStorm = NativePowerAmount.DeclaringType!.GetMethods().Single(method => method.Name == "GetPower" && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(StormUpgradeRune), "RecordStormBeforeCardPlayed"),
            new(getStorm.MakeGenericMethod(typeof(StormPower)), AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(StormPower)), 1),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), "Amount"), nameof(NativeLedgerStormAmount)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(StormUpgradeRune), "ChannelRecordedLightningAsync"),
            Site(NativeOrbChannel, nameof(ChannelOrb)));
        RegisterState<StormUpgradeRune>(); RuneMirrors.RegisterNativeBase<StormUpgradeRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(StormUpgradeRune), "FindForCardPlay"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(StormUpgradeRune), "ShouldTrigger"));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(BeforeCardPlayedMirrors), "HandleStormPower"), nameof(NativeStockStormBefore));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(AfterCardPlayedMirrors), "HandleStormPower"), nameof(NativeStockStormAfter));
    }
    private static int NativeLedgerStormAmount(PowerModel power)
        => _simulator is null ? power.Amount : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<StormPower>(power.Owner);
    private static bool NativeUsesUpgradedStorm(CombatPredictionSimulator simulator, StormPower power)
        => power.Owner.Player is { } player && simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().Any()
            && ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player).OfType<StormUpgradeRune>().Any();
    private static bool NativeStockStormBefore(StormPower power, BeforeCardPlayedMirrorContext context)
        => !NativeUsesUpgradedStorm(context.Simulator, power);
    private static bool NativeStockStormAfter(StormPower power, AfterCardPlayedMirrorContext context)
        => !NativeUsesUpgradedStorm(context.Simulator, power);

    internal static void RecordNativeStorm(CombatPredictionSimulator simulator, CardPlay play)
    {
        foreach (var rune in ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(play.Card.Owner).OfType<StormUpgradeRune>())
            Invoke(rune, simulator, model => { model.RecordStormBeforeCardPlayed(play); return true; });
    }
    internal static void ChannelNativeStorm(CombatPredictionSimulator simulator, CardPlay play)
    {
        foreach (var rune in ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(play.Card.Owner).OfType<StormUpgradeRune>())
            RequireCompleted(Invoke(rune, simulator, model => model.ChannelRecordedLightningAsync(new ThrowingPlayerChoiceContext(), play)), typeof(StormUpgradeRune));
    }
}
