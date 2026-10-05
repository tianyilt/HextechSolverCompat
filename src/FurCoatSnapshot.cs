using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace HextechSolverCompat;

// FurCoat's native clone only copies DynamicVars, leaving its map-coordinate
// arrays shared with the live relic. New-enemy HP must use the frozen root.
internal sealed class FurCoatSnapshot(FurCoat source) : IPredictionStateForkable
{
    private readonly int _act = source.FurCoatActIndex;
    private readonly bool _set = source.FurCoatCoordsSet;
    private readonly int[] _cols = source.FurCoatCoordCols?.ToArray() ?? [];
    private readonly int[] _rows = source.FurCoatCoordRows?.ToArray() ?? [];

    public object Fork(PredictionForkContext context) => MemberwiseClone(); // Arrays are private and immutable.

    internal static void Write(FurCoatSnapshot state, ref ModelPredictionStateWriter writer)
    {
        writer.Add("furAct", state._act);
        writer.Add("furSet", state._set);
        writer.Add("furCols", state._cols.Length);
        for (int i = 0; i < state._cols.Length; i++) writer.Add($"furCol:{i}", state._cols[i]);
        writer.Add("furRows", state._rows.Length);
        for (int i = 0; i < state._rows.Length; i++) writer.Add($"furRow:{i}", state._rows[i]);
    }

    internal static void Register(Harmony harmony)
    {
        ModelPredictionStateMirrors.RegisterRelic<FurCoat, FurCoatSnapshot>("fur-map-root-v1",
            (_, live) => new(live),
            (FurCoat live, ref ModelPredictionStateWriter writer) => Write(new(live), ref writer), Write);
        harmony.Patch(AccessTools.Method(typeof(PredictionUtils), nameof(PredictionUtils.CreateRelic)),
            postfix: new HarmonyMethod(typeof(FurCoatSnapshot), nameof(DetachCoordinates)));
    }

    private static void DetachCoordinates(RelicModel relic, RelicModel __result)
    {
        if (relic is not FurCoat source || __result is not FurCoat clone) return;
        clone.FurCoatCoordCols = source.FurCoatCoordCols?.ToArray()!;
        clone.FurCoatCoordRows = source.FurCoatCoordRows?.ToArray()!;
    }
}
