using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static class OpeningFormBoundary
{
    private static readonly Dictionary<Type, (FieldInfo Started, FieldInfo Playing, MethodInfo Available)> Contracts = [];

    internal static void Register<T>() where T : HextechRelicBase
    {
        var type = typeof(T);
        var started = AccessTools.Field(type, "_startedThisCombat");
        var playing = AccessTools.Field(type, "_autoPlaying");
        if (started?.FieldType != typeof(bool) || playing?.FieldType != typeof(bool))
            throw new PredictionUnsupportedException($"Opening form field contract changed: {type.Name}.");
        var available = AccessTools.DeclaredMethod(type, "IsAvailableForCharacter");
        if (available?.ReturnType != typeof(bool))
            throw new PredictionUnsupportedException($"Opening form character contract changed: {type.Name}.");
        Contracts.Add(type, (started, playing, available));
    }

    internal static void Validate(CombatState combat)
    {
        foreach (var rune in combat.Players.SelectMany(player => player.Relics))
            if (Contracts.TryGetValue(rune.GetType(), out var fields)
                && rune.Owner is { } owner && (bool)fields.Available.Invoke(rune, [owner])!
                && (!(bool)fields.Started.GetValue(rune)! || (bool)fields.Playing.GetValue(rune)!))
                // First-turn setup must finish its original native batch before
                // a root exists. AllowNativeTurnSetup already falls back to that
                // native setup on a rejected root; this never drops the batch.
                throw new PredictionUnsupportedException($"海克斯开局形态仍在原生结算：{rune.Id.Entry}。结算完成后可求解。");
    }
}
