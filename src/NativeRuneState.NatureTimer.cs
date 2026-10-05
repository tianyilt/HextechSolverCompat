using System.Reflection;
using CombatSolver.Engine.Common;
using HextechRunes;

namespace HextechSolverCompat;

internal sealed partial class NativeRuneState
{
    private static readonly FieldInfo PlayerNatureTimer = typeof(NatureIsHealingRune).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PlayerNatureHealing = typeof(NatureIsHealingRune).GetField("_healing", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static bool IsReviewedNatureTimer(FieldInfo field)
        => field == PlayerNatureTimer && field.FieldType == typeof(Godot.Timer);
    private static void DetachNativePlayerNatureTimer(HextechRelicBase source, HextechRelicBase clone)
    {
        if (source is not NatureIsHealingRune) return;
        if ((bool)PlayerNatureHealing.GetValue(source)!)
            throw new PredictionUnsupportedException("Native player Nature heal is still resolving; wait for a fresh root.");
        PlayerNatureTimer.SetValue(clone, null);
    }
}
