using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal sealed partial class NativeRuneState
{
    private static readonly FieldInfo SweepingContext = AccessTools.DeclaredField(typeof(SweepingBladeRune), "_activeContext");
    private static bool IsSweepingContext(FieldInfo field) => field == SweepingContext;
    private static void AssertSweepingContextEmpty(HextechRelicBase source)
    {
        if (source is SweepingBladeRune && SweepingContext.GetValue(source) is not null)
            throw new PredictionUnsupportedException("SweepingBlade's attack context must complete at the enclosing card boundary before capture/fork/hash.");
    }
}

internal static partial class NativeRuneBridge
{
    private static readonly PropertyInfo SweepingPowerCardSource = AccessTools.Property(typeof(SimulatedCombatState), "CurrentPowerCardSource");
    private static void RegisterNativeSweepingBlade(Harmony harmony)
    {
        foreach (string name in new[] { "BeforeAttack", "BeforeCardPlayed" })
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SweepingBladeRune), name),
                Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
                Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat), 2));
        var apply = AccessTools.DeclaredMethod(typeof(HextechPowerCmdCompat), "Apply",
            [typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool)]);
        foreach (string name in new[] { "BeforePowerAmountChanged", "AfterPowerAmountChanged" })
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SweepingBladeRune), name), Site(apply, nameof(ApplyNativeCopiedDebuff)));
        foreach (var type in new[] { typeof(SweepingBladeRune), AccessTools.Inner(typeof(SweepingBladeRune), "SweepingBladeContext") })
            foreach (var method in AccessTools.GetDeclaredMethods(type))
                if (!EventCallSites.ContainsKey(method) && method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() is null)
                    NativeCallbackContracts.Add(method);
        RegisterNativeBeforeAfterCard<SweepingBladeRune>();
        BeforeAttackMirrors.Registry.Register<SweepingBladeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeAttack(context.Command)), typeof(SweepingBladeRune)));
        AfterAttackMirrors.Registry.Register<SweepingBladeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterAttack(new ThrowingPlayerChoiceContext(), context.Command)), typeof(SweepingBladeRune)));

        var before = AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "ModifyPowerAmountForRelics");
        var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(BeforeNativeSweepingPower));
        harmony.Patch(before, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(before, prefix, "HextechSolverCompat", Priority.Normal);
        NativeCallbackContracts.AddNativePostfix(before, AccessTools.DeclaredMethod(typeof(PowerSourceBridge), "ModifyReceivedAmount"), "HextechSolverCompat", Priority.Normal);
    }
    private static void BeforeNativeSweepingPower(SimulatedCombatState __instance, PowerModel power, Creature target, int amount, Creature? applier)
    {
        if (amount == 0 || !CombatSimulators.TryGetValue(__instance, out var simulator)) return;
        var card = (CardModel?)SweepingPowerCardSource.GetValue(__instance);
        if (card is null) return;
        foreach (var rune in __instance.IterateHookListeners().OfType<SweepingBladeRune>().ToArray())
            RequireCompleted(Invoke(rune, simulator, model => model.BeforePowerAmountChanged(power, amount, target, applier, card)), typeof(SweepingBladeRune));
    }
    private static void AfterNativeSweepingPower(SweepingBladeRune rune, CombatPredictionSimulator simulator,
        SimulatedPowerAmountChange change, PredictedCard? card)
        => RequireCompleted(Invoke(rune, simulator, model => model.AfterPowerAmountChanged(new ThrowingPlayerChoiceContext(),
            change.Power, change.Delta, change.Applier, card?.MutablePreview)), typeof(SweepingBladeRune));
}
