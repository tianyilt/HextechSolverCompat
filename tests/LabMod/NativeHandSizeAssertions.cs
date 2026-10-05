using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

public class LabHandSizeSubscriber : AbstractModel
{
    public override bool ShouldReceiveCombatHooks => true;
    public int Limit;
}

internal static partial class FixtureAssertions
{
    private static LabHandSizeSubscriber? _handSizeSubscriber;
    private static CombatState? _handSizeCombat;
    private static Type? _handSizeSubscriberType;

    private static void InstallNativeHandSizeProbe(Harmony harmony)
    {
        ModHelper.SubscribeForCombatStateHooks("HextechCompatLab.NativeHandSize", combat =>
            ReferenceEquals(combat, _handSizeCombat) && _handSizeSubscriber is not null
                ? new AbstractModel[] { _handSizeSubscriber } : []);
        harmony.Patch(AccessTools.Method(typeof(UnattendedTestRunner.ScenarioBuilder), "InjectInitialStateAsync"),
            prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(PrepareNativeHandSizeProbe)));
    }

    private static void PrepareNativeHandSizeProbe(CombatState CombatState, Player player)
    {
        _handSizeSubscriber = null;
        _handSizeCombat = null;
        using var request = Request();
        if (!request.RootElement.TryGetProperty("hextechNativeMaxHandSize", out var requested)) return;
        int limit = requested.GetInt32();
        if (limit is < 1 or > 30) throw new Exception("Native hand-size test limit must be 1..30.");
        if (_handSizeSubscriberType is null)
        {
            // Implement the actual optional BaseLib interface, without adding
            // a hard dependency to the no-BaseLib test host. No native query is
            // patched, and this test subscriber is never installed daily.
            var contract = AccessTools.TypeByName("BaseLib.Hooks.IMaxHandSizeModifier")
                ?? throw new Exception("Native hand-size probe requires actual BaseLib.");
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("HextechCompatLab.NativeHandSize"), AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("NativeHandSize").DefineType("NativeBaseLibHandSizeSubscriber",
                TypeAttributes.Public, typeof(LabHandSizeSubscriber), [contract]);
            foreach (var method in contract.GetMethods())
            {
                var implementation = builder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                    method.ReturnType, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
                var il = implementation.GetILGenerator();
                if (method.Name == "ModifyMaxHandSize")
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, typeof(LabHandSizeSubscriber).GetField(nameof(LabHandSizeSubscriber.Limit))!);
                }
                else if (method.Name == "ModifyMaxHandSizeLate") il.Emit(OpCodes.Ldarg_2);
                else throw new Exception("BaseLib hand-size hook interface changed.");
                il.Emit(OpCodes.Ret);
                builder.DefineMethodOverride(implementation, method);
            }
            _handSizeSubscriberType = builder.CreateType()!;
        }
        _handSizeSubscriber = (LabHandSizeSubscriber)Activator.CreateInstance(_handSizeSubscriberType)!;
        _handSizeSubscriber.Limit = limit;
        _handSizeCombat = CombatState;
        if (ReadNativeMaxHandSize(player) != limit)
            throw new Exception("Actual BaseLib hook did not change native hand-size limit.");
        GD.Print($"HEXTECH_NATIVE_HAND_SIZE_PREPARED limit={limit} real_baselib_hook=true");
    }

    private static int ReadNativeMaxHandSize(Player player)
        => (int)AccessTools.Method(AccessTools.TypeByName("STS2RitsuLib.RitsuLibFramework"), "GetMaxHandSize", [typeof(Player)])
            .Invoke(null, [player])!;

    private static void VerifyNativeHandSizeRoot(CombatState combat, Player player, CombatRootSnapshot root,
        CombatPredictionSimulator simulator)
    {
        if (_handSizeSubscriber is null) return;
        int expected = _handSizeSubscriber.Limit;
        if (ReadNativeMaxHandSize(player) != expected || simulator.GetMaxHandSize(player) != expected)
            throw new Exception("Root did not capture the actual BaseLib hand-size hook value.");
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        var key = Key(simulator);
        string stamp = ContinuationStamp.CaptureLive(combat).StateText;
        string predictedStamp = ContinuationStamp.CapturePredicted(player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        if (stamp != predictedStamp) throw new Exception("Live and frozen predicted hand-size continuations differ before change.");
        _handSizeSubscriber.Limit = expected + 3;
        try
        {
            int native = ReadNativeMaxHandSize(player), frozen = simulator.GetMaxHandSize(player);
            int sibling = root.ForkSimulator().GetMaxHandSize(player);
            bool keyStable = Key(simulator) == key, stampChanged = ContinuationStamp.CaptureLive(combat).StateText != stamp;
            int fresh = CombatRootSnapshot.Capture(combat).ForkSimulator().GetMaxHandSize(player);
            var freshSimulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            GD.Print($"HEXTECH_NATIVE_HAND_SIZE_CHANGE native={native} frozen={frozen} sibling={sibling} new_root={fresh} key_stable={keyStable} live_stamp_changed={stampChanged}");
            if (native != expected + 3 || frozen != expected || sibling != expected || !keyStable || !stampChanged || fresh != expected + 3
                || Key(freshSimulator) == key
                || ContinuationStamp.CapturePredicted(player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText != predictedStamp)
                throw new Exception("Native hand-size change did not invalidate live state or leaked into old branches.");
        }
        finally { _handSizeSubscriber.Limit = expected; }
        if (ContinuationStamp.CaptureLive(combat).StateText != stamp)
            throw new Exception("Native hand-size hook restoration changed combat state.");
        GD.Print($"HEXTECH_NATIVE_HAND_SIZE_VERIFIED limit={expected} native=true root_frozen=true sibling_frozen=true new_root_updated=true live_stamp_changed=true restored=true");
    }
}
