using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Relics;
using System.Reflection.Emit;

namespace HextechSolverCompat;

// Mirrors the native persistent HP lifecycle, not combat-start power replay.
// Marker/base/projected tracking is owned by each prediction branch.
internal sealed class EnemyHpState
{
    private static readonly MonsterHexKind[] Providers = [MonsterHexKind.Goliath, MonsterHexKind.AstralBody,
        MonsterHexKind.Stats, MonsterHexKind.StatsOnStats, MonsterHexKind.StatsOnStatsOnStats,
        MonsterHexKind.GoldenSpatula, MonsterHexKind.MadScientist];
    internal readonly Dictionary<MonsterHexKind, HashSet<uint>> Applied = [];
    internal readonly Dictionary<uint, int> Base = [];
    internal readonly Dictionary<uint, int> Projected = [];
    internal readonly Dictionary<uint, int> LegacyTankStacks = [];

    internal static EnemyHpState Capture(HextechMayhemCombatTrackingState tracking)
    {
        var state = new EnemyHpState();
        state.Applied.Add(MonsterHexKind.Goliath, new(tracking.GoliathApplied));
        state.Applied.Add(MonsterHexKind.AstralBody, new(tracking.AstralBodyApplied));
        state.Applied.Add(MonsterHexKind.Stats, new(tracking.StatsApplied));
        state.Applied.Add(MonsterHexKind.StatsOnStats, new(tracking.StatsOnStatsApplied));
        state.Applied.Add(MonsterHexKind.StatsOnStatsOnStats, new(tracking.StatsOnStatsOnStatsApplied));
        state.Applied.Add(MonsterHexKind.GoldenSpatula, new(tracking.GoldenSpatulaApplied));
        state.Applied.Add(MonsterHexKind.MadScientist, new(tracking.MadScientistApplied));
        foreach (var pair in tracking.MonsterMaxHpCoefficientBase) state.Base.Add(pair.Key, pair.Value);
        foreach (var pair in tracking.MonsterMaxHpCoefficientProjected) state.Projected.Add(pair.Key, pair.Value);
        foreach (var pair in tracking.TankEngineStacks) state.LegacyTankStacks.Add(pair.Key, pair.Value);
        return state;
    }
    internal EnemyHpState Fork()
    {
        var copy = new EnemyHpState();
        foreach (var (kind, ids) in Applied) copy.Applied.Add(kind, new(ids));
        foreach (var pair in Base) copy.Base.Add(pair.Key, pair.Value);
        foreach (var pair in Projected) copy.Projected.Add(pair.Key, pair.Value);
        foreach (var pair in LegacyTankStacks) copy.LegacyTankStacks.Add(pair.Key, pair.Value);
        return copy;
    }
    internal void Write(ref ModelPredictionStateWriter writer)
    {
        writer.Add("hpMarkerKinds", Applied.Count);
        foreach (var (kind, ids) in Applied.OrderBy(p => p.Key))
        {
            writer.Add($"hpMarkerCount:{kind}", ids.Count);
            foreach (uint id in ids.Order()) writer.Add($"hpMarker:{kind}:{id}", true);
        }
        writer.Add("hpBaseCount", Base.Count);
        foreach (var (id, value) in Base.OrderBy(p => p.Key)) writer.Add($"hpBase:{id}", value);
        writer.Add("hpProjectedCount", Projected.Count);
        foreach (var (id, value) in Projected.OrderBy(p => p.Key)) writer.Add($"hpProjected:{id}", value);
        writer.Add("legacyTankCount", LegacyTankStacks.Count);
        foreach (var (id, value) in LegacyTankStacks.OrderBy(p => p.Key)) writer.Add($"legacyTank:{id}", value);
    }
    private static decimal Bonus(MonsterHexKind kind, int tier) => kind switch
    {
        MonsterHexKind.Goliath or MonsterHexKind.AstralBody => tier <= 1 ? .20m : tier == 2 ? .30m : .40m,
        MonsterHexKind.GoldenSpatula => tier <= 1 ? .25m : tier == 2 ? .30m : .45m,
        MonsterHexKind.MadScientist => tier <= 1 ? -.30m : tier == 2 ? -.15m : 0m,
        _ => EnemyAttributeBoostValues.GetBonusFraction(kind, tier)
    };
    private bool HasMarker(uint id) => Applied.Values.Any(ids => ids.Contains(id))
        || LegacyTankStacks.GetValueOrDefault(id) > 0;
    private int CaptureBase(EnemyState state, CombatPredictionSimulator simulator, Creature creature, out bool migrated)
    {
        var target = simulator.State.GetCreature(creature);
        migrated = false;
        if (creature.CombatId is not uint id) return Math.Max(1, target.MaxHp);
        if (Base.TryGetValue(id, out int tracked) && tracked > 0)
        {
            migrated = Projected.GetValueOrDefault(id) <= 0 && HasMarker(id);
            return tracked;
        }
        migrated = HasMarker(id);
        var fixedBonuses = new[] { MonsterHexKind.Goliath, MonsterHexKind.AstralBody, MonsterHexKind.GoldenSpatula,
                MonsterHexKind.Stats, MonsterHexKind.StatsOnStats, MonsterHexKind.StatsOnStatsOnStats }
            .Where(kind => Applied[kind].Contains(id)).Select(kind => Bonus(kind, state.StrengthTier)).ToArray();
        int? rawHp = creature.MonsterMaxHpBeforeModification is > 0 ? creature.MonsterMaxHpBeforeModification : null;
        int value = migrated ? HextechLegacyEnemyMaxHpMigration.ResolveBaseMaxHp(target.MaxHp, rawHp,
            fixedBonuses, Applied[MonsterHexKind.MadScientist].Contains(id)
                ? state.StrengthTier <= 1 ? .30m : state.StrengthTier == 2 ? .15m : 0m : 0m,
            Math.Max(0, LegacyTankStacks.GetValueOrDefault(id))) : Math.Max(1, target.MaxHp);
        Base[id] = value;
        return value;
    }
    private static bool FurCoatMarked(SimulatedCombatState combat) => combat.CurrentMapCoord is { } coord
        && combat.Players.SelectMany(combat.RelicsOf).OfType<FurCoat>()
            .Any(relic => relic.GetMarkedCoords()?.Contains(coord) == true);
    private static void SetHp(CombatPredictionSimulator simulator, Creature creature, int value)
    {
        var target = simulator.State.GetCreature(creature);
        int delta = value - target.CurrentHp;
        if (delta == 0) return;
        target.CurrentHp = value;
        HookMirrors.AfterCurrentHpChanged(simulator, creature, delta);
    }
    internal void Reapply(EnemyState state, CombatPredictionSimulator simulator, SimulatedCombatState combat, Creature creature)
    {
        var target = simulator.State.GetCreature(creature);
        int basis = CaptureBase(state, simulator, creature, out _);
        if (creature.CombatId is uint id && Projected.TryGetValue(id, out int previous) && previous > 0 && previous != target.MaxHp)
        {
            basis = (int)Math.Clamp((long)basis + target.MaxHp - previous, 1L, int.MaxValue);
            Base[id] = basis;
        }
        decimal scale = HextechEnemyCoefficientHelper.CombineBonusFractionsByHex(Providers
            .Where(state.Has).Select(kind => (kind, Bonus(kind, state.StrengthTier)))
            .Concat(state.Has(MonsterHexKind.TankEngine) && creature.CombatId is uint tankId
                ? new[] { (MonsterHexKind.TankEngine, Math.Max(0, LegacyTankStacks.GetValueOrDefault(tankId)) * .05m) }
                : Array.Empty<(MonsterHexKind, decimal)>()));
        int expected = (int)Math.Clamp(Math.Floor(basis * scale), 1m, int.MaxValue);
        int oldMax = target.MaxHp, oldHp = target.CurrentHp;
        target.SetMaxHp(expected);
        bool marked = FurCoatMarked(combat);
        if (expected > oldMax)
        {
            int gain = Math.Max(0, target.MaxHp - oldMax);
            if (gain > 0) SetHp(simulator, creature, marked ? 1 : (int)Math.Min(target.MaxHp, (long)oldHp + gain));
        }
        else if (marked) SetHp(simulator, creature, 1);
        if (creature.CombatId is uint key) Projected[key] = Math.Max(1, target.MaxHp);
    }
    internal int CaptureNativeBase(EnemyState state, CombatPredictionSimulator simulator, Creature creature, int? basis)
    {
        if (basis is { } value && creature.CombatId is uint id)
        { Base[id] = Math.Max(1, value); Projected.Remove(id); return Base[id]; }
        return CaptureBase(state, simulator, creature, out _);
    }
    internal void Apply(EnemyState state, CombatPredictionSimulator simulator, SimulatedCombatState combat, Creature creature)
    {
        var target = simulator.State.GetCreature(creature);
        if (creature.Side != CombatSide.Enemy || !target.IsAlive) return;
        CaptureBase(state, simulator, creature, out bool migrated);
        if (migrated) Reapply(state, simulator, combat, creature);
        foreach (var kind in Providers)
            if (state.Has(kind) && creature.CombatId is uint id && Applied[kind].Add(id))
            {
                Reapply(state, simulator, combat, creature);
                if (kind == MonsterHexKind.MadScientist && target.IsAlive)
                    combat.ApplyPowerFromSource(typeof(MegaCrit.Sts2.Core.Models.Powers.PersonalHivePower), creature, 1, creature, null);
            }
        if (state.Has(MonsterHexKind.GlassCannon))
        {
            int cap = Math.Max(1, (int)Math.Floor(target.MaxHp * GlassCannonEnemyHex.HealCapPercent));
            if (target.CurrentHp > cap) SetHp(simulator, creature, cap);
        }
    }
}

