using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class ConditionalCardPatches
{
    internal const string ReviewedRitsuRuntimeHash = "1453444c546564026250faf0a0581174521244af550db7b5c37c4541512a3424";
    internal const string ReviewedRitsu065RuntimeHash = "383f86873509c76579ea3c6881ff0e021c25b82269b83a80cc918ee51b224594";
    internal const string ReviewedBaseLibHash = "55863ca6adc30a61a7544874e157a519ffe148ccf2b18025947609c4bff813ce";
    private static MethodInfo _handSizeTranspiler = null!;
    private static string _capturedRitsuRuntimeHash = null!;
    private static MethodInfo? _baseLibHandSizeTranspiler;
    private static readonly Dictionary<MethodInfo, MethodInfo> HandSizeMachines = [];

    private static void RegisterRitsuHandSizeContract(Harmony harmony)
    {
        var type = AccessTools.TypeByName("STS2RitsuLib.Combat.HandSize.MaxHandSizePatchInstaller")
            ?? throw new PredictionUnsupportedException("Reviewed Ritsu hand-size implementation is missing.");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location))).ToLowerInvariant();
        // The consumed hand-size/tag contracts are bytecode-equivalent across
        // these two reviewed runtimes. Pin both; stamp the actual loaded one.
        if (hash != ReviewedRitsuRuntimeHash && hash != ReviewedRitsu065RuntimeHash)
            throw new PredictionUnsupportedException("Ritsu hand-size runtime binary changed; adapter review is required.");
        _capturedRitsuRuntimeHash = hash;
        _handSizeTranspiler = AccessTools.Method(type, "CardOnPlayTranspiler");
        ResolveReviewedBaseLibTranspiler();
        foreach (var card in new[] { typeof(CrashLanding), typeof(Scrawl), typeof(Dredge), typeof(Pillage) })
        {
            var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(card)!;
            HandSizeMachines.Add(onPlay, AccessTools.Method(onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext"));
        }
        harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "RejectPatchedStateMachine"),
            prefix: new HarmonyMethod(typeof(ConditionalCardPatches), nameof(ReviewedHandSizeMachine)));
        harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "CaptureLiveStamp"),
            postfix: new HarmonyMethod(typeof(ConditionalCardPatches), nameof(AppendHandSizeComposition)));
    }

    private static void ResolveReviewedBaseLibTranspiler()
    {
        // Optional frameworks may initialize after this adapter. Resolve at the
        // live boundary too, rather than treating their startup order as absence.
        if (_baseLibHandSizeTranspiler is not null) return;
        var baseLib = AccessTools.TypeByName("BaseLib.Patches.Hooks.CardOnPlay_MaxHandSizePatch");
        if (baseLib is not null)
        {
            string baseHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(baseLib.Assembly.Location))).ToLowerInvariant();
            if (baseHash != ReviewedBaseLibHash)
                throw new PredictionUnsupportedException("BaseLib hand-size binary changed; adapter review is required.");
            _baseLibHandSizeTranspiler = AccessTools.DeclaredMethod(baseLib, "Transpiler");
        }
    }

    private static bool ReviewedHandSizeMachine(MethodInfo target)
    {
        if (!HandSizeMachines.TryGetValue(target, out var machine)) return true;
        ResolveReviewedBaseLibTranspiler();
        var patches = Harmony.GetPatchInfo(machine);
        if (patches is null || patches.Owners.Count == 0) return true;
        // BaseLib changes only the fixed hand-size load to its owner query.
        // Ritsu deliberately leaves BaseLib's base-amount token intact and
        // bridges the query to the same maximum that the solver freezes at root.
        // Keep both native patches; accept only the pinned methods and order.
        if (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0
            || patches.Finalizers.Count != 0 || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0)
            return true;
        static bool Matches(Patch patch, MethodInfo method, string owner, string[] after)
            => patch.PatchMethod == method && patch.owner == owner && patch.priority == Priority.Normal
                && patch.before.Length == 0 && patch.after.SequenceEqual(after);
        var ritsu = patches.Transpilers.Where(patch => Matches(patch, _handSizeTranspiler,
            "com.ritsukage.sts2-RitsuLib.framework-core", ["BaseLib"])).ToArray();
        if (ritsu.Length != 1) return true;
        if (patches.Transpilers.Count == 1) return false;
        if (_baseLibHandSizeTranspiler is null || patches.Transpilers.Count != 2) return true;
        var basePatches = patches.Transpilers.Where(patch => Matches(patch, _baseLibHandSizeTranspiler, "BaseLib", [])).ToArray();
        return basePatches.Length != 1 || !PatchProcessor.GetSortedPatchMethods(machine, patches.Transpilers.ToArray())
            .SequenceEqual(new[] { _baseLibHandSizeTranspiler, _handSizeTranspiler });
    }

    private static void AppendHandSizeComposition(ref string? __result)
    {
        if (__result is null) return;
        foreach (var target in HandSizeMachines.Keys)
            if (Harmony.GetPatchInfo(HandSizeMachines[target]) is { Owners.Count: > 0 } && ReviewedHandSizeMachine(target))
                throw new PredictionUnsupportedException($"Unreviewed hand-size state-machine composition for {target.DeclaringType?.Name}.");
        string signature = string.Join(';', HandSizeMachines.Select(pair => pair.Key.DeclaringType!.Name + ":"
            + AdaptedCardOnPlayMirrors.DescribeActual(pair.Value, Harmony.GetPatchInfo(pair.Value), includeIndex: true)));
        __result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            __result + ":ritsu-baselib-hand-size-v2:" + _capturedRitsuRuntimeHash + ":"
            + (_baseLibHandSizeTranspiler is null ? "absent" : ReviewedBaseLibHash) + ":" + signature)));
    }
}
