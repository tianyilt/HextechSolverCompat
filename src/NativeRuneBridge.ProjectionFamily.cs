using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static int? _nativeDeckCount;
    private static void RegisterNativeProjectionFamily(Harmony harmony)
    {
        RegisterNativeSustainQuery<GoliathRune>();
        RegisterNativeSustainQuery<GoldenSpatulaRune>();
        PatchEventCallback(harmony, AccessTools.PropertyGetter(typeof(AnthonyBiasRune), "SustainMultiplier"),
            Site(AccessTools.Method(typeof(AnthonyBiasRune), "CountDeckCards"), nameof(NativeFrozenDeckCount)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(AnthonyBiasRune), "CountDeckCards"));
        ModelPredictionStateMirrors.RegisterRelic<AnthonyBiasRune, NativeRuneState>("native-deck-projection-v1",
            (simulator, live) => new NativeRuneState(NativeRuneState.Clone(live)) { FrozenDeckCount = live.Owner.Deck.Cards.Count,
                SelfUpgrades = NativeSelfUpgradeState.NeedsPermanentDeck(live.Owner) ? NativeSelfUpgradeState.Capture(simulator, live.Owner) : null },
            NativeRuneState.WriteModel<AnthonyBiasRune>, NativeRuneState.WriteState);
        RuneMirrors.RegisterNativeBase<AnthonyBiasRune>();
        RegisterNativeSustainCallbacks<AnthonyBiasRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GiantSlayerRune), "ModifyDamageMultiplicativeCompat"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(GiantSlayerRune), "ResolveDamageMultiplier"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(GiantSlayerRune), "ModifyHandDraw"));
        RegisterState<GiantSlayerRune>();
        RuneMirrors.RegisterNativeBase<GiantSlayerRune>();
        RegisterNativeQueryCallbacks<GiantSlayerRune>(NativeQueries.DamageMultiplier);
        RegisterState<EightPennyGateRune>();
        RuneMirrors.RegisterNativeBase<EightPennyGateRune>();
        RegisterNativeReplayHooks<EightPennyGateRune>();
        RegisterNativeResultLocation<EightPennyGateRune>();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(EightPennyGateRune), "ShouldReplayAndExhaust"));
        foreach (var type in new[] { typeof(GoliathRune), typeof(GoldenSpatulaRune), typeof(AnthonyBiasRune), typeof(GiantSlayerRune), typeof(EightPennyGateRune) })
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name is
                "ModifyDamageMultiplicativeCompat" or "ModifyBlockMultiplicative" or "ModifyCardPlayCount"
                or "AfterModifyingCardPlayCount" or "ModifyCardPlayResultPileTypeAndPositionCompat"
                or "get_StackMultiplier" or "TotalBonusPercentFor"))
                if (!EventCallSites.ContainsKey(method)) NativeCallbackContracts.Add(method);
        NativeCallbackContracts.Add(AccessTools.PropertyGetter(typeof(GoldenSpatulaRune), "SustainMultiplier"));
    }

    private static void RegisterNativeSustainQuery<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        RegisterNativeSustainCallbacks<T>();
    }
    private static void RegisterNativeSustainCallbacks<T>() where T : HextechRelicBase
    {
        ModifyDamageMirrors.MultiplicativeRegistry.Register<T>((rune, context) => Invoke(rune, context.Simulator,
            model => model.ModifyDamageMultiplicativeCompat(context.Target, context.Amount, context.Props,
                context.Dealer, context.CardSource?.Preview)));
        ModifyBlockMultiplicativeMirrors.Registry.Register<T>((rune, context) => Invoke(rune, context.Simulator,
            model => model.ModifyBlockMultiplicative(context.Target, context.Amount, context.Props,
                context.CardSource?.Preview, context.CardPlay)));
    }

    private static readonly Func<AnthonyBiasRune, int> OriginalNativeDeckCount =
        AccessTools.Method(typeof(AnthonyBiasRune), "CountDeckCards").CreateDelegate<Func<AnthonyBiasRune, int>>();
    private static int NativeFrozenDeckCount(AnthonyBiasRune rune)
    {
        if (_simulator is not { } simulator) return OriginalNativeDeckCount(rune);
        if (NativeSelfUpgradeState.HasAnchor(simulator) && NativeSelfUpgradeState.Require(simulator) is { CapturesPermanentDeck: true } deck)
            return deck.DeckCards.Count;
        return _nativeDeckCount ?? throw new PredictionUnsupportedException("Native deck coefficient requires a frozen root count.");
    }
    private static int NativeBranchMaxHp(Creature creature)
        => _simulator is { } sim ? sim.State.GetCreature(creature).MaxHp : creature.MaxHp;
}
