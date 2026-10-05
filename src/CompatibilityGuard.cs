using CombatSolver;
using CombatSolver.Engine.Common;
using System.Security.Cryptography;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;

namespace HextechSolverCompat;

internal static class CompatibilityGuard
{
    internal static readonly HashSet<MonsterHexKind> EnemyHexes =
    [MonsterHexKind.BigStrength, MonsterHexKind.HeavyHitter, MonsterHexKind.Sturdy,
     MonsterHexKind.FirstAidKit, MonsterHexKind.VitalitySurge, MonsterHexKind.ProteinShake, MonsterHexKind.MoreTheMerrier,
     MonsterHexKind.Judicator, MonsterHexKind.TungstenRod, MonsterHexKind.SerpentsFang, MonsterHexKind.BadTaste,
     MonsterHexKind.CourageOfColossus, MonsterHexKind.Slap, MonsterHexKind.BloodPact, MonsterHexKind.ClownCollege,
     MonsterHexKind.AstralBody, MonsterHexKind.Goliath, MonsterHexKind.Stats, MonsterHexKind.StatsOnStats,
     MonsterHexKind.StatsOnStatsOnStats, MonsterHexKind.GiantSlayer, MonsterHexKind.GlassCannon,
     MonsterHexKind.ShrinkRay, MonsterHexKind.Corrosion, MonsterHexKind.OminousPact,
     MonsterHexKind.DeathHarvest, MonsterHexKind.CantTouchThis,
     MonsterHexKind.Loop, MonsterHexKind.Enlightenment, MonsterHexKind.SomethingForNothing,
     MonsterHexKind.LightEmUp, MonsterHexKind.TwiceThrice, MonsterHexKind.ReforgedHelmet,
     MonsterHexKind.BlueCandleMedkit, MonsterHexKind.EightPennyGate, MonsterHexKind.AncientStatue, MonsterHexKind.JeweledGauntlet,
     // Exact reviewed effects run only at native combat entry. Their resulting
     // stock powers/block are part of the root and use stock solver semantics.
     MonsterHexKind.ProtectiveVeil, MonsterHexKind.Thornmail, MonsterHexKind.StartupRoutine,
     MonsterHexKind.BrutalForce, MonsterHexKind.Zealot, MonsterHexKind.SuperBrain,
     MonsterHexKind.SkulkingColony, MonsterHexKind.PhantasmalGardener, MonsterHexKind.Exoskeleton,
     MonsterHexKind.ShrinkerBeetle, MonsterHexKind.TheLost, MonsterHexKind.TheForgotten,
     MonsterHexKind.Inklet, MonsterHexKind.Vantom, MonsterHexKind.FossilStalker, MonsterHexKind.Byrdonis];

    // Filled by the actual mirror registration path, never by a second list of
    // names. Registration is implementation coverage, not proof of validation.
    internal static readonly HashSet<Type> Runes = [];
    internal static readonly HashSet<Type> Cards = [];
    internal static readonly HashSet<Type> Enchantments = [];

    // These audited temporary stat subclasses retain native family lifecycles. Creation uses
    // the solver's temporary-stat application functions, including the internal
    // Strength/Dexterity grant; hidden negative Strength additionally preserves native Artifact visibility.
    internal static readonly HashSet<Type> Powers =
    [typeof(HextechTemporaryStrengthPower), typeof(HextechTemporaryDexterityPower),
     typeof(HextechLethalTempoTemporaryStrengthPower), typeof(HextechSlapTemporaryStrengthPower),
     typeof(HextechBloodPactTemporaryStrengthPower), typeof(HextechPlayerSlowPower), typeof(HextechTemporarySlowPower),
     typeof(HextechTemporaryStrengthLossPower), typeof(HextechTemporaryDexterityLossPower)];

