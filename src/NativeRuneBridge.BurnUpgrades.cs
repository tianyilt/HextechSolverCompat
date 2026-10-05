using System.Reflection;
using HarmonyLib;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private delegate bool NativeFlameBarrierPrefix(FlameBarrierPower power, PlayerChoiceContext context,
        Creature target, ValueProp props, Creature? dealer, ref Task result);
    private static NativeFlameBarrierPrefix NativeFlameBarrier = null!;
    [ThreadStatic] private static SimulatedCombatState? _nativeXCombat;

    private static NativeCallSite NativeRelicSite<T>(string replacement) where T : RelicModel
        => new(typeof(Player).GetMethods().Single(m => m.Name == nameof(Player.GetRelic) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(T)), AccessTools.Method(typeof(NativeRuneBridge), replacement).MakeGenericMethod(typeof(T)), 1);

    private static void RegisterNativeBurnUpgrades(Harmony harmony)
    {
        RegisterAfterCardPlayed<BashUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BashUpgradeRune), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            SingleNativePowerSite<StrengthPower>());
        RuneMirrors.RegisterNativeBase<FlameBarrierUpgradeRune>();
        RuneMirrors.RegisterNativeBase<InfernoUpgradeRune>();
        RuneMirrors.RegisterNativeBase<WhirlwindUpgradeRune>();
        var flamePrefix = AccessTools.Method(AccessTools.Inner(typeof(FlameBarrierUpgradeRune), "BurnRetaliationPatch"), "Prefix");
        NativeFlameBarrier = flamePrefix.CreateDelegate<NativeFlameBarrierPrefix>();
        PatchEventCallback(harmony, flamePrefix, NativeRelicSite<FlameBarrierUpgradeRune>(nameof(NativeResourceRelic)),
            new(NativeContextPower.MakeGenericMethod(typeof(HextechBurnPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(HextechBurnPower)), 1));
        NativeCallbackContracts.AddNativePrefix(AccessTools.Method(typeof(FlameBarrierPower), "AfterDamageReceived"),
            flamePrefix, "Natsuki.HextechRunes", Priority.Low);
        var infernoPrefix = AccessTools.Method(AccessTools.Inner(typeof(InfernoUpgradeRune), "InfernoBurnPatch"), "Prefix");
        NativeCallbackContracts.AddNativePrefix(AccessTools.Method(typeof(InfernoPower), "AfterDamageReceived"),
            infernoPrefix, "Natsuki.HextechRunes", Priority.Low);
        PatchEventCallback(harmony, AccessTools.Method(typeof(InfernoUpgradeRune), "DamageAndBurn"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature)]),
                nameof(NativeInfernoDamage)),
            new(NativeContextPower.MakeGenericMethod(typeof(HextechBurnPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(HextechBurnPower)), 1),
            Site(AccessTools.PropertyGetter(typeof(NCombatRoom), nameof(NCombatRoom.Instance)), nameof(NativeInfernoRoom)));
        RegisterNativeSdkPrefix(harmony, AccessTools.Method(typeof(AfterDamageReceivedMirrors), "HandleFlameBarrierPower"),
            nameof(NativeFlameBarrierResponse));
        RegisterNativeSdkPrefix(harmony, AccessTools.Method(typeof(AfterDamageReceivedMirrors), "HandleInfernoPower"),
            nameof(NativeInfernoResponse));

        PatchEventCallback(harmony, AccessTools.Method(typeof(WhirlwindUpgradeRune), "TryDoubleResolvedX"),
            NativeRelicSite<WhirlwindUpgradeRune>(nameof(NativeXRelic)));
        var nativeX = AccessTools.Method(AccessTools.Inner(typeof(WhirlwindUpgradeRune), "WhirlwindXValuePatch"), "Postfix");
        NativeCallbackContracts.AddNativePostfix(AccessTools.Method(typeof(CardModel), nameof(CardModel.ResolveEnergyXValue), []),
            nativeX, "Natsuki.HextechRunes", Priority.Normal);
        var sdkX = AccessTools.Method(typeof(CombatPredictedCardExtensions), nameof(CombatPredictedCardExtensions.ResolveEnergyXValue));
        var postfix = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeWhirlwindX));
        harmony.Patch(sdkX, postfix: new HarmonyMethod(postfix));
        NativeCallbackContracts.AddNativePostfix(sdkX, postfix, "HextechSolverCompat", Priority.Normal);
    }
    private static void RegisterNativeSdkPrefix(Harmony harmony, MethodInfo target, string name)
    {
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), name);
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(target, prefix, "HextechSolverCompat", Priority.Normal);
    }
    private static bool NativeFlameBarrierResponse(FlameBarrierPower power, AfterDamageReceivedMirrorContext context)
    {
        var combat = (SimulatedCombatState)context.CombatState;
        if (power.Owner.Player is not { } player || !combat.RelicsOf(player).OfType<FlameBarrierUpgradeRune>().Any()) return true;
        var previous = _simulator; _simulator = context.Simulator;
        try
        {
            Task result = null!;
            if (NativeFlameBarrier(power, new ThrowingPlayerChoiceContext(), context.Target, context.Props, context.Dealer, ref result))
                throw new PredictionUnsupportedException("Captured FlameBarrier rune disagreed with original native prefix.");
            RequireCompleted(result, typeof(FlameBarrierUpgradeRune));
            return false;
        }
        finally { _simulator = previous; }
    }
    private static bool NativeInfernoResponse(InfernoPower power, AfterDamageReceivedMirrorContext context)
    {
        var combat = (SimulatedCombatState)context.CombatState;
        if (power.Owner.Player is not { } player || !combat.RelicsOf(player).OfType<InfernoUpgradeRune>().Any()) return true;
        var previous = _simulator; _simulator = context.Simulator;
        try
        {
            RequireCompleted(InfernoUpgradeRune.DamageAndBurn(new ThrowingPlayerChoiceContext(), power, context.Target, context.Result),
                typeof(InfernoUpgradeRune));
            return false;
        }
        finally { _simulator = previous; }
    }
    private static Task<IEnumerable<DamageResult>> NativeInfernoDamage(PlayerChoiceContext context, IEnumerable<Creature> targets,
        decimal amount, ValueProp props, Creature dealer)
    {
        if (_simulator is null) return CreatureCmd.Damage(context, targets, amount, props, dealer);
        var results = _simulator.Damage(targets.ToArray(), amount, props, dealer, null, null);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<DamageResult>>(results);
    }
    private static NCombatRoom? NativeInfernoRoom() => _simulator is null ? NCombatRoom.Instance : null;
    private static T? NativeXRelic<T>(Player player) where T : RelicModel
        => _nativeXCombat is null ? player.GetRelic<T>() : _nativeXCombat.RelicsOf(player).OfType<T>().FirstOrDefault();
    private static void NativeWhirlwindX(PredictedCard card, CombatPredictionState state, ref int __result)
    {
        if (card.Preview is not Whirlwind) return;
        var previous = _nativeXCombat; _nativeXCombat = (SimulatedCombatState)state.CombatState;
        try { WhirlwindUpgradeRune.TryDoubleResolvedX(card.MutablePreview, ref __result); }
        finally { _nativeXCombat = previous; }
    }
}
