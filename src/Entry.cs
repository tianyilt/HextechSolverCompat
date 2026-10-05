using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;

namespace HextechSolverCompat;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    internal static bool Ready;
    internal static string Failure = "适配器初始化尚未完成";
    private static readonly System.Reflection.FieldInfo HistoryTrace =
        AccessTools.GetDeclaredFields(typeof(CombatPredictionHistory)).Single(f => f.FieldType == typeof(PredictionTrace));

    public static void Initialize()
    {
        var harmony = new Harmony("HextechSolverCompat");
        // Install the boundary before registration: a failed/partial initialization
        // must never leave an apparently supported solver behind.
        harmony.Patch(AccessTools.Method(typeof(ContinuationStamp), nameof(ContinuationStamp.CaptureLive)),
            prefix: new HarmonyMethod(typeof(CompatibilityGuard), nameof(CompatibilityGuard.CaptureLiveBoundary)));
        harmony.Patch(AccessTools.Method(typeof(CombatRootSnapshot), nameof(CombatRootSnapshot.Capture)),
            prefix: new HarmonyMethod(typeof(CompatibilityGuard), nameof(CompatibilityGuard.Validate)));
        harmony.Patch(AccessTools.Method(typeof(PlayerTurnSetupCoordinator), nameof(PlayerTurnSetupCoordinator.TryInterceptSetup)),
            prefix: new HarmonyMethod(typeof(CompatibilityGuard), nameof(CompatibilityGuard.AllowNativeTurnSetup)));
        harmony.Patch(AccessTools.Method(typeof(CombatPredictionHistory), nameof(CombatPredictionHistory.RecordRisk)),
            prefix: new HarmonyMethod(typeof(Entry), nameof(RejectMissingSemantics)));
        harmony.Patch(AccessTools.Method(typeof(CombatSolverLog), nameof(CombatSolverLog.Error)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(ReportSearchFailure)));
        try
        {
            CompatibilityGuard.CheckVersions();
            ModSourceSettingsPatch.Install(harmony);
            HandSizeContinuationPatch.Install(harmony);
            ModifierMirrors.Register(harmony);
            RuneMirrors.Register(harmony);
            NativeRuneBridge.Register(harmony);
            ConditionalCardPatches.Register(harmony);
            Ready = true;
            Log.Info("[HextechSolverCompat] Ready: experimental single-player adapters; unsupported effects stop prediction.");
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Log.Error($"[HextechSolverCompat] Registration failed; prediction remains blocked: {error}");
        }
    }

    private static void ReportSearchFailure(string message)
    {
        // The solver journal buffers small logs. Preserve the complete inner
        // exception in the game's local log even when no report is uploaded.
        if (message.StartsWith("[CombatSolver/Test] SEARCH_FAILURE ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] SEARCH_SETUP_FAILURE ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] DEPLOY_FAILURE ", StringComparison.Ordinal))
        {
            Log.Error($"[HextechSolverCompat] {message}");
            foreach (var composition in AdaptedCardOnPlayMirrors.DescribeRegisteredCompositions())
            {
                var machine = composition.Target.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)
                    .Cast<System.Runtime.CompilerServices.AsyncStateMachineAttribute>().SingleOrDefault()?.StateMachineType;
                if (machine is null) continue;
                var patches = Harmony.GetPatchInfo(AccessTools.Method(machine, "MoveNext"));
                if (patches is not { Owners.Count: > 0 }) continue;
                Log.Warn($"[HextechSolverCompat] Patched OnPlay machine {composition.Target.DeclaringType?.FullName}: "
                    + string.Join(';', AdaptedCardOnPlayMirrors.Groups(patches).SelectMany(group => group.Patches)
                        .Select(patch => $"{patch.owner}:{patch.PatchMethod.DeclaringType?.FullName}.{patch.PatchMethod.Name}")));
            }
        }
    }

    private static void RejectMissingSemantics(CombatPredictionHistory __instance, PredictionRiskReason reason)
    {
        if (reason is not (PredictionRiskReason.MethodNotMirrored or PredictionRiskReason.MethodMirrorIncomplete)) return;
        var frame = ((PredictionTrace)HistoryTrace.GetValue(__instance)!).Current;
        // Vanilla inferred mirrors are part of the solver's existing behavior.
        // Only unreviewed Hextech semantics are a hard error in this adapter.
        if (frame?.Source.GetType().Assembly == typeof(HextechRunes.ModEntry).Assembly)
            throw new PredictionUnsupportedException($"海克斯兼容层遇到未实现的战斗语义：{frame.Source.GetType().Name} / {reason}。停止本次求解。");
    }
}
