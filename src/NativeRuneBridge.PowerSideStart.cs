using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record SidePowerScope(SimulatedCombatState Combat, PowerModel Power);
    private sealed class SidePowerPhaseScope(CombatPredictionSimulator simulator)
    {
        internal readonly CombatPredictionSimulator Simulator = simulator;
        internal bool Handled;
    }
    private sealed record SidePowerPhaseParent(SidePowerPhaseScope? Phase, SidePowerScope? Power);
    [ThreadStatic] private static SidePowerPhaseScope? _sidePowerPhase;
    [ThreadStatic] private static SidePowerScope? _sidePowerScope;
    private static readonly Dictionary<Type, Action<PowerModel, CombatPredictionSimulator, CombatSide, IReadOnlyList<Creature>>> NativeAfterSidePowers = [];

    private static void RegisterNativePowerSideStart(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.Method(typeof(NeurosurgeUpgradeRune), "PlayUpgraded"),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim),
                [typeof(Creature), typeof(string), typeof(float)]), nameof(QuantumAnim)),
            Site(NativeGainEnergy, nameof(GainNativeEnergy)), Site(NativeDraw, nameof(Draw)),
            new NativeCallSite(NativeContextPower.MakeGenericMethod(typeof(HextechNeurosurgePower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(HextechNeurosurgePower)), 1));
        RegisterConditionalPlay<Neurosurge, NeurosurgeUpgradeRune>((_, context) => NeurosurgeUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (Neurosurge)context.Card.MutablePreview));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(NeurosurgeUpgradeRune), "ShouldSwapToHextechPower"));
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechNeurosurgePower), "AfterSideTurnStartForParticipants"),
            new NativeCallSite(NativeContextPower.MakeGenericMethod(typeof(DoomPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(DoomPower)), 1));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechNeurosurgePower), "ShouldApplyDoom"));
        BeforeSideTurnStartMirrors.Register<HextechNeurosurgePower>((_, _) => { });
        BeforeSideTurnEndMirrors.Registry.RegisterIgnored<HextechNeurosurgePower>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<HextechNeurosurgePower>((power, context) =>
            power.ModifyDamageMultiplicativeCompat(context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechPowerBase), "ModifyDamageMultiplicativeCompat"));
        CompatibilityGuard.Powers.Add(typeof(HextechNeurosurgePower));
        NativeAfterSidePowers.Add(typeof(HextechNeurosurgePower), (power, simulator, side, participants) =>
            InvokeNativePower((HextechNeurosurgePower)power, simulator, model => model.AfterSideTurnStartForParticipants(
                side, participants, simulator.State.CombatState)));

        var sideStart = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.TriggerSideTurnStart));
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteOrderedSidePowerDispatch));
        var enter = AccessTools.Method(typeof(NativeRuneBridge), nameof(EnterSidePowerPhase));
        var leave = AccessTools.Method(typeof(NativeRuneBridge), nameof(LeaveSidePowerPhase));
        harmony.Patch(sideStart, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave), transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.AddScope(sideStart, enter, leave, rewrite);
        foreach (var method in new[] {
            AccessTools.Method(typeof(PersistentPowerSupport), "TriggerOwnerAfterSideTurnStart"),
            AccessTools.Method(typeof(PersistentPowerSupport), "TriggerRampart"),
            AccessTools.Method(typeof(TurnStartPowerSupport), nameof(TurnStartPowerSupport.TriggerAfterSideTurnStart)) })
        {
            var filter = AccessTools.Method(typeof(NativeRuneBridge), nameof(FilterIndividualSidePower));
            harmony.Patch(method, transpiler: new HarmonyMethod(filter));
            NativeCallbackContracts.Add(method, filter);
        }
    }

    private static IEnumerable<CodeInstruction> RewriteOrderedSidePowerDispatch(IEnumerable<CodeInstruction> instructions)
    {
        int persistent = 0, additional = 0;
        var originalPersistent = AccessTools.Method(typeof(PersistentPowerSupport), nameof(PersistentPowerSupport.TriggerAfterSideTurnStart));
        var originalAdditional = AccessTools.Method(typeof(TurnStartPowerSupport), nameof(TurnStartPowerSupport.TriggerAfterSideTurnStart));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(originalPersistent))
            {
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(TriggerOrderedSidePowers));
                persistent++;
            }
            else if (instruction.Calls(originalAdditional))
            {
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(TriggerRemainingSidePowers));
                additional++;
            }
            yield return instruction;
        }
        if (persistent != 1 || additional != 1) throw new InvalidOperationException("Pinned side power dispatch changed.");
    }

    private static bool UsesOrderedSidePowers(SimulatedCombatState combat)
        => combat.EffectivePowers().Any(power => power.Amount > 0
            && (NativeAfterSidePowers.ContainsKey(power.GetType())
                || power is PoisonPower && HasRegisteredPoisonOrdering(combat, power.Owner)));

    private static bool TriggerOrderedSidePowers(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        CombatSide side, IReadOnlyList<Creature> participants, bool isExtraTurn)
    {
        if (!UsesOrderedSidePowers(combat))
            return PersistentPowerSupport.TriggerAfterSideTurnStart(simulator, combat, side, participants, isExtraTurn);
        var phase = _sidePowerPhase;
        if (phase is null || !ReferenceEquals(phase.Simulator, simulator))
            throw new InvalidOperationException("Native side power phase was not scoped.");
        phase.Handled = true;
        // Reuse the existing vanilla command implementations, selecting one
        // captured power at a time. Their alphabetical grouping cannot insert a
        // third-party power at its native acquisition position.
        foreach (var power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0) continue;
            var previous = _sidePowerScope;
            _sidePowerScope = new(combat, power);
            try
            {
                if (NativeAfterSidePowers.TryGetValue(power.GetType(), out var native))
                    native(power, simulator, side, participants);
                else if (power is PoisonPower && HasRegisteredPoisonOrdering(combat, power.Owner)
                    && side == power.Owner.Side && participants.Contains(power.Owner))
                {
                    if (!CorePowerSupport.TriggerPoison(simulator, combat, [power.Owner])) return false;
                }
                else if (power is CountdownPower or SandpitPower)
                {
                    if (!TurnStartPowerSupport.TriggerAfterSideTurnStart(simulator, combat, side, participants)) return false;
                }
                else if (power is RampartPower)
                {
                    if (side == CombatSide.Player && !isExtraTurn
                        && !PersistentPowerSupport.TriggerRampart(simulator, combat)) return false;
                }
                else if (participants.Contains(power.Owner)
                    && !PersistentPowerSupport.TriggerOwnerAfterSideTurnStart(simulator, combat, power.Owner)) return false;
                PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
                if (simulator.HasPendingChoice)
                {
                    simulator.RejectExecutionContinuation();
                    return false;
                }
            }
            finally { _sidePowerScope = previous; }
        }
        return !simulator.HasPendingChoice;
    }

    private static bool TriggerRemainingSidePowers(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        CombatSide side, IReadOnlyList<Creature> participants)
        => _sidePowerPhase is { Handled: true } phase && ReferenceEquals(phase.Simulator, simulator) ? !simulator.HasPendingChoice
            : TurnStartPowerSupport.TriggerAfterSideTurnStart(simulator, combat, side, participants);

    private static void EnterSidePowerPhase(CombatPredictionSimulator simulator, out SidePowerPhaseParent __state)
    {
        __state = new(_sidePowerPhase, _sidePowerScope);
        _sidePowerPhase = new(simulator);
        _sidePowerScope = null;
    }
    private static void LeaveSidePowerPhase(SidePowerPhaseParent __state)
    {
        _sidePowerPhase = __state.Phase;
        _sidePowerScope = __state.Power;
    }

    private static IEnumerable<CodeInstruction> FilterIndividualSidePower(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        // Reflect's existing reviewed expiry rewrite shares this exact stock
        // method. Compose both rewrites under one audited transpiler rather
        // than allowing arbitrary additional patch compositions.
        if (__originalMethod.Name == "TriggerOwnerAfterSideTurnStart")
            instructions = PowerExpiryBridge.RewriteReflectTick(instructions);
        int amounts = 0, powers = 0, lists = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(SimulatedCombatState))
            {
                if (method.IsGenericMethod && method.Name == nameof(SimulatedCombatState.GetAmount))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(IndividualSidePowerAmount)).MakeGenericMethod(method.GetGenericArguments());
                    amounts++;
                }
                else if (method.IsGenericMethod && method.Name == nameof(SimulatedCombatState.GetPower))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(IndividualSidePower)).MakeGenericMethod(method.GetGenericArguments());
                    powers++;
                }
                else if (method.Name == nameof(SimulatedCombatState.EffectivePowers))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(IndividualSidePowerList));
                    lists++;
                }
            }
            yield return instruction;
        }
        var expected = __originalMethod.Name switch {
            "TriggerOwnerAfterSideTurnStart" => (11, 1, 0), "TriggerRampart" => (0, 0, 1),
            "TriggerAfterSideTurnStart" => (0, 0, 2), _ => throw new InvalidOperationException("Unknown side power filter target.") };
        if ((amounts, powers, lists) != expected) throw new InvalidOperationException($"Pinned side power queries changed: {__originalMethod}.");
    }

    private static int IndividualSidePowerAmount<T>(SimulatedCombatState combat, Creature owner) where T : PowerModel
        => _sidePowerScope is { } scope && ReferenceEquals(scope.Combat, combat)
            ? scope.Power is T && ReferenceEquals(scope.Power.Owner, owner) ? scope.Power.Amount : 0
            : combat.GetAmount<T>(owner);
    private static T? IndividualSidePower<T>(SimulatedCombatState combat, Creature owner) where T : PowerModel
        => _sidePowerScope is { } scope && ReferenceEquals(scope.Combat, combat)
            ? scope.Power is T power && ReferenceEquals(power.Owner, owner) ? power : null : combat.GetPower<T>(owner);
    private static IReadOnlyList<PowerModel> IndividualSidePowerList(SimulatedCombatState combat)
        => _sidePowerScope is { } scope && ReferenceEquals(scope.Combat, combat) ? [scope.Power] : combat.EffectivePowers();
}
