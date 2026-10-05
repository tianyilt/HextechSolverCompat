using System.Collections;
using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;

namespace HextechSolverCompat;

internal sealed partial class NativeRuneState
{
    private static readonly FieldInfo[] NativeDamagePendingFields =
    [AccessTools.DeclaredField(typeof(CompensationRune), "_pendingCompensations"),
        AccessTools.DeclaredField(typeof(PiercingThreadRune), "_pendingDamage")];
    private static bool IsNativeDamagePending(FieldInfo field) => NativeDamagePendingFields.Contains(field);
    private static void AssertNativeDamageQueuesEmpty(HextechRelicBase source)
    {
        foreach (var field in NativeDamagePendingFields.Where(field => field.DeclaringType!.IsInstanceOfType(source)))
            if (((IList)field.GetValue(source)!).Count != 0)
                throw new PredictionUnsupportedException("Native damage queues must finish at the enclosing command boundary before capture/fork/hash.");
    }
    private static void CloneNativeDamageQueues(HextechRelicBase source, HextechRelicBase clone)
    {
        AssertNativeDamageQueuesEmpty(source);
        foreach (var field in NativeDamagePendingFields.Where(field => field.DeclaringType!.IsInstanceOfType(source)))
            field.SetValue(clone, Activator.CreateInstance(field.FieldType));
    }
}
