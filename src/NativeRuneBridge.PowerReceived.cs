using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record ReceivedPower(PowerModel Incoming, HextechRelicBase[] Modifiers);
    private static readonly ConditionalWeakTable<SimulatedCombatState, CombatPredictionSimulator> CombatSimulators = new();
    private static readonly ConditionalWeakTable<SimulatedCombatState, List<ReceivedPower>> PendingReceivedPowers = new();
    private delegate (bool Changed, decimal Amount) NativeReceivedQuery(HextechRelicBase rune, CombatPredictionSimulator simulator,
        PowerModel power, Creature target, decimal amount, Creature? applier);
    private static readonly Dictionary<Type, NativeReceivedQuery> NativeReceivedQueries = [];
    private static readonly Dictionary<Type, Action<HextechRelicBase, CombatPredictionSimulator, PowerModel>> NativeReceivedCallbacks = [];

    private static void RegisterNativePowerReceived(Harmony harmony)
    {
        RegisterNativeReceivedRune<ReforgedHelmetRune>();
        RegisterNativeReceivedRune<DexterityToStrengthRune>();
        RegisterNativeReceivedRune<StrengthToDexterityRune>();
        RegisterNativeReceivedRune<DexterityStrengthToFocusRune>();
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 5
            && method.GetParameters()[0].ParameterType == typeof(Creature));
        var bridge = AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne));
        foreach (var (type, result, sources) in new[] {
            (typeof(DexterityToStrengthRune), typeof(StrengthPower), new[] { typeof(DexterityPower) }),
            (typeof(StrengthToDexterityRune), typeof(DexterityPower), new[] { typeof(StrengthPower) }),
            (typeof(DexterityStrengthToFocusRune), typeof(FocusPower), new[] { typeof(DexterityPower), typeof(StrengthPower) }) })
        {
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "ApplyConvertedPower"),
                new NativeCallSite(apply.MakeGenericMethod(result), bridge.MakeGenericMethod(result), 1));
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "RevertOriginalPower"),
                sources.Select(source => new NativeCallSite(apply.MakeGenericMethod(source), bridge.MakeGenericMethod(source), 1)).ToArray());
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "ShouldConvert"));
        }
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(AttributeConversionRelicBase), "AfterPowerAmountChanged"));
        var capture = AccessTools.Method(typeof(NativeRuneBridge), nameof(RegisterCombatSimulator));
        var constructors = typeof(CombatPredictionSimulator).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (constructors.Length != 2) throw new InvalidOperationException("Pinned simulator constructor shape changed.");
        foreach (var constructor in constructors)
        {
            harmony.Patch(constructor, postfix: new HarmonyMethod(capture));
            NativeCallbackContracts.AddNativePostfix(constructor, capture, "HextechSolverCompat", Priority.Normal);
        }
        var target = AccessTools.Method(typeof(SimulatedCombatState), "ModifyPowerAmountForRuinedHelmet");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteReceivedPowerStage));
        harmony.Patch(target, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(target, rewrite);
    }

    private static void RegisterNativeReceivedRune<T>(bool queryRewritten = false) where T : HextechRelicBase
    {
        RegisterState<T>(); RuneMirrors.RegisterNativeBase<T>();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(T), nameof(HextechRelicBase.TryModifyPowerAmountReceived)),
            queryRewritten ? AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)) : null);
        NativeCallbackContracts.Add(AccessTools.Method(typeof(T), nameof(HextechRelicBase.AfterModifyingPowerAmountReceived)));
        NativeReceivedQueries.Add(typeof(T), (rune, simulator, power, target, amount, applier) =>
        {
            decimal modified = amount;
            bool changed = Invoke((T)rune, simulator, model => model.TryModifyPowerAmountReceived(power, target, amount, applier, out modified));
            return (changed, modified);
        });
        NativeReceivedCallbacks.Add(typeof(T), (rune, simulator, power) => RequireCompleted(
            Invoke((T)rune, simulator, model => model.AfterModifyingPowerAmountReceived(power)), typeof(T)));
    }

    private static void RegisterCombatSimulator(CombatPredictionSimulator __instance)
    {
        if (__instance.State.CombatState is SimulatedCombatState combat) CombatSimulators.Add(combat, __instance);
    }

    internal static PredictedCard? CapturePowerSource(SimulatedCombatState combat, CardModel? source)
    {
        if (source is null) return null;
        if (!CombatSimulators.TryGetValue(combat, out var simulator))
            throw new PredictionUnsupportedException("Power source capture has no attached simulator.");
        return simulator.State.FindCard(source)
            ?? throw new PredictionUnsupportedException("Power source references a card outside the captured branch.");
    }

    private static void InvokeNativeAttributeChange(AttributeConversionRelicBase rune,
        CombatPredictionSimulator simulator, SimulatedPowerAmountChange change, bool? sourceKnown, PredictedCard? source)
    {
        if (sourceKnown is null)
            throw new PredictionUnsupportedException("Native attribute conversion cannot infer an uncaptured power source.");
        RequireCompleted(Invoke(rune, simulator, model => model.AfterPowerAmountChanged(
            new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, source?.MutablePreview)), rune.GetType());
    }

    private static IEnumerable<CodeInstruction> RewriteReceivedPowerStage(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var body = instructions.ToList();
        var original = generator.DefineLabel();
        body[0].labels.Add(original);
        var result = generator.DeclareLocal(typeof(int));
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Ldarg_2);
        yield return new CodeInstruction(OpCodes.Ldarg_3);
        yield return new CodeInstruction(OpCodes.Ldloca, result);
        yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(TryNativeReceivedPowerStage));
        yield return new CodeInstruction(OpCodes.Brfalse, original);
        yield return new CodeInstruction(OpCodes.Ldloc, result);
        yield return new CodeInstruction(OpCodes.Ret);
        foreach (var instruction in body) yield return instruction;
    }

    private static bool TryNativeReceivedPowerStage(SimulatedCombatState combat, PowerModel power, Creature target,
        int amount, out int result)
    {
        result = amount;
        if (!combat.Players.Any(player => combat.RelicsOf(player).Any(relic => NativeReceivedQueries.ContainsKey(relic.GetType())))) return false;
        // Artifact is the native power listener preceding the relic listeners.
        // Keep the stock command's one Artifact consumption and prevent relics
        // from converting a request that it already nullifies.
        bool artifactGuardsIncoming = power.IsVisible && power.GetTypeForAmount(amount) == PowerType.Debuff
            || amount > 0 && power is HextechTemporaryStrengthLossPower or HextechTemporaryDexterityLossPower;
        if (artifactGuardsIncoming && combat.GetAmount<ArtifactPower>(target) > 0) return false;
        if (!CombatSimulators.TryGetValue(combat, out var simulator))
            throw new PredictionUnsupportedException("Native received-power stage has no attached simulator.");
        decimal modified = amount;
        List<HextechRelicBase> modifiers = [];
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is RuinedHelmet helmet && ReferenceEquals(helmet.Owner.Creature, target)
                && power is StrengthPower && modified > 0 && combat.GetStatefulRelicState(helmet).Current == 0)
            {
                modified *= 2;
                combat.SetStatefulRelicState(helmet, new SimulatedCombatState.StatefulRelicState(1, 0));
            }
            else if (listener is HextechRelicBase rune && NativeReceivedQueries.TryGetValue(rune.GetType(), out var query))
            {
                var next = query(rune, simulator, power, target, modified, power.Applier);
                if (!next.Changed) continue;
                modified = next.Amount;
                modifiers.Add(rune);
            }
        }
        if (modified != decimal.Truncate(modified) || modified < int.MinValue || modified > int.MaxValue)
            throw new PredictionUnsupportedException("Native received-power modifier returned a non-integral amount.");
        result = (int)modified;
        if (modifiers.Count > 0) PendingReceivedPowers.GetOrCreateValue(combat).Add(new(power, modifiers.ToArray()));
        return true;
    }

    internal static void AfterNativePowerReceived(SimulatedCombatState combat, PowerModel power)
    {
        if (!PendingReceivedPowers.TryGetValue(combat, out var pending) || pending.Count == 0) return;
        int index = pending.FindLastIndex(frame => frame.Incoming.GetType() == power.GetType() && ReferenceEquals(frame.Incoming.Owner, power.Owner));
        if (index < 0) return;
        var frame = pending[index];
        pending.RemoveAt(index);
        if (!CombatSimulators.TryGetValue(combat, out var simulator)) throw new InvalidOperationException("Received callback lost simulator.");
        foreach (var rune in frame.Modifiers)
        {
            NativeReceivedCallbacks[rune.GetType()](rune, simulator, power);
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); break; }
        }
    }
}
