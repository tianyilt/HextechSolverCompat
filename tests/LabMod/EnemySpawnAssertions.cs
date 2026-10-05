using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemySpawn(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var source = combat.Enemies.First();
        var modifier = combat.RunState.Modifiers.OfType<HextechMayhemModifier>().Single();
        using var request = Request();
        bool furMarked = request.RootElement.TryGetProperty("hextechSpawnFurCoat", out var furFlag) && furFlag.GetBoolean();
        FurCoat? fur = null;
        if (furMarked)
        {
            fur = player.Relics.OfType<FurCoat>().Single();
            if (player.RunState.CurrentMapPoint is null)
            {
                await RunManager.Instance.GenerateMap();
                var point = player.RunState.Map.GetAllMapPoints().First(p =>
                    p.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Monster);
                ((RunState)player.RunState).AddVisitedMapCoord(point.coord);
            }
            var coord = player.RunState.CurrentMapPoint?.coord ?? throw new Exception("FurCoat fixture has no map point.");
            fur.FurCoatActIndex = player.RunState.CurrentActIndex;
            fur.FurCoatCoordCols = [coord.col];
            fur.FurCoatCoordRows = [coord.row];
            fur.FurCoatCoordsSet = true;
        }
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        StateFingerprint Key(CombatPredictionSimulator sim) => driver.BuildStateKey(root.StartTurnNumber,
            sim.State.GetCreature(player.Creature), sim.State.GetPlayerCombatState(player),
            (SimulatedCombatState)sim.State.CombatState, sim, 0, new HashSet<uint>());
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent), live = ContinuationStamp.CaptureLive(combat).StateText;
        var shadow = (SimulatedCombatState)child.State.CombatState;
        string? Slot(SimulatedCombatState state)
        {
            string? slot = MonsterSpawnSupport.NextSlot(state);
            return string.IsNullOrWhiteSpace(slot) ? null : slot;
        }
        int tier = modifier.SavedMonsterHexStrengthTierFloor;
        var rootFur = shadow.RelicsOf(player).OfType<FurCoat>().SingleOrDefault();
        if (fur is not null && (rootFur is null || ReferenceEquals(fur.FurCoatCoordCols, rootFur.FurCoatCoordCols)
            || ReferenceEquals(fur.FurCoatCoordRows, rootFur.FurCoatCoordRows)))
            throw new Exception("FurCoat root retained live coordinate arrays.");
        MegaCrit.Sts2.Core.Entities.Creatures.Creature predicted;
        try
        {
            modifier.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
            if (fur is not null) { fur.FurCoatCoordCols[0] += 1000; fur.FurCoatCoordRows[0] += 1000; }
            predicted = MonsterSpawnSupport.Spawn<GasBomb>(child, shadow, source, Slot(shadow));
        }
        finally
        {
            modifier.SavedMonsterHexStrengthTierFloor = tier;
            if (fur is not null) { fur.FurCoatCoordCols[0] -= 1000; fur.FurCoatCoordRows[0] -= 1000; }
        }
        PowerLifecycleSupport.ResolvePowerAmountChanges(child, shadow);
        if (Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Stamp(parent) != stamp
            || Key(sibling) != key || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(combat).StateText != live)
            throw new Exception("Enemy spawn changed the parent/sibling/live state or omitted branch tracking.");
        var siblingCombat = (SimulatedCombatState)sibling.State.CombatState;
        _ = MonsterSpawnSupport.Spawn<GasBomb>(sibling, siblingCombat, source, Slot(siblingCombat));
        PowerLifecycleSupport.ResolvePowerAmountChanges(sibling, siblingCombat);
        if (Key(sibling) != Key(child) || Stamp(sibling) != Stamp(child))
            throw new Exception("Enemy spawn RNG or frozen tier differed between sibling branches.");
        // An HP marker is meaningful future state even when creature values do
        // not change. Mutating it must affect both search and continuation.
        var stateType = AccessTools.TypeByName("HextechSolverCompat.EnemyState");
        var getState = typeof(ModelPredictionStateMirrors).GetMethods(System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "Get" && method.IsGenericMethodDefinition);
        uint id = predicted.CombatId!.Value;
        foreach (string field in new[] { "Base", "Projected", "Applied", "LegacyTankStacks" })
        {
            var probe = child.Fork();
            var probeCombat = (SimulatedCombatState)probe.State.CombatState;
            var probeModifier = probeCombat.Modifiers.OfType<HextechMayhemModifier>().Single();
            var state = getState.MakeGenericMethod(stateType).Invoke(null, [probe, probeModifier])!;
            var hp = AccessTools.Field(stateType, "Hp").GetValue(state)!;
            if (field == "Applied")
                ((Dictionary<MonsterHexKind, HashSet<uint>>)AccessTools.Field(hp.GetType(), field).GetValue(hp)!)[MonsterHexKind.Goliath]
                    .SymmetricExceptWith([id]);
            else
            {
                var values = (Dictionary<uint, int>)AccessTools.Field(hp.GetType(), field).GetValue(hp)!;
                values[id] = values.GetValueOrDefault(id) + 1;
            }
            if (Key(probe) == Key(child) || Stamp(probe) == Stamp(child)
                || Key(parent) != key || Stamp(parent) != stamp || Key(sibling) != Key(child) || Stamp(sibling) != Stamp(child))
                throw new Exception($"Enemy HP {field} tracking is shared or missing from key/continuation.");
        }
        var slot = combat.Encounter!.GetNextSlot(combat);
        var actual = await CreatureCmd.Add<GasBomb>(combat, string.IsNullOrWhiteSpace(slot) ? null : slot);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (request.RootElement.TryGetProperty("hextechNativeServantSpawnProbe", out var servantSpawn) && servantSpawn.GetBoolean())
        {
            shadow.ApplyPowerFromSource(typeof(MinionPower), predicted, 1, source, null);
            PowerLifecycleSupport.ResolvePowerAmountChanges(child, shadow);
            await HextechPowerCmdCompat.Apply<MinionPower>(actual, 1, source, null);
            if (actual.Powers.OfType<IllusionPower>().SingleOrDefault()?.Amount != 1)
                throw new Exception("Native newborn Minion did not gain exactly one Servant Master Illusion.");
        }
        if (actual.CombatId != predicted.CombatId)
            throw new Exception("Native and predicted spawn allocated different combat IDs.");
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, predicted),
            UnattendedTestRunner.CaptureActual(combat, player, actual), "HextechEnemySpawn", "NativeAddHpAndRng");
        runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, source),
            UnattendedTestRunner.CaptureActual(combat, player, source), "HextechEnemySpawn", "ExistingEnemyUnchanged");
        decimal scale = 1m;
        foreach (var kind in new[] { MonsterHexKind.Goliath, MonsterHexKind.AstralBody, MonsterHexKind.Stats,
            MonsterHexKind.StatsOnStats, MonsterHexKind.StatsOnStatsOnStats,
            MonsterHexKind.GoldenSpatula, MonsterHexKind.MadScientist })
            if (modifier.HasActiveMonsterHex(kind)) scale *= 1m + (kind is MonsterHexKind.Goliath or MonsterHexKind.AstralBody
                ? tier <= 1 ? .20m : tier == 2 ? .30m : .40m
                : kind == MonsterHexKind.GoldenSpatula ? tier <= 1 ? .25m : tier == 2 ? .30m : .45m
                : kind == MonsterHexKind.MadScientist ? tier <= 1 ? -.30m : tier == 2 ? -.15m : 0m
                : EnemyAttributeBoostValues.GetBonusFraction(kind, tier));
        int expectedMax = (int)Math.Floor(actual.MonsterMaxHpBeforeModification!.Value * scale);
        int expectedCurrent = modifier.HasActiveMonsterHex(MonsterHexKind.GlassCannon)
            ? Math.Max(1, (int)Math.Floor(expectedMax * .7m)) : expectedMax;
        if (furMarked) expectedCurrent = 1;
        if (actual.MaxHp != expectedMax || actual.CurrentHp != expectedCurrent)
            throw new Exception($"Native spawned HP fixture not exercised: expected {expectedCurrent}/{expectedMax}, got {actual.CurrentHp}/{actual.MaxHp}.");
        if (actual.Block != 0 || actual.Powers.OfType<ThornsPower>().Any())
            throw new Exception("Spawn unexpectedly replayed the combat-start power/block effects.");
        if (modifier.HasActiveMonsterHex(MonsterHexKind.MadScientist)
            && actual.Powers.OfType<PersonalHivePower>().SingleOrDefault()?.Amount != 1)
            throw new Exception("Native spawned MadScientist did not apply its persistent PersonalHive power exactly once.");
        if (request.RootElement.TryGetProperty("hextechNativeNearSpawnProbe", out var nearSpawn) && nearSpawn.GetBoolean())
        {
            int loss = actual.CurrentHp;
            child.Damage(predicted, loss, ValueProp.Unblockable | ValueProp.Unpowered, player.Creature);
            await HextechGameApiCompat.Damage(new ThrowingPlayerChoiceContext(), actual, loss,
                ValueProp.Unblockable | ValueProp.Unpowered, player.Creature, null);
            child.SynchronizePowerAmountPredictionStates();
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, predicted),
                UnattendedTestRunner.CaptureActual(combat, player, actual), "HextechNearSpawn", "newborn-dying-debt-zero");
            if (actual.CurrentHp != 1 || modifier.CombatTracking.NearDeathFeastEnemyDebt.GetValueOrDefault(actual.CombatId!.Value, -1) != 0)
                throw new Exception("Newborn enemy did not enter native near-death tracking.");
            child.Heal(predicted, 20m); await CreatureCmd.Heal(actual, 20m);
            child.GainBlock(predicted, 10m, ValueProp.Unpowered); await CreatureCmd.GainBlock(actual, 10m, ValueProp.Unpowered, null);
            runner.AssertSnapshotEqual(UnattendedTestRunner.CaptureSimulated(child, shadow, player, predicted),
                UnattendedTestRunner.CaptureActual(combat, player, actual), "HextechNearSpawn", "newborn-no-heal-block");
            GD.Print("HEXTECH_NATIVE_NEAR_SPAWN_VERIFIED native_spawn=true new_binding=true zero_debt_alive=true no_heal_block=true");
        }
        if (furMarked) GD.Print("HEXTECH_FUR_COAT_SPAWN_VERIFIED hp=1 root_arrays_detached=true live_map_mutation_frozen=true");
        GD.Print($"HEXTECH_ENEMY_SPAWN_VERIFIED hp={actual.CurrentHp} max_hp={actual.MaxHp} native_add=true rng=true full_key=true continuation=true owned_tracking=true frozen_tier=true no_opening_replay=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
