using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record NativePlayCostScope(CombatPredictionSimulator Simulator, PredictedCard Card, int EnergyValue, NativePlayCostScope? Previous);
    [ThreadStatic] private static NativePlayCostScope? _nativePlayCostScope;
    [ThreadStatic] private static PredictedCard? _nativeBeforePlayCard;
    private static void RegisterNativeCostGates(Harmony harmony)
    {
        var wrapper = AccessTools.Method(typeof(CombatPredictionSimulator), "OnPlayWrapper");
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), nameof(EnterNativePlayCostScope));
        var finalizer = AccessTools.Method(typeof(NativeRuneBridge), nameof(LeaveNativePlayCostScope));
        harmony.Patch(wrapper, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
        NativeCallbackContracts.AddScope(wrapper, prefix, finalizer);
        RegisterState<UltimateRefreshRune>();
        RuneMirrors.RegisterNativeBase<UltimateRefreshRune>();
        RegisterNativeReplayHooks<UltimateRefreshRune>();
        RegisterState<BackToBasicsRune>();
        RuneMirrors.RegisterNativeBase<BackToBasicsRune>();
        RegisterNativeSustainCallbacks<BackToBasicsRune>();
        ShouldPlayMirrors.Registry.Register<BackToBasicsRune>((rune, context) =>
        {
            var previous = _nativeBeforePlayCard;
            _nativeBeforePlayCard = context.Card;
            try { return Invoke(rune, context.Simulator, model => model.ShouldPlay(context.Card.MutablePreview, context.AutoPlayType)); }
            finally { _nativeBeforePlayCard = previous; }
        });
        foreach (var type in new[] { typeof(UltimateRefreshRune), typeof(BackToBasicsRune) })
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name is
                "ShouldPlay" or "ModifyCardPlayCount" or "AfterModifyingCardPlayCount"
                or "ModifyDamageMultiplicativeCompat" or "ModifyBlockMultiplicative"))
                NativeCallbackContracts.Add(method);
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRelicBase), "IsOwnedCardWithEffectiveCostAtLeast"));
    }

    private static void EnterNativePlayCostScope(CombatPredictionSimulator __instance, PredictedCard card,
        ResourceInfo resources, out NativePlayCostScope? __state)
    {
        __state = _nativePlayCostScope;
        _nativePlayCostScope = new(__instance, card, resources.EnergyValue, __state);
    }
    private static void LeaveNativePlayCostScope(NativePlayCostScope? __state) => _nativePlayCostScope = __state;
    private static int? FindNativePlayCost(CardModel card)
    {
        for (var scope = _nativePlayCostScope; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Simulator, _simulator) && scope.Card.References(card)) return scope.EnergyValue;
        return null;
    }
}
