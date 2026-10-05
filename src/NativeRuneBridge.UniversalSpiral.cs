using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Enchantments.OnPlay;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeUniversalSpiral(Harmony harmony)
    {
        CompatibilityGuard.Enchantments.Add(typeof(UniversalSpiral));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(UniversalSpiral), "EnchantPlayCount"));
        // UniversalSpiral inherits the stock no-op OnPlay. The SDK already
        // dispatches inherited base methods; registering an override here is
        // invalid because the original model does not declare one.
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(EnchantmentStateSupport)).Where(method => method.Name == "Append"))
        {
            var callback = AccessTools.DeclaredMethod(typeof(NativeRuneBridge),
                method.GetParameters()[0].ParameterType == typeof(StringBuilder) ? nameof(AppendNativeSpiralStamp) : nameof(AppendNativeSpiralKey));
            harmony.Patch(method, postfix: new HarmonyMethod(callback));
            NativeCallbackContracts.AddNativePostfix(method, callback, "HextechSolverCompat", Priority.Normal);
        }
    }

    private static void AppendNativeSpiralKey(ref StateFingerprintBuilder key, EnchantmentModel? enchantment)
    {
        if (enchantment is UniversalSpiral spiral) key.Add(spiral.DynamicVars["Times"].IntValue);
    }
    private static void AppendNativeSpiralStamp(StringBuilder text, EnchantmentModel? enchantment)
    {
        if (enchantment is UniversalSpiral spiral) text.Append(":hextechTimes=").Append(spiral.DynamicVars["Times"].IntValue);
    }
}
