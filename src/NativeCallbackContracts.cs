using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;

namespace HextechSolverCompat;

// Shared composition guard for original callbacks and the exact command/read sites
// reviewed by each family. A registration never licenses an unknown extra patch.
internal static class NativeCallbackContracts
{
    private static readonly Dictionary<MethodBase, MethodInfo?> Targets = [];
    private static readonly Dictionary<MethodBase, List<(MethodInfo Method, string Owner, int Priority)>> NativePostfixes = [];
    private static readonly Dictionary<MethodBase, (MethodInfo Method, string Owner, int Priority)> NativePrefixes = [];
    private static readonly Dictionary<MethodBase, MethodInfo[]> AlternatePostfixOrders = [];
    internal static void AddAlternatePostfixOrder(MethodBase target, params MethodInfo[] methods)
    {
        var registered = NativePostfixes[target].Select(entry => entry.Method).ToArray();
        if (methods.Length != registered.Length || methods.Distinct().Count() != methods.Length
            || methods.Any(method => !registered.Contains(method)))
            throw new InvalidOperationException("Alternate order must contain exactly the reviewed postfixes.");
        AlternatePostfixOrders.Add(target, methods);
    }
    internal static void AddNativePrefix(MethodBase target, MethodInfo method, string owner, int priority, MethodInfo? transpiler = null)
    {
        if (Scopes.ContainsKey(target)) throw new InvalidOperationException("Native prefix conflicts with an owned scope.");
        Add(target, transpiler); NativePrefixes.Add(target, (method, owner, priority));
    }
    internal static void AddNativePostfix(MethodBase target, MethodInfo method, string owner, int priority, MethodInfo? transpiler = null)
    {
        Add(target, transpiler);
        if (!NativePostfixes.TryGetValue(target, out var entries)) NativePostfixes.Add(target, entries = []);
        if (entries.Any(entry => entry.Method == method)) throw new InvalidOperationException("Duplicate native postfix contract.");
        entries.Add((method, owner, priority));
    }

    private static readonly Dictionary<MethodBase, (MethodInfo Prefix, MethodInfo Finalizer)> Scopes = [];

    internal static void AddScope(MethodBase target, MethodInfo prefix, MethodInfo finalizer, MethodInfo? transpiler = null)
    {
        Add(target, transpiler);
        Scopes.Add(target, (prefix, finalizer));
    }

    internal static void Add(MethodBase target, MethodInfo? transpiler = null)
    {
        if (!Targets.TryAdd(target, transpiler) && Targets[target] != transpiler)
            throw new InvalidOperationException($"Conflicting native callback contract: {target}.");
    }

    internal static void Install(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "CaptureLiveStamp"),
            postfix: new HarmonyMethod(typeof(NativeCallbackContracts), nameof(AppendStamp)));

    internal static void Validate()
    {
        foreach (var (target, expected) in Targets)
        {
            bool scoped = Scopes.TryGetValue(target, out var scope);
            bool nativePostfix = NativePostfixes.TryGetValue(target, out var native);
            bool nativePrefix = NativePrefixes.TryGetValue(target, out var nativeBefore);
            var patches = Harmony.GetPatchInfo(target);
            if (patches is null)
            {
                if (expected is not null || scoped || nativePostfix || nativePrefix) throw new PredictionUnsupportedException($"Native callback bridge missing: {target}.");
                continue;
            }
            bool Matches(Patch patch, MethodInfo method) => patch.owner == "HextechSolverCompat" && patch.PatchMethod == method
                && patch.priority == Priority.Normal && patch.before.Length == 0 && patch.after.Length == 0;
            bool MatchesPostfixOrder()
            {
                var actual = PatchProcessor.GetSortedPatchMethods(target, patches.Postfixes.ToArray());
                return actual.SequenceEqual(native!.Select(entry => entry.Method))
                    || AlternatePostfixOrders.TryGetValue(target, out var alternate) && actual.SequenceEqual(alternate);
            }
            if (patches.Prefixes.Count != (scoped || nativePrefix ? 1 : 0) || patches.Postfixes.Count != (nativePostfix ? native!.Count : 0)
                || nativePrefix && (patches.Prefixes[0].owner != nativeBefore.Owner || patches.Prefixes[0].PatchMethod != nativeBefore.Method
                    || patches.Prefixes[0].priority != nativeBefore.Priority || patches.Prefixes[0].before.Length != 0 || patches.Prefixes[0].after.Length != 0)
                || nativePostfix && (native!.Any(entry => patches.Postfixes.Count(patch => patch.owner == entry.Owner
                        && patch.PatchMethod == entry.Method && patch.priority == entry.Priority
                        && patch.before.Length == 0 && patch.after.Length == 0) != 1)
                    || !MatchesPostfixOrder())
                || patches.Finalizers.Count != (scoped ? 1 : 0)
                || scoped && (!Matches(patches.Prefixes[0], scope.Prefix) || !Matches(patches.Finalizers[0], scope.Finalizer))
                || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0
                || patches.Transpilers.Count != (expected is null ? 0 : 1)
                || expected is not null && (patches.Transpilers[0].owner != "HextechSolverCompat"
                    || patches.Transpilers[0].PatchMethod != expected || patches.Transpilers[0].priority != Priority.Normal
                    || patches.Transpilers[0].before.Length != 0 || patches.Transpilers[0].after.Length != 0))
                throw new PredictionUnsupportedException($"Unreviewed native callback composition: {target.DeclaringType?.Name}.{target.Name}. "
                    + AdaptedCardOnPlayMirrors.DescribeActual(target, patches, includeIndex: true));
        }
    }

    private static void AppendStamp(ref string? __result)
    {
        if (__result is null) return;
        string signature = string.Join(';', Targets.Keys.OrderBy(method => method.DeclaringType!.FullName + ":" + method.Name, StringComparer.Ordinal)
            .Select(method => AdaptedCardOnPlayMirrors.DescribeActual(method, Harmony.GetPatchInfo(method), includeIndex: true)));
        __result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(__result + ":native-callbacks-v1:" + signature)));
    }
}
