using System.Globalization;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace HextechSolverCompat;

// Immutable run context. The original hash implementation receives only frozen
// inputs: a search branch never consults the mutable live run for a proc.
internal sealed record EnemyStableSeed(string Seed, int Act, int Floor)
{
    internal static EnemyStableSeed Capture(HextechMayhemModifier modifier)
    {
        var run = modifier.ActiveRunState
            ?? throw new PredictionUnsupportedException("Enemy repeat has no run seed to capture.");
        return new(run.Rng.StringSeed, run.CurrentActIndex, run.TotalFloor);
    }

    internal int Index(int count, params string?[] salt)
    {
        List<string?> parts = [Seed, "|act:", Act.ToString(), "|floor:", Floor.ToString()];
        foreach (var part in salt) { parts.Add("|"); parts.Add(part ?? ""); }
        return HextechStableRandom.IndexFromRawParts(count, parts.ToArray());
    }

    internal static void Write(EnemyStableSeed seed, ref ModelPredictionStateWriter writer)
    {
        writer.Add("enemyStableSeed", seed.Seed);
        writer.Add("enemyStableAct", seed.Act);
        writer.Add("enemyStableFloor", seed.Floor);
    }
}

internal static class EnemyMoveRepeatBridge
{
    [ThreadStatic] private static int _repeatDepth;

    internal static void Register(Harmony harmony)
    {
        if (!HextechCombatHooks.HasJeweledGauntletPrivateFieldContracts(
                AccessTools.Field(typeof(MoveState), "<Intents>k__BackingField"),
                AccessTools.Field(typeof(MonsterModel), "_isPerformingMove"),
                AccessTools.Field(typeof(KnowledgeDemon), "_curseOfKnowledgeCounter")))
            throw new PredictionUnsupportedException("Reviewed Jeweled Gauntlet native field contracts changed.");
        harmony.Patch(AccessTools.Method(typeof(MonsterMoveSemantics), nameof(MonsterMoveSemantics.ApplyForecastMove)),
            prefix: new HarmonyMethod(typeof(EnemyMoveRepeatBridge), nameof(BeforeMove)),
            postfix: new HarmonyMethod(typeof(EnemyMoveRepeatBridge), nameof(AfterMove)));
    }

    internal static bool ShouldRepeat(CombatPredictionSimulator simulator, SimulatedCombatState combat, ForecastMove move)
    {
        var creature = move.Owner;
        if (creature.CombatId is not uint id || creature.Monster is not { } monster)
            return false;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return false;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        if (!state.Has(MonsterHexKind.JeweledGauntlet)) return false;
        var current = move.Move;
        if (HextechCombatHooks.IsMonsterRevivalMove(current.Id)
            || (monster is TheInsatiable && HextechCombatHooks.IsTheInsatiableOpeningMove(current.Id))
            || (monster is KnowledgeDemon && HextechCombatHooks.WouldRepeatFinalKnowledgeDemonCurse(
                current.Id, combat.GetKnowledgeDemonCurseCounter(creature)))
            || !HextechCombatHooks.AreJeweledGauntletIntentsRepeatable(current.Intents)) return false;
        var seed = state.StableSeed
            ?? throw new PredictionUnsupportedException("Enemy repeat seed was not captured.");
        return seed.Index(100, "enemy-jeweled-gauntlet-repeat",
            id.ToString(CultureInfo.InvariantCulture), combat.RoundNumber.ToString(CultureInfo.InvariantCulture), current.Id)
            < HextechCombatHooks.GetJeweledGauntletRepeatPercent(state.StrengthTier);
    }

    private static void BeforeMove(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ForecastMove move, out bool __state)
    {
        // Choose once before the first move, including Knowledge Demon's counter.
        // Native repeats the captured delegate, never recursively performs a move.
        __state = _repeatDepth == 0 && ShouldRepeat(simulator, combat, move);
        if (__state)
        {
            // An inner command continuation does not include this outer repeat.
            // Keep explicit-choice support by replaying the whole planned action
            // from its parent. This invalidates only the continuation optimization.
            simulator.RejectExecutionContinuation();
        }
    }

    private static void AfterMove(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ForecastMove move, Creature player, ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices, bool __state, ref bool __result)
    {
        NativeRuneBridge.CompleteNativeMonsterUpgrade(simulator, combat, move);
        if (!__state || __result || simulator.HasPendingChoice
            || simulator.State.GetCreature(move.Owner).IsDead || !combat.ContainsCreature(move.Owner)
            || simulator.State.GetCreature(player).IsDead
            || !ReferenceEquals(combat.CurrentMonsterMove(move.Owner).Move, move.Move)) return;
        _repeatDepth++;
        try
        {
            // Re-evaluate dynamic damage/hits from the same captured MoveState,
            // e.g. TestSubject's growing multi-claw count. AI advances once later.
            __result = MonsterMoveSemantics.ApplyForecastMove(simulator, combat,
                combat.CurrentMonsterMove(move.Owner), player, processedEnemyDeaths, plannedChoices);
        }
        finally { _repeatDepth--; }
    }
}
