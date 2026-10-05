using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed class NativeDamageCounter { internal long Value; }
    private sealed class NativeDamageCommandScope(CombatPredictionSimulator simulator, NativeDamageCounter counter)
    {
        internal readonly CombatPredictionSimulator Simulator = simulator;
        internal readonly NativeDamageCounter Counter = counter;
        internal readonly long Id = ++counter.Value;
        internal readonly HashSet<CompensationRune> Compensations = [];
        internal readonly HashSet<PiercingThreadRune> Piercing = [];
        internal CompensationEnemyHex? Enemy;
        internal bool Implicit;
        internal bool TargetActive;
        internal bool ResultsActive;
        internal NativeDamageCommandScope? ImplicitParent;
        internal readonly Dictionary<long, HashSet<SlipperyPower>> SlipperyReductions = [];
        internal readonly Dictionary<long, HashSet<SlipperyPower>> OstySlipperyReductions = [];
    }
    [ThreadStatic] private static NativeDamageCommandScope? _nativeDamageCommand;
    private static readonly FieldInfo NativeCompensationRegistry = AccessTools.DeclaredField(typeof(CompensationRune), "RunesWithPendingCompensation");
    private static readonly FieldInfo NativePiercingRegistry = AccessTools.DeclaredField(typeof(PiercingThreadRune), "RunesWithPendingDamage");
    private static readonly FieldInfo NativeEnemyCompensationRegistry = AccessTools.DeclaredField(typeof(CompensationEnemyHex), "_effectWithPendingCompensation");
    private static readonly FieldInfo NativeEnemyCompensationQueue = AccessTools.DeclaredField(typeof(CompensationEnemyHex), "_pendingCompensations");
    private static readonly Dictionary<MethodBase, (int Loads, int Stores)> NativePendingFieldContracts = [];
    private static readonly MethodInfo NativeActualDamageId = AccessTools.PropertyGetter(typeof(HextechCombatHooks), "CurrentActualDamageCommandId");
    private static readonly Func<CompensationEnemyHex, HextechEnemyHexContext, Creature, decimal, ValueProp, Creature?, CardModel?, decimal> NativeEnemyCompensateHp =
        AccessTools.DeclaredMethod(typeof(CompensationEnemyHex), "ModifyHpLostAfterOsty")
            .CreateDelegate<Func<CompensationEnemyHex, HextechEnemyHexContext, Creature, decimal, ValueProp, Creature?, CardModel?, decimal>>();
    private static readonly Func<CompensationEnemyHex, HextechEnemyHexContext, Creature, DamageResult, Creature?, CardModel?, Task> NativeEnemyCompensateAfter =
        AccessTools.DeclaredMethod(typeof(CompensationEnemyHex), "AfterEnemyDamageReceivedAny")
            .CreateDelegate<Func<CompensationEnemyHex, HextechEnemyHexContext, Creature, DamageResult, Creature?, CardModel?, Task>>();

    private static void RegisterNativeDamageCommandFamily(Harmony harmony)
    {
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ShouldSuppressSleightOfFleshPowerDebuffResponse"));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationRune), "ModifyHpLostAfterOsty"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)), Site(NativeActualDamageId, nameof(CurrentNativeDamageId)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationRune), "AfterDamageReceived"),
            Site(NativeActualDamageId, nameof(CurrentNativeDamageId)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationRune), "IsInActiveCombat"),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), "IsInProgress"), nameof(NativeBranchProgress)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(PiercingThreadRune), "BeforeDamageReceived"),
            Site(NativeActualDamageId, nameof(CurrentNativeDamageId)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationEnemyHex), "ModifyHpLostAfterOsty"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)), Site(NativeActualDamageId, nameof(CurrentNativeDamageId)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationEnemyHex), "AfterEnemyDamageReceivedAny"),
            Site(NativeActualDamageId, nameof(CurrentNativeDamageId)), Site(AccessTools.PropertyGetter(typeof(Creature), "IsAlive"), nameof(NativeBranchIsAlive)));
        PatchPendingCallback(harmony, AccessTools.DeclaredMethod(typeof(CompensationEnemyHex), "CanApplyPendingCompensation"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsAlive"), nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)));
        PatchPendingLambda<CompensationRune, DoomPower>(harmony, "<AfterDamageReceived>b__");
        PatchPendingLambda<CompensationEnemyHex, HextechNextTurnDamagePower>(harmony, "<AfterEnemyDamageReceivedAny>b__");
        foreach (var type in new[] { typeof(CompensationRune), typeof(PiercingThreadRune), typeof(CompensationEnemyHex) })
            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if (EventCallSites.ContainsKey(method)) continue;
                var body = PatchProcessor.GetOriginalInstructions(method);
                if (body.Any(instruction => instruction.operand is FieldInfo field && IsNativePendingRegistry(field)))
                    PatchPendingCallback(harmony, method);
                else NativeCallbackContracts.Add(method);
            }
        RegisterState<CompensationRune>(); RegisterState<PiercingThreadRune>();
        RuneMirrors.RegisterNativeBase<CompensationRune>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(CompensationRune)));
        RuneMirrors.RegisterNativeBase<PiercingThreadRune>();
        ModifyHpLostMirrors.AfterOstyRegistry.Register<CompensationRune>((rune, context) =>
        {
            decimal value = context.Amount;
            Invoke(rune, context.Simulator, model => { value = model.ModifyHpLostAfterOsty(context.Target, context.Amount,
                context.Props, context.Dealer, context.CardSource?.MutablePreview); return Task.CompletedTask; });
            return value;
        });
        AfterDamageReceivedMirrors.Registry.Register<CompensationRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterDamageReceived(new ThrowingPlayerChoiceContext(), context.Target,
                context.Result, context.Props, context.Dealer, context.Source?.MutablePreview)), typeof(CompensationRune)));
        BeforeDamageReceivedMirrors.Registry.Register<PiercingThreadRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeDamageReceived(new ThrowingPlayerChoiceContext(), context.Target,
                context.Amount, context.Props, context.Dealer, context.Source?.MutablePreview)), typeof(PiercingThreadRune)));
        foreach (string name in new[] { "Damage", "DamageSingleTarget" })
        {
            var method = AccessTools.GetDeclaredMethods(typeof(CombatPredictionSimulator))
                .Single(method => method.Name == name && method.GetParameters().Length == 6);
            var enter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterNativeDamageCommand));
            var leave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveNativeDamageCommand));
            harmony.Patch(method, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
            NativeCallbackContracts.AddScope(method, enter, leave);
        }
        // The SDK's small scalar wrappers can already be inlined into their
        // callers before mod initialization. Keep the command alive across the
        // actual target body and its result dispatch in that case as well.
        var targetBody = AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "TryDamageTarget");
        var targetEnter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterImplicitNativeDamage));
        var targetLeave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveImplicitNativeDamageTarget));
        PatchEventCallback(harmony, targetBody, Site(AccessTools.DeclaredMethod(typeof(Hook), "ModifyUnblockedDamageTarget"), nameof(RedirectNativeDamageTarget)));
        harmony.Patch(targetBody, prefix: new HarmonyMethod(targetEnter), finalizer: new HarmonyMethod(targetLeave));
        NativeCallbackContracts.AddScope(targetBody, targetEnter, targetLeave, AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)));
        var results = AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "ProcessDamageResults");
        var resultsEnter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterImplicitNativeDamageResults));
        var resultsLeave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveImplicitNativeDamageResults));
        harmony.Patch(results, prefix: new HarmonyMethod(resultsEnter), finalizer: new HarmonyMethod(resultsLeave));
        NativeCallbackContracts.AddScope(results, resultsEnter, resultsLeave);
        var block = AccessTools.DeclaredMethod(typeof(SimCreatureState), "DamageBlock");
        var blockPrefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ConsumeNativePiercingBlock));
        harmony.Patch(block, prefix: new HarmonyMethod(blockPrefix));
        NativeCallbackContracts.AddNativePrefix(block, blockPrefix, "HextechSolverCompat", Priority.Normal);
        RegisterNativeNextTurnDamage(harmony);
        RegisterNativeSlipperyDamage(harmony);
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.Compensation);
    }

    private static void PatchPendingLambda<T, TPower>(Harmony harmony, string prefix) where TPower : PowerModel
    {
        var method = typeof(T).GetNestedTypes(BindingFlags.NonPublic).SelectMany(AccessTools.GetDeclaredMethods)
            .Single(method => method.Name.StartsWith(prefix));
        PatchPendingCallback(harmony, method, SingleNativePowerSite<TPower>());
    }
    private static bool IsNativePendingRegistry(FieldInfo field)
        => field == NativeCompensationRegistry || field == NativePiercingRegistry || field == NativeEnemyCompensationRegistry
            || field == NativeSlipperyReductions || field == NativeOstySlipperyReductions;
    private static void PatchPendingCallback(Harmony harmony, MethodInfo callback, params NativeCallSite[] sites)
    {
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var target = machine is null ? callback : AccessTools.DeclaredMethod(machine, "MoveNext");
        if (machine is not null) NativeCallbackContracts.Add(callback);
        EventCallSites.Add(target, sites);
        var body = PatchProcessor.GetOriginalInstructions(target);
        NativePendingFieldContracts.Add(target, (body.Count(instruction => instruction.opcode == OpCodes.Ldsfld
            && instruction.operand is FieldInfo field && IsNativePendingRegistry(field)), body.Count(instruction => instruction.opcode == OpCodes.Stsfld
            && instruction.operand is FieldInfo field && IsNativePendingRegistry(field))));
        var rewrite = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(RewriteNativePendingCallback));
        NativeCallbackContracts.Add(target, rewrite);
        harmony.Patch(target, transpiler: new HarmonyMethod(rewrite));
    }
    private static IEnumerable<CodeInstruction> RewriteNativePendingCallback(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int loads = 0, stores = 0;
        foreach (var instruction in RewriteEventCallSites(instructions, __originalMethod))
        {
            if (instruction.operand is FieldInfo field && IsNativePendingRegistry(field))
            {
                string method;
                if (instruction.opcode == OpCodes.Ldsfld)
                { loads++; method = field == NativeCompensationRegistry ? nameof(GetNativeCompensationRegistry)
                    : field == NativePiercingRegistry ? nameof(GetNativePiercingRegistry)
                    : field == NativeSlipperyReductions ? nameof(GetNativeSlipperyReductions)
                    : field == NativeOstySlipperyReductions ? nameof(GetNativeOstySlipperyReductions) : nameof(GetNativeEnemyCompensationRegistry); }
                else if (instruction.opcode == OpCodes.Stsfld && field == NativeEnemyCompensationRegistry)
                { stores++; method = nameof(SetNativeEnemyCompensationRegistry); }
                else throw new InvalidOperationException("Unreviewed pending-damage static field access.");
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), method);
            }
            yield return instruction;
        }
        if (NativePendingFieldContracts[__originalMethod] != (loads, stores))
            throw new InvalidOperationException("Pinned native pending-damage registry accesses changed.");
    }
    private static HashSet<CompensationRune> GetNativeCompensationRegistry()
        => _simulator is null ? (HashSet<CompensationRune>)NativeCompensationRegistry.GetValue(null)!
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.Compensations : [];
    private static HashSet<PiercingThreadRune> GetNativePiercingRegistry()
        => _simulator is null ? (HashSet<PiercingThreadRune>)NativePiercingRegistry.GetValue(null)!
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.Piercing : [];
    private static CompensationEnemyHex? GetNativeEnemyCompensationRegistry()
        => _simulator is null ? (CompensationEnemyHex?)NativeEnemyCompensationRegistry.GetValue(null)
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.Enemy : null;
    private static void SetNativeEnemyCompensationRegistry(CompensationEnemyHex? effect)
    {
        if (_simulator is null) NativeEnemyCompensationRegistry.SetValue(null, effect);
        else if (_nativeDamageCommand?.Simulator == _simulator) _nativeDamageCommand.Enemy = effect;
        else if (effect is not null) throw new PredictionUnsupportedException("Enemy compensation enqueued outside its owned damage command.");
    }
    private static long CurrentNativeDamageId()
        => _simulator is null ? HextechCombatHooks.CurrentActualDamageCommandId
            : _nativeDamageCommand?.Simulator == _simulator ? _nativeDamageCommand.Id : 0;
    private static void EnterNativeDamageCommand(CombatPredictionSimulator __instance, object[] __args, out NativeDamageCommandScope? __state)
    {
        __state = _nativeDamageCommand;
        if (__state?.Simulator != __instance && !__instance.State.CombatState.Players.SelectMany(player => player.Relics)
            .Any(rune => rune is CompensationRune or PiercingThreadRune)
            && !PowerSourceBridge.HasCapturedHex((SimulatedCombatState)__instance.State.CombatState, MonsterHexKind.Compensation)
            && !((SimulatedCombatState)__instance.State.CombatState).EffectivePowers().Any(power => power is SlipperyPower && power.Amount > 0)) return;
        _nativeDamageCommand = new(__instance, __state?.Simulator == __instance ? __state.Counter : new NativeDamageCounter());
    }
    private static void LeaveNativeDamageCommand(NativeDamageCommandScope? __state)
    {
        var scope = _nativeDamageCommand;
        if (ReferenceEquals(scope, __state)) return;
        var previous = _simulator;
        _simulator = scope!.Simulator;
        try
        {
            if (_simulator.HasPendingChoice) _simulator.RejectExecutionContinuation();
            CompensationRune.ClearPendingCompensations(scope.Id);
            CompensationEnemyHex.ClearPendingCompensations(scope.Id);
            PiercingThreadRune.ClearPendingDamage(scope.Id);
            try { RequireCompleted(OriginalConsumeOstySlippery(scope.Id), typeof(SlipperyPower)); }
            finally { OriginalClearSlippery(scope.Id); }
        }
        finally { _simulator = previous; _nativeDamageCommand = __state; }
    }
    private static void EnterImplicitNativeDamage(CombatPredictionSimulator __instance, object[] __args, out NativeDamageCommandScope? __state)
    {
        __state = _nativeDamageCommand;
        if (__state?.Simulator == __instance && !__state.TargetActive && !__state.ResultsActive)
        { __state.TargetActive = true; return; }
        EnterNativeDamageCommand(__instance, __args, out _);
        if (!ReferenceEquals(__state, _nativeDamageCommand))
        { _nativeDamageCommand!.Implicit = true; _nativeDamageCommand.ImplicitParent = __state; }
        if (_nativeDamageCommand is { } current && current.Simulator == __instance) current.TargetActive = true;
    }
    private static void LeaveImplicitNativeDamageTarget(CombatPredictionSimulator __instance, NativeDamageCommandScope? __state, bool __result, Exception? __exception)
    {
        if (_nativeDamageCommand is { } current && current.Simulator == __instance) current.TargetActive = false;
        if ((__exception is not null || !__result) && !ReferenceEquals(__state, _nativeDamageCommand))
            LeaveNativeDamageCommand(__state);
    }
    private static void EnterImplicitNativeDamageResults(CombatPredictionSimulator __instance, out NativeDamageCommandScope? __state)
    {
        __state = _nativeDamageCommand is { } scope && scope.Simulator == __instance ? scope : null;
        if (__state is not null) __state.ResultsActive = true;
    }
    private static void LeaveImplicitNativeDamageResults(NativeDamageCommandScope? __state)
    {
        if (__state is not null) __state.ResultsActive = false;
        if (__state is { Implicit: true } && ReferenceEquals(_nativeDamageCommand, __state))
            LeaveNativeDamageCommand(__state.ImplicitParent);
    }
    private static void ConsumeNativePiercingBlock(SimCreatureState __instance, ref decimal amount, ValueProp props)
    {
        if (_nativeDamageCommand is not { } scope || scope.Simulator.State.GetCreature(__instance.Creature) != __instance) return;
        var previous = _simulator; _simulator = scope.Simulator;
        try { if (PiercingThreadRune.TryTakeBlockableDamage(scope.Id, __instance.Creature, amount, props, out decimal blockable)) amount = blockable; }
        finally { _simulator = previous; }
    }
    internal static void AssertNativeEnemyCompensationEmpty(CompensationEnemyHex? effect)
    {
        if (effect is not null && ((IList)NativeEnemyCompensationQueue.GetValue(effect)!).Count != 0)
            throw new PredictionUnsupportedException("Enemy compensation must finish before capture/fork/hash.");
    }
    internal static void AssertLiveEnemyCompensationEmpty()
        => AssertNativeEnemyCompensationEmpty((CompensationEnemyHex?)NativeEnemyCompensationRegistry.GetValue(null));
    internal static decimal DispatchNativeEnemyCompensationHp(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, decimal amount, ValueProp props, Creature? dealer, CardModel? source)
    {
        if (!state.Has(MonsterHexKind.Compensation)) return amount;
        var effect = state.Compensation ??= new CompensationEnemyHex();
        decimal value = amount;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { value = NativeEnemyCompensateHp(effect, context, target, amount, props, dealer, source); return Task.CompletedTask; });
        return value;
    }
    internal static void DispatchNativeEnemyCompensationAfter(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? source)
    {
        if (state.Compensation is { } effect) InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyCompensateAfter(effect, context, target, result, dealer, source));
    }

    private static void RegisterNativeNextTurnDamage(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechNextTurnDamagePower), "AfterSideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsAlive"), nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)),
            SingleNativePowerSite<HextechNextTurnDamagePower>());
        var lambda = typeof(HextechNextTurnDamagePower).GetNestedTypes(BindingFlags.NonPublic).SelectMany(AccessTools.GetDeclaredMethods)
            .Single(method => method.Name.StartsWith("<AfterSideTurnStart>b__"));
        PatchEventCallback(harmony, lambda, Site(NativeDamageAmount, nameof(DamageNativeAmount)));
        foreach (string method in new[] { "GetDamageToResolve", "RunWithDamageResolutionGuard", "get_IsResolvingDamage" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechNextTurnDamagePower), method));
        RegisterNativeSoulPower<HextechNextTurnDamagePower>();
        NativeAfterSidePowers.Add(typeof(HextechNextTurnDamagePower), (power, simulator, side, _) =>
            InvokeNativePower((HextechNextTurnDamagePower)power, simulator, model => model.AfterSideTurnStart(side, simulator.State.CombatState)));
        var semantics = AccessTools.DeclaredMethod(typeof(PowerLifecycleSupport), "SemanticallyRelevantAmountOnTurnStart");
        var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(NativeDelayedDamageStartAmount));
        harmony.Patch(semantics, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(semantics, prefix, "HextechSolverCompat", Priority.Normal);
    }
    private static bool NativeDelayedDamageStartAmount(PowerModel power, ref int __result)
    {
        if (power is not HextechNextTurnDamagePower) return true;
        __result = power.AmountOnTurnStart; return false;
    }
}