    internal static void CheckVersions()
    {
        foreach (var assembly in new[] { typeof(AbstractModel).Assembly, typeof(CombatRootSnapshot).Assembly, typeof(ModEntry).Assembly })
        {
            string name = assembly.GetName().Name!;
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
            if (!PinnedAssemblies.Hashes.TryGetValue(name, out string? expectedHash) || hash != expectedHash)
                throw new NotSupportedException($"{name} 的二进制尚未通过此兼容包验证；请使用匹配版本并重新验证/构建。");
        }
        var mods = ModManager.GetLoadedMods().Where(m => m.manifest?.id != null)
            .ToDictionary(m => m.manifest!.id!, m => m.manifest!.version);
        foreach (var (id, expected) in PinnedAssemblies.Versions)
            if (!mods.TryGetValue(id, out string? actual) || actual != expected)
                throw new NotSupportedException($"版本尚未验证：{id}={actual ?? "missing"}，需要 {expected}");
    }

    internal static void Validate(CombatState state)
    {
        if (!Entry.Ready)
            throw new PredictionUnsupportedException($"海克斯兼容层未就绪：{Entry.Failure}");
        PowerExpiryBridge.ValidateContracts();
        NativeRuneBridge.ValidateNativeEnemyTurnContracts();
        NativeRuneBridge.ValidateGeneratedReactionContracts();
        NativeCallbackContracts.Validate();
        OpeningFormBoundary.Validate(state);
        if (state.Players.Count != 1 || HextechRelicBase.IsNetworkMultiplayerRun())
            throw new PredictionUnsupportedException("海克斯兼容层目前只验证了单人战斗。");
        List<string> unsupported = [];
        foreach (var modifier in state.Modifiers.OfType<HextechMayhemModifier>())
        {
            foreach (var hex in modifier.GetActiveMonsterHexes())
                if (!EnemyHexes.Contains(hex))
                    unsupported.Add(hex is MonsterHexKind.GlobeHead or MonsterHexKind.SolidTime
                        ? $"已禁用的流电来源 {hex}（旧存档仍然持有）" : $"敌方海克斯 {hex}");
        }
        foreach (var player in state.Players)
        {
            foreach (var relic in player.Relics)
                if (relic.GetType().Assembly == typeof(ModEntry).Assembly && !Runes.Contains(relic.GetType()))
                    unsupported.Add($"符文/遗物 {relic.Id.Entry}");
            foreach (var card in player.PlayerCombatState!.AllCards)
                if ((card.GetType().Assembly == typeof(ModEntry).Assembly && !Cards.Contains(card.GetType())) ||
                    card.Enchantment is { } enchantment && enchantment.GetType().Assembly == typeof(ModEntry).Assembly
                        && !Enchantments.Contains(enchantment.GetType()) ||
                    card.Affliction?.GetType().Assembly == typeof(ModEntry).Assembly)
                    unsupported.Add($"卡牌或附加效果 {card.Id.Entry}");
        }
        foreach (var creature in state.Creatures)
            foreach (var power in creature.Powers)
                if (power.GetType().Assembly == typeof(ModEntry).Assembly && !Powers.Contains(power.GetType()))
                    unsupported.Add($"能力 {power.Id.Entry}");
        if (unsupported.Count > 0)
            throw new PredictionUnsupportedException("海克斯兼容层尚未覆盖：" +
                string.Join("、", unsupported.Distinct().Take(12)) + "。本场停止求解和自动出牌；可继续手动游戏。");
    }

    internal static bool CaptureLiveBoundary(CombatState state, ref ContinuationStamp __result)
    {
        try { Validate(state); return true; }
        catch (PredictionUnsupportedException error)
        {
            // Live stamps are also used by UI/checkpoint observation outside a
            // caught search boundary. Never throw there and kill a native turn.
            // This sentinel cannot match a supported predicted continuation;
            // root capture still throws, so it can never authorize a route.
            __result = new ContinuationStamp("hextech_prediction_unavailable:" + error.Message);
            return false;
        }
    }

    internal static bool AllowNativeTurnSetup(CombatManager manager, ref Task? task, ref bool __result)
    {
        var state = manager.DebugOnlyGetState();
        if (state == null || state.Players.Any(p => p.PlayerCombatState == null)) return true;
        try { Validate(state); return true; }
        catch (PredictionUnsupportedException error)
        {
            // Returning false from TryInterceptSetup lets its existing caller
            // execute the original game setup. Do this before any solver setup
            // coroutine starts, never after it has partially mutated the turn.
            task = null;
            __result = false;
            Log.Info("[HextechSolverCompat] Native turn setup retained: " + error.Message);
            return false;
        }
    }
}
