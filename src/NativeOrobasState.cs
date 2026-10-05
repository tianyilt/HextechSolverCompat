using System.Globalization;
using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using HextechRunes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal sealed class NativeOrobasState(OrobasPlusRelicBase model) : IPredictionStateForkable
{
    internal readonly OrobasPlusRelicBase Model = model;
    internal static OrobasPlusRelicBase Clone(OrobasPlusRelicBase source)
    {
        var copy = PredictionUtils.CloneModelForSimulation(source);
        for (Type? type = source.GetType(); type is not null && typeof(AbstractModel).IsAssignableFrom(type); type = type.BaseType)
            foreach (var observer in type.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                AccessTools.DeclaredField(type, observer.Name)?.SetValue(copy, null);
        return copy;
    }
    public object Fork(PredictionForkContext context) => new NativeOrobasState(Clone(Model));
    internal static void WriteModel<T>(T model, ref ModelPredictionStateWriter writer) where T : OrobasPlusRelicBase
    {
        // The reviewed five concrete types and their base declare no mutable
        // gameplay fields. Freeze the actual dynamic values used by callbacks.
        foreach (var variable in model.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Add("orobasVar:" + variable.Key, variable.Value.BaseValue.ToString(CultureInfo.InvariantCulture));
    }
    internal static void Write(NativeOrobasState state, ref ModelPredictionStateWriter writer) => WriteModel(state.Model, ref writer);
}
