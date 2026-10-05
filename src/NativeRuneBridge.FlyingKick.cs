using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterFlyingKick(Harmony harmony)
    {
        var trigger = AccessTools.DeclaredMethod(typeof(FlyingKickRune), "TriggerFlyingKick");
        var machine = trigger.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("FlyingKick callback shape changed.");
        var moveNext = AccessTools.Method(machine, "MoveNext");
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteFlyingKick)));
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteHeal)));
        RegisterNativeDamageHook<FlyingKickRune>();
    }

    private static IEnumerable<CodeInstruction> RewriteFlyingKick(IEnumerable<CodeInstruction> instructions)
    {
        var calls = new Dictionary<MethodInfo, string>
        {
            [AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Kill), [typeof(Creature), typeof(bool)])] = nameof(KillNative),
            [AccessTools.Method(typeof(HextechCombatVfx), "FlyingKickStrike")] = nameof(FlyingKickStrike),
            [AccessTools.Method(typeof(FlyingKickCorpseLaunchDriver), "MarkPending")] = nameof(FlyingKickMark),
            [AccessTools.Method(typeof(FlyingKickCorpseLaunchDriver), "MarkPendingUntilConsumed")] = nameof(FlyingKickMarkConsumed),
            [AccessTools.Method(typeof(FlyingKickCorpseLaunchDriver), "ClearPending")] = nameof(FlyingKickClear),
            [AccessTools.Method(typeof(CollectorRune), "IsCreditableDeath")] = nameof(FlyingKickCreditable),
            [AccessTools.Method(typeof(HextechMonsterInteractionPolicy), "IsTrueCombatDeath", [typeof(Creature)])] = nameof(FlyingKickTrueDeath),
            [typeof(Player).GetMethods().Single(m => m.Name == nameof(Player.GetRelic) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(CollectorRune))] = nameof(FlyingKickCollector)
        };
        var counts = calls.Keys.ToDictionary(m => m, _ => 0);
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && calls.TryGetValue(method, out string? replacement))
            {
                counts[method]++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), replacement);
            }
            yield return instruction;
        }
        foreach (var (method, count) in counts)
            if (count != 1) throw new InvalidOperationException($"FlyingKick {method.Name} call count changed: {count}.");
    }

    private static Task KillNative(Creature target, bool force)
    {
        if (_simulator is null) return CreatureCmd.Kill(target, force);
        bool completed = _simulator.Kill(target, force);
        PauseNativeChoice(_simulator);
        if (!completed) throw new PredictionUnsupportedException("Native execution could not complete in the current branch.");
        return Task.CompletedTask;
    }

    private static bool FlyingKickTrueDeath(Creature target)
        => _simulator is null ? HextechMonsterInteractionPolicy.IsTrueCombatDeath(target)
            : _simulator.State.Enemies.Contains(target)
                && ((ICombatPredictionCreatureSemantics)_simulator.State.CombatState).ShouldRemoveAfterDeath(target);
    private static bool FlyingKickCreditable(Creature target)
        => _simulator is null ? CollectorRune.IsCreditableDeath(target)
            : !_simulator.State.Enemies.Contains(target) || FlyingKickTrueDeath(target);
    private static CollectorRune? FlyingKickCollector(Player player)
    {
        if (_simulator is null) return player.GetRelic<CollectorRune>();
        var relic = ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(player).OfType<CollectorRune>().SingleOrDefault();
        return relic is null ? null : (CollectorRune)ModelPredictionStateMirrors.Get<NativeRuneState>(_simulator, relic).Model;
    }
    private static void FlyingKickStrike(Creature target, Creature owner)
    { if (_simulator is null) HextechCombatVfx.FlyingKickStrike(target, owner); }
    private static void FlyingKickMark(Creature target)
    { if (_simulator is null) FlyingKickCorpseLaunchDriver.MarkPending(target); }
    private static void FlyingKickMarkConsumed(Creature target)
    { if (_simulator is null) FlyingKickCorpseLaunchDriver.MarkPendingUntilConsumed(target); }
    private static void FlyingKickClear(Creature target)
    { if (_simulator is null) FlyingKickCorpseLaunchDriver.ClearPending(target); }
}