internal static class EnemySpawnBridge
{
    internal static void Register(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(MonsterSpawnSupport), nameof(MonsterSpawnSupport.AddCreated)),
        transpiler: new HarmonyMethod(typeof(EnemySpawnBridge), nameof(InsertPersistentEffects)));
    private static IEnumerable<CodeInstruction> InsertPersistentEffects(IEnumerable<CodeInstruction> instructions)
    {
        var entrance = AccessTools.Method(typeof(MonsterSpawnSupport), "ApplyCreatureAddedRelics");
        var powers = AccessTools.Method(typeof(MonsterSpawnSupport), "ApplyNativeEntrancePowers");
        int count = 0, powerCount = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.Calls(powers))
            {
                powerCount++;
                yield return new(OpCodes.Ldarg_0);
                yield return new(OpCodes.Ldarg_1);
                yield return new(OpCodes.Call, AccessTools.Method(typeof(EnemySpawnBridge), nameof(ResolveNativeEntrancePowers)));
            }
            if (!instruction.Calls(entrance)) continue;
            count++;
            yield return new(OpCodes.Ldarg_0);
            yield return new(OpCodes.Ldarg_1);
            yield return new(OpCodes.Ldarg_3);
            yield return new(OpCodes.Call, AccessTools.Method(typeof(EnemySpawnBridge), nameof(Apply)));
        }
        if (count != 1 || powerCount != 1) throw new InvalidOperationException($"Monster entrance hook shape changed: {count}/{powerCount}.");
    }
    private static void ResolveNativeEntrancePowers(CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        if (combat.Modifiers.OfType<HextechMayhemModifier>().Any(modifier => ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.ServantMaster)))
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
    }
    private static void Apply(CombatPredictionSimulator simulator, SimulatedCombatState combat, Creature creature)
    {
        foreach (var modifier in combat.Modifiers.OfType<HextechMayhemModifier>())
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
            NativeRuneBridge.BindNativeNearEnemy(state, simulator.State.GetCreature(creature));
            state.Hp.Apply(state, simulator, combat, creature);
            NativeRuneBridge.DispatchNativeServantSpawn(modifier, state, simulator, creature);
        }
    }
}
