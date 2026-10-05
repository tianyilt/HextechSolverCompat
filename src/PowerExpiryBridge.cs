using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static class PowerExpiryBridge
{
    private static readonly Dictionary<MethodInfo, MethodInfo> Contracts = [];

    internal static void AddContract(MethodInfo target, MethodInfo prefix) => Contracts.Add(target, prefix);

    internal static void Register(Harmony harmony)
    {
        Contracts.Add(AccessTools.DeclaredMethod(typeof(RagePower), "AfterSideTurnEnd"),
            AccessTools.Method(AccessTools.Inner(typeof(RageUpgradeRune), "PersistentRagePatch"), "Prefix"));
        Contracts.Add(AccessTools.DeclaredMethod(typeof(CorrosiveWavePower), "AfterSideTurnEnd"),
            AccessTools.Method(AccessTools.Inner(typeof(CorrosiveWaveUpgradeRune), "CorrosiveWavePatch"), "Prefix"));
        Contracts.Add(AccessTools.DeclaredMethod(typeof(OblivionPower), "AfterSideTurnEnd"),
            AccessTools.Method(AccessTools.Inner(typeof(OblivionUpgradeRune), "OblivionPatch"), "Prefix"));
        Contracts.Add(AccessTools.DeclaredMethod(typeof(ReflectPower), "AfterSideTurnStart"),
            AccessTools.Method(AccessTools.Inner(typeof(ReflectUpgradeRune), "PersistentReflectPatch"), "Prefix"));
        ValidateContracts();
        harmony.Patch(AccessTools.Method(typeof(CorePowerSupport), "TriggerTransientSideTurnEndPowers"),
            transpiler: new HarmonyMethod(typeof(PowerExpiryBridge), nameof(RewriteRageRemoval)));
        harmony.Patch(AccessTools.Method(typeof(EndTurnPowerSupport), nameof(EndTurnPowerSupport.TriggerRegular)),
            transpiler: new HarmonyMethod(typeof(PowerExpiryBridge), nameof(RewriteWaveRemoval)));
        harmony.Patch(AccessTools.Method(typeof(EndTurnPowerSupport), "TriggerBatch048"),
            transpiler: new HarmonyMethod(typeof(PowerExpiryBridge), nameof(RewriteOblivionRemoval)));
        harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "CaptureLiveStamp"),
            postfix: new HarmonyMethod(typeof(PowerExpiryBridge), nameof(AppendComposition)));
    }

    internal static void ValidateContracts()
    {
        foreach (var (target, prefix) in Contracts)
        {
            var patches = Harmony.GetPatchInfo(target);
            if (patches is null || patches.Prefixes.Count != 1
                || patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0
                || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0
                || patches.Prefixes[0].PatchMethod != prefix || patches.Prefixes[0].owner != "Natsuki.HextechRunes"
                || patches.Prefixes[0].priority != Priority.Low || patches.Prefixes[0].before.Length != 0
                || patches.Prefixes[0].after.Length != 0)
                throw new PredictionUnsupportedException($"Unreviewed power-expiry patch composition: {target.DeclaringType?.Name}.");
        }
    }

    private static void AppendComposition(ref string? __result)
    {
        if (__result is null) return;
        string signature = string.Join(';', Contracts.Keys.OrderBy(method => method.DeclaringType!.FullName, StringComparer.Ordinal)
            .Select(method => AdaptedCardOnPlayMirrors.DescribeActual(method, Harmony.GetPatchInfo(method), includeIndex: true)));
        __result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(__result + ":power-expiry-v1:" + signature)));
    }

    private static IEnumerable<CodeInstruction> RewriteRageRemoval(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(CorePowerSupport)
                && method.Name == "Remove" && method.IsGenericMethod
                && method.GetGenericArguments().SequenceEqual(new[] { typeof(RagePower) }))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PowerExpiryBridge), nameof(RemoveRage));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Rage expiry call contract changed: {count}.");
    }

    private static IEnumerable<CodeInstruction> RewriteWaveRemoval(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in NativeRuneBridge.RewriteNativeEndTurnPowers(instructions))
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(SimulatedCombatState)
                && method.Name == "SetAmount" && method.IsGenericMethod
                && method.GetGenericArguments().SequenceEqual(new[] { typeof(CorrosiveWavePower) }))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PowerExpiryBridge), nameof(RemoveWave));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"CorrosiveWave expiry call contract changed: {count}.");
    }

    private static bool HasUpgrade<T>(SimulatedCombatState combat, Creature creature) where T : RelicModel
        => creature.Player is { } player && combat.RelicsOf(player).OfType<T>().Any();

    private static void RemoveRage(CombatPredictionSimulator simulator, SimulatedCombatState combat, Creature creature)
    {
        if (!HasUpgrade<RageUpgradeRune>(combat, creature))
            CorePowerSupport.Remove<RagePower>(simulator, combat, creature);
    }

    private static void RemoveWave(SimulatedCombatState combat, Creature creature, int amount)
    {
        if (amount != 0 || !HasUpgrade<CorrosiveWaveUpgradeRune>(combat, creature))
            combat.SetAmount<CorrosiveWavePower>(creature, amount);
    }

    internal static IEnumerable<CodeInstruction> RewriteReflectTick(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(SimulatedCombatState)
                && method.Name == "SetAmount" && method.IsGenericMethod
                && method.GetGenericArguments().SequenceEqual(new[] { typeof(ReflectPower) }))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PowerExpiryBridge), nameof(TickReflect));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reflect tick call contract changed: {count}.");
    }

    private static IEnumerable<CodeInstruction> RewriteOblivionRemoval(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var native = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.SetPowerAmount));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PowerExpiryBridge), nameof(RemoveOblivionOrStock));
                count++;
            }
            yield return instruction;
        }
        if (count != 7) throw new InvalidOperationException($"Reviewed batch expiry command count changed: {count}.");
    }

    private static void TickReflect(SimulatedCombatState combat, Creature creature, int amount)
    {
        if (!HasUpgrade<ReflectUpgradeRune>(combat, creature))
            combat.SetAmount<ReflectPower>(creature, amount);
    }

    private static void RemoveOblivionOrStock(SimulatedCombatState combat, PowerModel power, int amount)
    {
        // Native persistence belongs to the first recorded power applier,
        // which can differ from its target and from a later stacking source.
        if (power is not OblivionPower || amount != 0 || power.Applier is not { } applier
            || !HasUpgrade<OblivionUpgradeRune>(combat, applier))
            combat.SetPowerAmount(power, amount);
    }
}
