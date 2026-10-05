using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeScalar(UnattendedTestRunner.ScenarioContext scenario, JsonElement request)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var live = player.Relics.Single(r => r.Id.Entry == request.GetProperty("relicId").GetString());
        var field = AccessTools.Field(live.GetType(), request.GetProperty("field").GetString());
        if (field.FieldType != typeof(int) && field.FieldType != typeof(bool))
            throw new Exception("Scalar probe requires an int or bool field.");
        object Changed(object value) => field.FieldType == typeof(bool) ? !(bool)value : (int)value + 1;
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var clone = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(player).Single(r => r.Id == live.Id);
        var type = AccessTools.TypeByName("HextechSolverCompat.NativeRuneState");
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type);
        RelicModel Model(CombatPredictionSimulator sim) => (RelicModel)AccessTools.Field(type, "Model")
            .GetValue(get.Invoke(null, [sim, clone]))!;
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent), liveStamp = ContinuationStamp.CaptureLive(combat).StateText;
        object before = field.GetValue(Model(parent))!;
        if (ReferenceEquals(Model(parent), Model(child)) || ReferenceEquals(Model(child), Model(sibling)))
            throw new Exception("Native scalar state shared model identities.");
        field.SetValue(Model(child), Changed(before));
        if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Key(sibling) != key
            || Stamp(parent) != stamp || Stamp(sibling) != stamp || !field.GetValue(Model(parent))!.Equals(before))
            throw new Exception("Native scalar omitted full key/continuation or leaked to other branches.");
        object saved = field.GetValue(live)!;
        try
        {
            field.SetValue(live, Changed(saved));
            if (Key(parent) != key || Stamp(parent) != stamp || !field.GetValue(Model(parent))!.Equals(before)
                || ContinuationStamp.CaptureLive(combat).StateText == liveStamp)
                throw new Exception("Native scalar root was not frozen or live continuation omitted the field.");
        }
        finally { field.SetValue(live, saved); }
        if (ContinuationStamp.CaptureLive(combat).StateText != liveStamp)
            throw new Exception("Scalar probe changed the live combat.");
        GD.Print("HEXTECH_NATIVE_SCALAR_VERIFIED full_key=true continuation=true parent=true sibling=true live_frozen=true");
    }
}
