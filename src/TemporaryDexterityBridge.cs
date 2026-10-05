using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static class TemporaryDexterityBridge
{
    internal static void Register(Harmony harmony)
    {
        // Reviewed Solver 0.48.1 retires temporary Strength/Dexterity/Focus in
        // each power's native listener position. Installing the earlier repair
        // would move retirement out of that order or apply it twice.
        if (typeof(SimulatedCombatState).Assembly.GetName().Version == new Version(0, 48, 1, 0))
        {
            if (AccessTools.Method(typeof(SimulatedCombatState), "RetireTemporaryStat") is null)
                throw new InvalidOperationException("Reviewed temporary-stat retirement contract is missing.");
            return;
        }
        harmony.Patch(AccessTools.Method(typeof(CorePowerSupport), "TriggerPlayerRegularSideTurnEndEffects"),
            transpiler: new HarmonyMethod(typeof(TemporaryDexterityBridge), nameof(RewritePlayerExpiry)));
        harmony.Patch(AccessTools.Method(typeof(CorePowerSupport), "TriggerEnemySideTurnEndEffects"),
            transpiler: new HarmonyMethod(typeof(TemporaryDexterityBridge), nameof(RewriteEnemyExpiry)));
    }

    private static IEnumerable<CodeInstruction> RewritePlayerExpiry(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var old = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.RestoreTemporaryDexterity));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(old))
            {
                // The existing combat receiver is already on the evaluation stack.
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(TemporaryDexterityBridge), nameof(Restore));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Temporary Dexterity player expiry changed: {count}.");
    }

    private static IEnumerable<CodeInstruction> RewriteEnemyExpiry(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var old = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.RestoreTemporaryStrength));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(old))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(TemporaryDexterityBridge), nameof(RestoreEnemyStats));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Temporary Dexterity enemy expiry changed: {count}.");
    }

    private static void RestoreEnemyStats(SimulatedCombatState combat, IEnumerable<Creature> participants)
    {
        combat.RestoreTemporaryStrength(participants);
        Restore(combat, participants);
    }

    private static void Restore(SimulatedCombatState combat, IEnumerable<Creature> participants)
    {
        var owners = participants.ToHashSet();
        // Native AfterSideTurnEnd removes each wrapper before applying -Sign*Amount
        // to its owner. Keep commands separate: Artifact and power reactions observe
        // their order. Enemy wrappers survive the player's end-of-turn boundary.
        foreach (TemporaryDexterityPower power in combat.EffectivePowers()
            .OfType<TemporaryDexterityPower>().Where(power => owners.Contains(power.Owner)).ToArray())
        {
            int amount = power.Amount;
            int sign = power.Sign;
            combat.SetPowerAmount(power, 0);
            combat.Apply<DexterityPower>(power.Owner, -sign * amount, power.Owner);
        }
    }
}
