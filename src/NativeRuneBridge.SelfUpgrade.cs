using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using NativeCounters = HextechRunes.HextechSelfUpgradeCardStore.Counters;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static CombatPredictionSimulator? _nativeGrowthSimulator;
    private static CombatPredictionSimulator? GrowthSimulator => _simulator ?? _nativeGrowthSimulator;
    private static void RegisterNativeSelfUpgrades(Harmony harmony)
    {
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeGrowthStore));
        foreach (var name in new[] { "AddDamageOnPlay", "AddBlockOnPlay" })
        {
            var method = AccessTools.Method(typeof(HextechSelfUpgradeCardStore), name);
            harmony.Patch(method, transpiler: new HarmonyMethod(rewrite));
            NativeCallbackContracts.Add(method, rewrite);
        }
        var downgrade = AccessTools.Method(AccessTools.Inner(typeof(HextechSelfUpgradeCardStore), "DowngradePatch"), "Postfix");
        harmony.Patch(downgrade, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(downgrade, rewrite);
        foreach (var name in new[] { "ApplyDamage", "ApplyBlock" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechSelfUpgradeCardStore), name));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ClawUpgradeRune), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeGrowthDeckCards)),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards)), nameof(NativeGrowthCombatCards)));
        var group = AccessTools.Method(AccessTools.Inner(typeof(ClawUpgradeRune), "<>c"), "<GrowClaws>b__2_1");
        PatchEventCallback(harmony, group,
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.DeckVersion)), nameof(NativeGrowthDeckVersion)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(ClawUpgradeRune), "GrowClaws"));
        RegisterNativeGrowthRune<ClawUpgradeRune>();
        RegisterNativeGrowthRune<SowUpgradeRune>();
        RegisterNativeGrowthRune<ReapUpgradeRune>();
        RegisterNativeGrowthRune<IronWaveUpgradeRune>();
        foreach (var type in new[] { typeof(SowUpgradeRune), typeof(ReapUpgradeRune), typeof(IronWaveUpgradeRune) })
        {
            NativeCallbackContracts.Add(AccessTools.Method(type, "AfterCardPlayed"));
            NativeCallbackContracts.Add(AccessTools.Method(type, "RecordSelfUpgradeOnPlay"));
        }
        var dampen = AccessTools.Method(typeof(SimulatedCombatState), "ApplyDampen");
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), nameof(EnterNativeGrowthScope));
        var finalizer = AccessTools.Method(typeof(NativeRuneBridge), nameof(LeaveNativeGrowthScope));
        harmony.Patch(dampen, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
        NativeCallbackContracts.AddScope(dampen, prefix, finalizer);
    }
    private static void RegisterNativeGrowthRune<T>() where T : HextechRelicBase
    {
        _ = NativeRuneState.Fields(typeof(T));
        ModelPredictionStateMirrors.RegisterRelic<T, NativeRuneState>("native-permanent-card-growth-v1",
            (simulator, live) => new NativeRuneState(NativeRuneState.Clone(live))
            { SelfUpgrades = NativeSelfUpgradeState.Capture(simulator, live.Owner) },
            NativeRuneState.WriteModel<T>, NativeRuneState.WriteState);
        RuneMirrors.RegisterNativeBase<T>();
        RegisterAfterCardPlayedCallback<T>();
    }
    private static void EnterNativeGrowthScope(CombatPredictionSimulator simulator, out CombatPredictionSimulator? __state)
    { __state = _nativeGrowthSimulator; _nativeGrowthSimulator = NativeSelfUpgradeState.HasAnchor(simulator) ? simulator : null; }
    private static void LeaveNativeGrowthScope(CombatPredictionSimulator? __state) => _nativeGrowthSimulator = __state;

    private static NativeSelfUpgradeState? GrowthOwner(CardModel card)
    {
        if (NativeSelfUpgradeState.OwnerOf(card) is { } owner) return owner;
        if (GrowthSimulator is not { } simulator) return null;
        var state = NativeSelfUpgradeState.Require(simulator);
        state.BindCurrentCards();
        if (NativeSelfUpgradeState.OwnerOf(card) is { } bound) return bound;
        var predicted = simulator.State.FindCard(card);
        if (predicted is null || !ReferenceEquals(predicted.MutablePreview, card))
            throw new PredictionUnsupportedException("Native growth tried to access a live card from a simulation.");
        return state;
    }
    private static CardModel? NativeGrowthDeckVersion(CardModel card)
        => GrowthOwner(card) is { } state ? state.DeckVersion(card) : card.DeckVersion;
    private static ConditionalWeakTable<CardModel, NativeCounters> NativeGrowthTable(CardModel card)
        => GrowthOwner(card)?.Table ?? HextechSelfUpgradeCardStore.BonusByCard;
    private static IReadOnlyList<CardModel> NativeGrowthDeckCards(CardPile pile)
        => _simulator is { } simulator ? NativeSelfUpgradeState.Require(simulator).DeckCards : pile.Cards;
    private static IEnumerable<CardModel> NativeGrowthCombatCards(PlayerCombatState player)
        => _simulator is { } simulator ? NativeSelfUpgradeState.Require(simulator).CombatCards : player.AllCards;

    private static IEnumerable<CodeInstruction> RewriteNativeGrowthStore(IEnumerable<CodeInstruction> instructions)
    {
        int decks = 0, tables = 0;
        var deck = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.DeckVersion));
        var table = AccessTools.Field(typeof(HextechSelfUpgradeCardStore), "BonusByCard");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(deck))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeGrowthDeckVersion));
                decks++;
            }
            if (instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, table))
            {
                instruction.opcode = OpCodes.Ldarg_0;
                instruction.operand = null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeGrowthTable)));
                tables++;
                continue;
            }
            yield return instruction;
        }
        if (decks != 1 || tables != 1)
            throw new InvalidOperationException($"Original native growth access changed: deck={decks}, table={tables}.");
    }
}
