using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static int _ownedInstantQueueCalls;
    private static readonly List<decimal> OwnedInstantQueueHps = [];
    private static readonly List<decimal> ActualInstantQueueHps = [];
    private static bool _instantProbeInstalled;
    private static void InstallNativeInstantProbe()
    {
        if (_instantProbeInstalled) return;
        var bridge = AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge")
            ?? throw new Exception("Native adapter was not loaded before the InstantDeath probe.");
        new Harmony("HextechCompatLab.InstantQueueObserver").Patch(
            AccessTools.DeclaredMethod(bridge, "QueueNativeInstantDoom"),
            prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveOwnedInstantQueue)));
        _instantProbeInstalled = true;
    }
    private static void ObserveOwnedInstantQueue(Creature creature)
    {
        var simulator = AccessTools.Field(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "_simulator").GetValue(null) as CombatPredictionSimulator;
        if (simulator is not null)
        {
            _ownedInstantQueueCalls++;
            OwnedInstantQueueHps.Add(simulator.State.GetCreature(creature).CurrentHp);
        }
        else ActualInstantQueueHps.Add(creature.CurrentHp);
    }
    private static void AssertNativeInstantGlobalQueueEmpty()
    {
        if (((List<Creature>)AccessTools.Field(typeof(HextechCombatHooks), "PendingInstantDeathDoomKills").GetValue(null)!).Count != 0)
            throw new Exception("Prediction changed the native global deferred Doom queue or an action did not finish.");
    }
    private static void AssertNativeInstantAfterPlay(CombatPredictionSimulator simulator, CombatState actual, Creature target)
    {
        using var request = Request();
        if (request.RootElement.TryGetProperty("hextechNativeInstantExpectedAlive", out var expected)
            && (target.IsAlive != expected.GetBoolean() || simulator.State.GetCreature(target).IsAlive != expected.GetBoolean()))
            throw new Exception("Native InstantDeath did not exercise its requested strict threshold.");
        bool deferred = request.RootElement.TryGetProperty("hextechNativeInstantDeferredProbe", out var flag) && flag.GetBoolean();
        if (deferred && _ownedInstantQueueCalls == 0) throw new Exception("Native InstantDeath branch did not defer inside the response guard.");
        if (!OwnedInstantQueueHps.SequenceEqual(ActualInstantQueueHps))
            throw new Exception($"InstantDeath queue timing differs: predicted=[{string.Join(',', OwnedInstantQueueHps)}] native=[{string.Join(',', ActualInstantQueueHps)}].");
        AssertNativeInstantGlobalQueueEmpty();
        var modifier = simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single();
        var stateType = AccessTools.TypeByName("HextechSolverCompat.EnemyState");
        var state = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType).Invoke(null, [simulator, modifier])!;
        if (((List<Creature>)AccessTools.Field(stateType, "PendingInstantDoom").GetValue(state)!).Count != 0)
            throw new Exception("Owned InstantDeath queue survived an action boundary.");
        GD.Print($"HEXTECH_NATIVE_INSTANT_DEATH_VERIFIED alive={target.IsAlive} queued_calls={_ownedInstantQueueCalls} queue_hps=[{string.Join(',', OwnedInstantQueueHps)}] deferred_required={deferred} owned_and_native_queues_empty=true");
    }
}
