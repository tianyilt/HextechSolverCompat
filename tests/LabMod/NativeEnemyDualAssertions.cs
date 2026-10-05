using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands.Builders;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static ConditionalWeakTable<AttackCommand, object> NativeDualCommands()
        => (ConditionalWeakTable<AttackCommand, object>)AccessTools.Field(typeof(HextechCombatHooks), "DualWieldProcessedCommands").GetValue(null)!;
    private static AttackCommand[]? CaptureNativeDualCommands()
    {
        using var request = Request();
        return request.RootElement.TryGetProperty("hextechNativeEnemyDualProbe", out var flag) && flag.GetBoolean()
            ? NativeDualCommands().Select(pair => pair.Key).ToArray() : null;
    }
    private static void AssertNativeDualCommandsUnchanged(AttackCommand[]? before)
    {
        if (before is null) return;
        if (!new HashSet<AttackCommand>(before).SetEquals(NativeDualCommands().Select(pair => pair.Key)))
            throw new Exception("DualWield prediction changed the original game's processed-command table.");
        GC.KeepAlive(before);
    }
    private static void VerifyOwnedDualForecast(CombatPredictionSimulator simulator)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var move = combat.CurrentMonsterMove(combat.Enemies.Single());
        int rawHits = move.Move.Intents.OfType<MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent>()
            .Sum(intent => Math.Max(1, intent.Repeats));
        if (move.AttackHits.Count != rawHits * 2)
            throw new Exception("Captured DualWield did not double the original monster attack segments.");
        GD.Print($"HEXTECH_NATIVE_ENEMY_DUAL_VERIFIED source_hits={rawHits} predicted_hits={move.AttackHits.Count} native_command_table_unchanged=true native_round_snapshots_required=true");
    }
}

