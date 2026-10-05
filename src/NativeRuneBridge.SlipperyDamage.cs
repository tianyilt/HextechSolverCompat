using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly FieldInfo NativeSlipperyReductions = AccessTools.DeclaredField(typeof(HextechCombatHooks), "SlipperyReductionsByCommand");
    private static readonly FieldInfo NativeOstySlipperyReductions = AccessTools.DeclaredField(typeof(HextechCombatHooks), "OstyRedirectSlipperyByCommand");
    private delegate void SlipperyAfterDelegate(SlipperyPower power, Creature target, DamageResult result, ref Task task);
    private static readonly SlipperyAfterDelegate OriginalSlipperyAfter = AccessTools.DeclaredMethod(
        AccessTools.Inner(typeof(HextechCombatHooks), "SlipperyDamageReceivedPatch"), "Postfix").CreateDelegate<SlipperyAfterDelegate>();
    private static readonly Func<long, Task> OriginalConsumeOstySlippery = AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ConsumeOstyRedirectedSlippery").CreateDelegate<Func<long, Task>>();
    private static readonly Action<long> OriginalClearSlippery = AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ClearSlipperyReductions").CreateDelegate<Action<long>>();

    private static void RegisterNativeSlipperyDamage(Harmony harmony)
    {
        foreach (string name in new[] { "SlipperyHpLostPatch", "SlipperyDamageReceivedPatch", "DieForYouTargetPatch" })
        {
            var method = AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechCombatHooks), name), "Postfix");
            if (name == "DieForYouTargetPatch")
            {
                var get = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPower" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(SlipperyPower));
                PatchPendingCallback(harmony, method, Site(NativeActualDamageId, nameof(CurrentNativeDamageId)), Site(get, nameof(GetNativeSlippery)));
            }
            else PatchPendingCallback(harmony, method, Site(NativeActualDamageId, nameof(CurrentNativeDamageId)));
        }
        var decrement = AccessTools.DeclaredMethod(typeof(HextechPowerCmdCompat), "Decrement");
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "AppendSlipperyConsumption"), Site(decrement, nameof(DecrementNativeSlippery)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ConsumeOstyRedirectedSlippery"), Site(decrement, nameof(DecrementNativeSlippery)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ClearSlipperyReductions"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "IsVanillaFixActiveFor"));

        var hpMirror = AccessTools.DeclaredMethod(typeof(ModifyHpLostMirrors), "HandleSlipperyPower");
        var enter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterNativeSlipperyHp));
        var leave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveNativeSlipperyHp));
        harmony.Patch(hpMirror, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
        NativeCallbackContracts.AddScope(hpMirror, enter, leave);
        var after = AccessTools.DeclaredMethod(typeof(AfterDamageReceivedMirrors), "HandleSlipperyPower");
        var afterPatch = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(AfterNativeSlipperyMirror));
        harmony.Patch(after, postfix: new HarmonyMethod(afterPatch));
        NativeCallbackContracts.AddNativePostfix(after, afterPatch, "HextechSolverCompat", Priority.Normal);
    }
    private static void EnterNativeSlipperyHp(ModifyHpLostMirrorContext context, out CombatPredictionSimulator? __state)
    { __state = _simulator; _simulator = context.Simulator; }
    private static void LeaveNativeSlipperyHp(CombatPredictionSimulator? __state) => _simulator = __state;
    private static void AfterNativeSlipperyMirror(SlipperyPower power, AfterDamageReceivedMirrorContext context)
    {
        if (_nativeDamageCommand?.Simulator != context.Simulator) return;
        var previous = _simulator; _simulator = context.Simulator;
        try
        {
            context.Simulator.SynchronizePowerAmountPredictionStates();
            Task task = Task.CompletedTask;
            OriginalSlipperyAfter(power, context.Target, context.Result, ref task);
            RequireCompleted(task, typeof(SlipperyPower));
        }
        finally { _simulator = previous; }
    }
    private static SlipperyPower? GetNativeSlippery(Creature creature)
        => _simulator is null ? creature.GetPower<SlipperyPower>() : ((SimulatedCombatState)_simulator.State.CombatState).GetPower<SlipperyPower>(creature);
    private static Creature RedirectNativeDamageTarget(ICombatState combat, Creature target, decimal amount, ValueProp props, Creature? dealer)
    {
        var previous = _simulator;
        if (_nativeDamageCommand is { } scope && ReferenceEquals(scope.Simulator.State.CombatState, combat)) _simulator = scope.Simulator;
        try { return Hook.ModifyUnblockedDamageTarget(combat, target, amount, props, dealer); }
        finally { _simulator = previous; }
    }
    private static Task DecrementNativeSlippery(PowerModel power)
    {
        if (_simulator is null) return HextechPowerCmdCompat.Decrement(power);
        if (power is not SlipperyPower) throw new PredictionUnsupportedException("Slippery cleanup received an unreviewed power.");
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        var owned = combat.GetPower<SlipperyPower>(power.Owner);
        if (owned is null || _simulator.StateStore.GetPowerAmount(owned).Amount <= 0) return Task.CompletedTask;
        _simulator.StateStore.GetPowerAmount(owned).Decrement();
        _simulator.SynchronizePowerAmountPredictionStates();
        owned = combat.GetPower<SlipperyPower>(power.Owner) ?? owned;
        combat.RecordPowerAmountChange(owned, -1, null);
        PowerLifecycleSupport.ResolvePowerAmountChanges(_simulator, combat);
        return Task.CompletedTask;
    }
    private static Dictionary<long, HashSet<SlipperyPower>> GetNativeSlipperyReductions()
        => _simulator is null ? (Dictionary<long, HashSet<SlipperyPower>>)NativeSlipperyReductions.GetValue(null)!
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.SlipperyReductions : [];
    private static Dictionary<long, HashSet<SlipperyPower>> GetNativeOstySlipperyReductions()
        => _simulator is null ? (Dictionary<long, HashSet<SlipperyPower>>)NativeOstySlipperyReductions.GetValue(null)!
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.OstySlipperyReductions : [];
}
