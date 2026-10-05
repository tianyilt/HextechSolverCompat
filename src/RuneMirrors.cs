using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HextechRunes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal sealed class DrawCountState(int count) : IPredictionStateForkable
{
    internal int Count = count;
    public object Fork(PredictionForkContext context) => new DrawCountState(Count);
    internal static void Write(DrawCountState state, ref ModelPredictionStateWriter writer) => writer.Add("drawn", state.Count);
}

internal static partial class RuneMirrors
{
    internal static void Register(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.Heal)),
            prefix: new HarmonyMethod(typeof(RuneMirrors), nameof(ModifyHealing)),
            postfix: new HarmonyMethod(typeof(RuneMirrors), nameof(AfterHealing)));
        var heal = AccessTools.Method(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.Heal));
        NativeCallbackContracts.AddNativePrefix(heal, AccessTools.Method(typeof(RuneMirrors), nameof(ModifyHealing)), "HextechSolverCompat", Priority.Normal);
        NativeCallbackContracts.AddNativePostfix(heal, AccessTools.Method(typeof(RuneMirrors), nameof(AfterHealing)), "HextechSolverCompat", Priority.Normal);
        RegisterBase<FirstAidKitRune>();
        RegisterBase<StartupRoutineRune>();
        RegisterBase<MindToMatterRune>();
        RegisterBase<ThornmailRune>();
        RegisterBase<SwiftAndSafeRune>();
        RegisterBase<OverflowRune>();
        RegisterSustain();
        RegisterDamage();
        RegisterOpeningEffects();
        RegisterCardAndOrbEffects();
        RegisterAcquisitionOnly();
        RuneProjectionMirrors.Register(harmony);
        RegisterBase<NimbleRune>();
        RegisterBase<ZealotRune>();
        RegisterBase<BrutalForceRune>();
        RegisterBase<VitalitySurgeRune>();
        RegisterBase<HubrisRune>();
        RegisterBase<ShrinkEngineRune>();
        RegisterBase<SpeedsterRune>();
        RegisterBase<LoopRune>();
        RegisterBase<EurekaRune>();
        RegisterBase<InfiniteLoopRune>();

        ModelPredictionStateMirrors.RegisterRelic<SwiftAndSafeRune, DrawCountState>(
            "swift-and-safe-draws-v1", (_, relic) => new(relic.SavedCardsDrawnThisCombat),
            (SwiftAndSafeRune relic, ref ModelPredictionStateWriter writer) => writer.Add("drawn", relic.SavedCardsDrawnThisCombat),
            DrawCountState.Write);
        AfterCardDrawnMirrors.Registry.Register<SwiftAndSafeRune>((relic, c) =>
        {
            if (c.InitialCard.Owner != relic.Owner) return;
            var state = ModelPredictionStateMirrors.Get<DrawCountState>(c.Simulator, relic);
            state.Count++;
            if (state.Count % 10 == 0 && c.State.GetCreature(relic.Owner.Creature).IsAlive)
                ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(ArtifactPower),
                    relic.Owner.Creature, relic.DynamicVars["ArtifactPower"].IntValue, relic.Owner.Creature, null);
        });
        // Both callbacks are network-history reconciliation only. Multiplayer is
        // rejected before a root is captured; single-player increments above.
        AfterCardPlayedMirrors.LateRegistry.RegisterIgnored<SwiftAndSafeRune>();
        AfterPlayerTurnStartMirrors.RegisterLate<SwiftAndSafeRune>((_, _) => { });
        ModifyBlockMultiplicativeMirrors.Registry.Register<FirstAidKitRune>((r, c) => c.Target == r.Owner.Creature ? 1.25m : 1m);
        ModifyBlockMultiplicativeMirrors.Registry.Register<OverflowRune>((r, c) => c.Target == r.Owner.Creature ? 2m : 1m);
        ModifyEnergyCostInCombatMirrors.Registry.Register<OverflowRune>((r, c) =>
            c.Card.Preview.Owner == r.Owner && c.Card.GetPile(c.State)?.Type == PileType.Hand && !c.Card.Preview.EnergyCost.CostsX
                ? c.Cost + 1m : c.Cost);
        ModifyDamageMirrors.MultiplicativeRegistry.Register<OverflowRune>((r, c) =>
            (c.Target == null || c.Target.Side == CombatSide.Enemy) &&
            (c.Dealer == r.Owner.Creature || c.Dealer?.PetOwner == r.Owner ||
                (c.Dealer?.Side != CombatSide.Player && c.CardSource?.Preview.Owner == r.Owner))
                ? 2m : 1m);
    }

    private static bool ModifyHealing(CombatPredictionSimulator __instance, Creature creature, ref decimal amount, out int __state)
    {
        __state = __instance.State.GetCreature(creature).CurrentHp;
        if (!NativeRuneBridge.NativeNearHeal(__instance, creature)) return false;
        if (__instance.State.CombatState is not SimulatedCombatState combat) return true;
        if (creature.Side == CombatSide.Enemy)
            return ModifierMirrors.ModifyEnemyHeal(__instance, combat, creature, ref amount);
        if (creature.Player is not { } player || creature != player.Creature) return true;
        var relics = combat.RelicsOf(player);
        amount *= NativeRuneBridge.NativeForgeHealing(__instance, player);
        if (relics.OfType<AllForYouRune>().FirstOrDefault() is { } allForYou)
            amount *= NativeRuneBridge.NativeTeamHealing(allForYou, __instance, player);
        if (relics.OfType<OverflowRune>().Any()) amount *= 2m;
        if (relics.OfType<FirstAidKitRune>().Any()) amount *= 1.25m;
        if (relics.OfType<BackToBasicsRune>().Any()) amount *= 1.4m;
        if (relics.OfType<GoliathRune>().Any()) amount *= 1.2m;
        if (relics.OfType<SacrificeRune>().FirstOrDefault() is { } sacrifice)
            amount *= NativeRuneBridge.Invoke(sacrifice, __instance, model => model.SustainMultiplier);
        if (relics.OfType<NineDragonPowerRune>().FirstOrDefault() is { } nineDragon)
            amount *= NativeRuneBridge.Invoke(nineDragon, __instance, model => model.SustainMultiplier);
        var target = __instance.State.GetCreature(creature);
        if (relics.OfType<ProteinShakeRune>().FirstOrDefault() is { } protein)
            amount *= ProteinMultiplier(protein, target.MaxHp);
        if (relics.OfType<MoreTheMerrierRune>().FirstOrDefault() is { } merrier)
            amount *= MoreMultiplier(merrier, combat);
        if (relics.OfType<GoldenSpatulaRune>().FirstOrDefault() is { } spatula)
            amount *= NativeRuneBridge.Invoke(spatula, __instance, model => model.SustainMultiplier);
        if (relics.OfType<AnthonyBiasRune>().FirstOrDefault() is { } anthony)
            amount *= NativeRuneBridge.Invoke(anthony, __instance, model => model.SustainMultiplier);
        if (relics.OfType<GlassCannonRune>().FirstOrDefault() is { } glass)
            amount = Math.Min(amount, Math.Max(0m, Math.Floor(target.MaxHp * glass.HealCapPercent) - target.CurrentHp));
        return amount > 0m;
    }

    private static void AfterHealing(CombatPredictionSimulator __instance, Creature creature, int __state)
        => NativeRuneBridge.NativeCircleHealed(__instance, creature, __state);

    internal static void RegisterNativeBase<T>(Action<T, BeforeSideTurnStartMirrorContext>? beforeTurn = null,
        Action<T, AfterModifyingCardPlayResultLocationMirrorContext>? afterResult = null,
        Action<T, BeforeSideTurnEndMirrorContext>? beforeEnd = null)
        where T : HextechRelicBase => RegisterBase(beforeTurn, afterResult, beforeEnd);

    private static void RegisterBase<T>(Action<T, BeforeSideTurnStartMirrorContext>? beforeTurn = null,
        Action<T, AfterModifyingCardPlayResultLocationMirrorContext>? afterResult = null,
        Action<T, BeforeSideTurnEndMirrorContext>? beforeEnd = null) where T : HextechRelicBase
    {
        // HextechRelicBase's compatibility wrappers override native hooks even
        // for inert runes. Only these reviewed exact types are registered.
        if (beforeTurn is null) RequireInheritedWrapper<T>("BeforeSideTurnStart", 3);
        if (beforeEnd is null) RequireInheritedWrapper<T>("BeforeTurnEnd", 2);
        if (afterResult is null) RequireInheritedWrapper<T>("AfterModifyingCardPlayResultPileOrPositionCompat", 3);
        BeforeSideTurnStartMirrors.Register<T>(beforeTurn ?? ((_, _) => { }));
        if (beforeEnd is null) BeforeSideTurnEndMirrors.Registry.RegisterIgnored<T>();
        else BeforeSideTurnEndMirrors.Registry.Register<T>(beforeEnd);
        if (afterResult is null) ModifyCardPlayResultLocationMirrors.AfterRegistry.RegisterIgnored<T>();
        else ModifyCardPlayResultLocationMirrors.AfterRegistry.Register<T>(afterResult);
        CompatibilityGuard.Runes.Add(typeof(T));
    }

    private static void RequireInheritedWrapper<T>(string method, int parameters) where T : HextechRelicBase
    {
        var implementation = typeof(T).GetMethods().Single(m => m.Name == method && m.GetParameters().Length == parameters);
        if (implementation.DeclaringType != typeof(HextechRelicBase))
            throw new InvalidOperationException($"{typeof(T).Name}.{method} has its own implementation; an empty wrapper mirror would drop its effect.");
    }
}
