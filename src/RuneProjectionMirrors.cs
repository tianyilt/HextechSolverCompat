using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal sealed class FrozenRuneCounter(int value) : IPredictionStateForkable
{
    internal int Value { get; } = value;
    public object Fork(PredictionForkContext context) => new FrozenRuneCounter(Value);
    internal static void Write(FrozenRuneCounter state, ref ModelPredictionStateWriter writer) => writer.Add("value", state.Value);
}

// CombatSolver 0.47.1 evaluates hand draw and maximum energy through native
// read-only hooks. Supply the current branch explicitly around those synchronous
// calls; a cloned relic must never consult Owner.Creature.CombatState or live HP.
internal static class RuneProjectionMirrors
{
    private static readonly ConditionalWeakTable<AbstractModel, object> Detached = new();
    [ThreadStatic] private static SimulatedCombatState? _branch;
    internal static SimulatedCombatState? CurrentBranch => _branch;

    internal static void Register(Harmony harmony)
    {
        RegisterCounter<HubrisRune>(r => r.SavedStacks);
        RegisterCounter<ShrinkEngineRune>(r => r.SavedStacks);
        RegisterCounter<InfiniteLoopRune>(r => r.SavedCombatVictories);
        harmony.Patch(AccessTools.Method(typeof(ModelPredictionStateMirrors), "CaptureRootState"),
            postfix: new HarmonyMethod(typeof(RuneProjectionMirrors), nameof(MarkDetached)));
        foreach (string method in new[] { "GetModifiedHandDraw", "GetModifiedMaxEnergy" })
            harmony.Patch(AccessTools.Method(typeof(PersistentPowerSupport), method),
                prefix: new HarmonyMethod(typeof(RuneProjectionMirrors), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(RuneProjectionMirrors), nameof(Leave)));

        foreach (Type type in new[] { typeof(NimbleRune), typeof(ZealotRune), typeof(BrutalForceRune),
                     typeof(VitalitySurgeRune), typeof(HubrisRune), typeof(ShrinkEngineRune), typeof(SpeedsterRune) })
            harmony.Patch(AccessTools.Method(type, nameof(AbstractModel.ModifyHandDraw)),
                prefix: new HarmonyMethod(typeof(RuneProjectionMirrors), nameof(Draw)));
        foreach (Type type in new[] { typeof(LoopRune), typeof(VitalitySurgeRune), typeof(ShrinkEngineRune),
                     typeof(EurekaRune), typeof(InfiniteLoopRune) })
            harmony.Patch(AccessTools.Method(type, nameof(AbstractModel.ModifyMaxEnergy)),
                prefix: new HarmonyMethod(typeof(RuneProjectionMirrors), nameof(Energy)));
    }

    private static void RegisterCounter<T>(Func<T, int> value) where T : HextechRelicBase =>
        ModelPredictionStateMirrors.RegisterRelic<T, FrozenRuneCounter>(
            "frozen-combat-victories-v1", (_, r) => new FrozenRuneCounter(value(r)),
            (T r, ref ModelPredictionStateWriter writer) => writer.Add("value", value(r)), FrozenRuneCounter.Write);

    private static void MarkDetached(AbstractModel clone)
    {
        if (clone is HextechRelicBase) Detached.GetValue(clone, _ => new object());
    }

    private static void Enter(SimulatedCombatState combat, out SimulatedCombatState? __state)
    {
        __state = _branch;
        _branch = combat;
    }

    private static void Leave(SimulatedCombatState? __state) => _branch = __state;

    private static SimulatedCombatState RequireBranch() => _branch
        ?? throw new PredictionUnsupportedException("海克斯抽牌/能量镜像缺少当前搜索分支，拒绝读取实机状态。");

    private static bool Draw(HextechRelicBase __instance, Player player, decimal count, ref decimal __result)
    {
        if (!Detached.TryGetValue(__instance, out _)) return true;
        var combat = RequireBranch();
        __result = count;
        if (player != __instance.Owner) return false;
        decimal bonus = __instance switch
        {
            NimbleRune r => r.DynamicVars.Cards.BaseValue,
            BrutalForceRune r => combat.RoundNumber == 1 ? r.DynamicVars.Cards.BaseValue : 0m,
            ZealotRune r => combat.RoundNumber <= 1
                ? Math.Floor(combat.RelicsOf(player).Count / r.DynamicVars["RelicsNeeded"].BaseValue) : 0m,
            VitalitySurgeRune r => Math.Floor(RequireMaxHp(combat, player) / r.DynamicVars["HpPerCard"].BaseValue),
            HubrisRune r => combat.RoundNumber <= 1
                ? Math.Floor(r.SavedStacks / r.DynamicVars["StacksPerBonus"].BaseValue) * r.DynamicVars.Cards.BaseValue : 0m,
            ShrinkEngineRune r => Math.Floor(r.SavedStacks / 4m),
            SpeedsterRune => PersistentPowerSupport.GetModifiedMaxEnergy(combat, player) / 2,
            _ => throw new PredictionUnsupportedException($"Missing hand-draw implementation for {__instance.GetType().Name}")
        };
        __result += bonus;
        return false;
    }

    private static bool Energy(HextechRelicBase __instance, Player player, decimal amount, ref decimal __result)
    {
        if (!Detached.TryGetValue(__instance, out _)) return true;
        var combat = RequireBranch();
        __result = amount;
        if (player != __instance.Owner) return false;
        __result += __instance switch
        {
            LoopRune r => r.DynamicVars.Energy.BaseValue,
            VitalitySurgeRune r => Math.Floor(RequireMaxHp(combat, player) / r.DynamicVars["HpPerEnergy"].BaseValue),
            ShrinkEngineRune r => Math.Floor(r.SavedStacks / 8m),
            EurekaRune r => Math.Floor(combat.RelicsOf(player).Count / r.DynamicVars["RelicsNeeded"].BaseValue),
            InfiniteLoopRune r => r.DynamicVars.Energy.BaseValue + Math.Floor(r.SavedCombatVictories / r.DynamicVars["StacksPerEnergy"].BaseValue),
            _ => throw new PredictionUnsupportedException($"Missing maximum-energy implementation for {__instance.GetType().Name}")
        };
        return false;
    }

    private static int RequireMaxHp(SimulatedCombatState combat, Player player) =>
        (combat._predictionState ?? throw new PredictionUnsupportedException("海克斯生命系数镜像缺少分支状态。"))
        .GetCreature(player.Creature).MaxHp;
}
