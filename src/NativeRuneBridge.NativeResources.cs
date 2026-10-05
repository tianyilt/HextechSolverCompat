using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeResourceReactions(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TerminalIllnessRune), "TryModifyPowerAmountReceived"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)));
        RegisterNativeReceivedRune<TerminalIllnessRune>(queryRewritten: true);
        var target = AccessTools.Method(typeof(CorePowerSupport), "TriggerPoison");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativePoisonDecrement));
        harmony.Patch(target, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(target, rewrite);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TrinityRune), "AfterEnergySpent"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainStars)), nameof(GainNativeStars)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TrinityRune), "AfterStarsSpent"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(ForgeCmd), nameof(ForgeCmd.Forge),
                [typeof(decimal), typeof(Player), typeof(AbstractModel)]), nameof(ForgeNativeBlade)));
        RegisterState<TrinityRune>();
        RuneMirrors.RegisterNativeBase<TrinityRune>();
        var energy = AccessTools.Method(typeof(PowerLifecycleSupport), "AfterEnergySpent");
        var dispatch = AccessTools.Method(typeof(NativeRuneBridge), nameof(DispatchNativeEnergySpent));
        harmony.Patch(energy, prefix: new HarmonyMethod(dispatch));
        NativeCallbackContracts.AddNativePrefix(energy, dispatch, "HextechSolverCompat", Priority.Normal);
    }

    private static bool DispatchNativeEnergySpent(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PredictedCard card, int amount)
    {
        if (!combat.Players.SelectMany(combat.RelicsOf).Any(relic => relic is TrinityRune)) return true;
        if (amount <= 0) return false;
        var owner = card.Preview.Owner;
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is RelicModel { IsMelted: true } || listener is PowerModel { Amount: <= 0 }) continue;
            switch (listener)
            {
                case OrbitPower power when ReferenceEquals(power.Owner, owner.Creature):
                    int triggers = combat.AdvanceOrbitEnergy(power, amount);
                    if (triggers > 0) simulator.GainEnergy(owner, power.Amount * triggers);
                    break;
                case TrinityRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterEnergySpent(card.MutablePreview, amount)), typeof(TrinityRune));
                    break;
                default:
                    if (AccessTools.Method(listener.GetType(), nameof(AbstractModel.AfterEnergySpent)).DeclaringType != typeof(AbstractModel))
                        throw new PredictionUnsupportedException($"Unreviewed AfterEnergySpent callback: {listener.GetType().Name}.");
                    break;
            }
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); return false; }
        }
        return false;
    }

    private static IEnumerable<CodeInstruction> RewriteNativePoisonDecrement(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(SimulatedCombatState), "SetAmount").MakeGenericMethod(typeof(PoisonPower));
        int count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(native))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativePoisonDecrement));
                count++;
            }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException($"Pinned poison decrement changed: {count}.");
    }

    private static void NativePoisonDecrement(SimulatedCombatState combat, Creature target, int next)
    {
        if (!combat.Players.SelectMany(combat.RelicsOf).Any(relic => relic is TerminalIllnessRune))
        { combat.SetAmount<PoisonPower>(target, next); return; }
        if (!CombatSimulators.TryGetValue(combat, out var simulator))
            throw new PredictionUnsupportedException("Native poison decrement has no captured simulator.");
        var power = combat.GetPower<PoisonPower>(target)
            ?? throw new PredictionUnsupportedException("Native poison decrement lost the branch power.");
        decimal delta = next - combat.GetAmount<PoisonPower>(target);
        // The original PoisonPower.Trigger calls PowerCmd.Decrement, whose
        // receiver query passes a null applier, not the poison's old applier.
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is not TerminalIllnessRune rune || rune.IsMelted) continue;
            var result = NativeReceivedQueries[typeof(TerminalIllnessRune)](rune, simulator, power, target, delta, null);
            if (!result.Changed) continue;
            delta = result.Amount;
            NativeReceivedCallbacks[typeof(TerminalIllnessRune)](rune, simulator, power);
        }
        if (delta == 0m) return;
        if (delta != -1m) throw new PredictionUnsupportedException("Unreviewed native poison decrement amount.");
        combat.SetAmount<PoisonPower>(target, next);
    }
}
