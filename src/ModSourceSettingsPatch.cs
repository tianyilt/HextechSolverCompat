using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace HextechSolverCompat;

// 0.111.0 loads settings by (id, source), but its post-load rebuild compares
// only id. A disabled workshop duplicate can consequently disable the local
// version on the following launch. Preserve each source's existing choice.
internal static class ModSourceSettingsPatch
{
    private static FieldInfo CapturedMod = null!;
    internal static void Install(Harmony harmony)
    {
        var closure = AccessTools.Inner(typeof(ModManager), "<>c__DisplayClass29_0")
            ?? throw new InvalidOperationException("Reviewed ModManager settings closure is missing.");
        CapturedMod = AccessTools.Field(closure, "mod");
        var predicate = AccessTools.DeclaredMethod(closure, "<Initialize>b__1");
        if (CapturedMod?.FieldType != typeof(Mod) || predicate is null
            || predicate.ReturnType != typeof(bool)
            || !predicate.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(SettingsSaveMod)])
            || Harmony.GetPatchInfo(predicate) is { Owners.Count: > 0 })
            throw new InvalidOperationException("Unreviewed ModManager settings rebuild.");
        harmony.Patch(predicate, prefix: new HarmonyMethod(typeof(ModSourceSettingsPatch), nameof(MatchesSource)));
    }
    private static bool MatchesSource(object __instance, [HarmonyArgument(0)] SettingsSaveMod saved, ref bool __result)
    {
        var mod = (Mod)CapturedMod.GetValue(__instance)!;
        __result = saved.Id == mod.manifest?.id && saved.Source == mod.modSource;
        return false;
    }
}
