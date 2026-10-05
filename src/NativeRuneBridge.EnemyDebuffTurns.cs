using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeEnemyDebuffTurns(Harmony harmony)
    {
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
            && method.IsGenericMethodDefinition && method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(IEnumerable<Creature>), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool) }));
        NativeCallSite Apply<T>() where T : PowerModel => new(apply.MakeGenericMethod(typeof(T)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerMany)).MakeGenericMethod(typeof(T)), 1);
        foreach (var type in new[] { typeof(OmegaEnemyHex), typeof(LagavulinMatriarchEnemyHex), typeof(FrostWraithEnemyHex), typeof(DoomsdayEnemyHex) })
        {
            string callback = type == typeof(OmegaEnemyHex) || type == typeof(LagavulinMatriarchEnemyHex)
                ? "BeforePlayerSideTurnStart" : "BeforeEnemySideTurnStart";
            var sites = new List<NativeCallSite>();
            if (type == typeof(OmegaEnemyHex)) sites.Add(Site(tracking, nameof(NativeCapturedTracking)));
            sites.AddRange(type == typeof(LagavulinMatriarchEnemyHex) ? new[] { Apply<StrengthPower>(), Apply<DexterityPower>() }
                : type == typeof(FrostWraithEnemyHex) ? new[] { Apply<HextechTemporarySlowPower>() } : new[] { Apply<DisintegrationPower>() });
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, callback), sites.ToArray());
        }
        NativeCallbackContracts.Add(AccessTools.Method(typeof(FrostWraithEnemyHex), "ShouldTriggerForRound"));
    }
}
