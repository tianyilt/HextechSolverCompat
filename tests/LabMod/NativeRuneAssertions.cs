using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyNativeOrbFork(UnattendedTestRunner.ScenarioContext scenario)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var relic = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(scenario.Player).OfType<HappyAccidentRune>().Single();
        var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.NativeRuneState", throwOnError: true)!;
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(type);
        HappyAccidentRune Model(CombatPredictionSimulator s) => (HappyAccidentRune)type.GetField("Model", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(get.Invoke(null, [s, relic]))!;
        var count = AccessTools.Field(typeof(HappyAccidentRune), "_statusOrbsThisCombat");
        int Count(CombatPredictionSimulator s) => (int)count.GetValue(Model(s))!;
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            scenario.Player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string stamp = Stamp(parent);
        int before = Count(parent);
        int statuses = child.State.GetPlayerCombatState(scenario.Player).AllPiles.Sum(p => p.Cards.Count(c => c.Preview.Type == CardType.Status));
        if (statuses <= 0) throw new Exception("Native orb probe requires actual status cards.");
        AfterPlayerTurnStartMirrors.Invoke(relic, new AfterPlayerTurnStartMirrorContext {
            Simulator = child, Player = scenario.Player, Choices = new TurnStartChoiceCursor(null)
        }, 1);
        if (ReferenceEquals(Model(parent), Model(child)) || Count(child) != before + statuses ||
            Count(parent) != before || Count(sibling) != before || Stamp(child) == stamp ||
            Stamp(parent) != stamp || Stamp(sibling) != stamp ||
            ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Native orb callback leaked its ordinal/orb queue or omitted continuation state.");
        GD.Print("HEXTECH_NATIVE_ORB_FORK_VERIFIED ordinal=true orb_queue=true parent=true sibling=true continuation=true live_rng_unchanged=true");
    }

    private static void VerifyJudicatorBranch(UnattendedTestRunner.ScenarioContext scenario)
    {
        var live = scenario.CombatState;
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var parent = CombatRootSnapshot.Capture(live).ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var player = scenario.Player.Creature;
        var enemy = live.Enemies.Single();
        var modifier = ((SimulatedCombatState)parent.State.CombatState).Modifiers.OfType<HextechMayhemModifier>().Single();
        int tier = modifier.GetMonsterHexStrengthTier(MonsterHexKind.Judicator);
        parent.State.GetCreature(player).CurrentHp = 40;
        sibling.State.GetCreature(player).CurrentHp = 40;
        child.State.GetCreature(player).CurrentHp = 39;
        decimal Multiplier(CombatPredictionSimulator simulator) => ModifyDamageMirrors.InvokeMultiplicative(modifier,
            new ModifyDamageMirrorContext { Simulator = simulator, Target = player, Dealer = enemy,
                Amount = 10m, Props = ValueProp.Move, CardSource = null, CardPlay = null });
        decimal expected = 1m + (tier <= 1 ? .10m : tier == 2 ? .20m : .30m);
        if (Multiplier(parent) != 1m || Multiplier(sibling) != 1m || Multiplier(child) != expected)
            throw new Exception("Judicator threshold/tier used the wrong branch.");
        var actualModifier = live.Modifiers.OfType<HextechMayhemModifier>().Single();
        int floor = actualModifier.SavedMonsterHexStrengthTierFloor;
        try
        {
            actualModifier.SavedMonsterHexStrengthTierFloor = tier == 3 ? 1 : 3;
            if (Multiplier(child) != expected) throw new Exception("Judicator re-read a changed live strength tier.");
        }
        finally { actualModifier.SavedMonsterHexStrengthTierFloor = floor; }
        if (ContinuationStamp.CaptureLive(live).StateText != before)
            throw new Exception("Judicator probe changed live combat.");
        GD.Print($"HEXTECH_JUDICATOR_BRANCH_VERIFIED tier={tier} parent=1 sibling=1 child={expected} live_unchanged=true");
    }

    private static void VerifyNativePowerQuery(UnattendedTestRunner.ScenarioContext scenario)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var parent = CombatRootSnapshot.Capture(scenario.CombatState).ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var enemy = scenario.CombatState.Enemies.Single();
        var relic = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(scenario.Player).OfType<VenomousBladeRune>().Single();
        ((SimulatedCombatState)child.State.CombatState).SetAmount<PoisonPower>(enemy, 8);
        decimal Bonus(CombatPredictionSimulator simulator) => ModifyDamageMirrors.InvokeAdditive(relic,
            new ModifyDamageMirrorContext {
                Simulator = simulator, Target = enemy, Dealer = scenario.Player.Creature,
                Amount = 4m, Props = ValueProp.Move, CardPlay = null,
                CardSource = simulator.State.GetPlayerCombatState(scenario.Player).Hand.Cards.Single(c => c.Preview is MegaCrit.Sts2.Core.Models.Cards.Shiv)
            });
        if (Bonus(child) != 8 || Bonus(parent) != 0 || Bonus(sibling) != 0 ||
            ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Native power query used a live or sibling power instead of the selected branch.");
        GD.Print("HEXTECH_NATIVE_POWER_QUERY_VERIFIED child=8 parent=0 sibling=0 live_unchanged=true");
    }

    private static void VerifyNativeRuneFork(UnattendedTestRunner.ScenarioContext scenario)
    {
        string liveBefore = ContinuationStamp.CaptureLive(scenario.CombatState).StateText;
        var root = CombatRootSnapshot.Capture(scenario.CombatState);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var relic = ((SimulatedCombatState)parent.State.CombatState).RelicsOf(scenario.Player).OfType<ArchmageRune>().Single();
        var stateType = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechSolverCompat")
            .GetType("HextechSolverCompat.NativeRuneState", throwOnError: true)!;
        var get = typeof(ModelPredictionStateMirrors).GetMethod("Get")!.MakeGenericMethod(stateType);
        ArchmageRune Model(CombatPredictionSimulator s) => (ArchmageRune)stateType.GetField("Model", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(get.Invoke(null, [s, relic]))!;
        FieldInfo rolls = AccessTools.Field(typeof(ArchmageRune), "_freeCardRollsThisCombat");
        int Count(CombatPredictionSimulator s) => (int)rolls.GetValue(Model(s))!;
        StateFingerprint Key(CombatPredictionSimulator s)
        {
            StateFingerprintBuilder writer = new();
            ((SimulatedCombatState)s.State.CombatState).AppendFingerprint(ref writer, s);
            return writer.Finish();
        }
        string Stamp(CombatPredictionSimulator s) => ContinuationStamp.CapturePredicted(
            scenario.Player, s, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        var key = Key(parent);
        string stamp = Stamp(parent);
        int before = Count(parent);
        if (ReferenceEquals(Model(parent), Model(child)) || ReferenceEquals(Model(child), Model(sibling)))
            throw new Exception("Native rune branches shared a mutable relic.");
        var card = child.State.GetPlayerCombatState(scenario.Player).Hand.Cards.First(c => c.Preview.Type == CardType.Skill);
        for (int i = 0; i < 20; i++)
        {
            var play = new CardPlay { Card = card.MutablePreview, Player = scenario.Player, Target = null,
                ResultPile = PileType.Discard, Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            AfterCardPlayedMirrors.Invoke(relic, new AfterCardPlayedMirrorContext { Simulator = child, Card = card, CardPlay = play });
        }
        if (Count(child) != before + 20 || Count(parent) != before || Count(sibling) != before ||
            Key(child) == key || Stamp(child) == stamp || Key(parent) != key || Stamp(parent) != stamp ||
            Key(sibling) != key || Stamp(sibling) != stamp || ContinuationStamp.CaptureLive(scenario.CombatState).StateText != liveBefore)
            throw new Exception("Native rune callback leaked state or omitted its fingerprint/continuation.");
        if (!child.State.GetPlayerCombatState(scenario.Player).Hand.Cards.Any(c =>
                c.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0) ||
            parent.State.GetPlayerCombatState(scenario.Player).Hand.Cards.Any(c =>
                c.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0) ||
            sibling.State.GetPlayerCombatState(scenario.Player).Hand.Cards.Any(c =>
                c.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0))
            throw new Exception("Native card cost mutation did not stay in the child branch.");
        var live = scenario.Player.Relics.OfType<ArchmageRune>().Single();
        int saved = (int)rolls.GetValue(live)!;
        try
        {
            rolls.SetValue(live, saved + 100);
            if (Key(parent) != key || Stamp(parent) != stamp || Count(parent) != before)
                throw new Exception("Frozen native rune state read the live relic again.");
        }
        finally { rolls.SetValue(live, saved); }
        GD.Print("HEXTECH_NATIVE_RUNE_FORK_VERIFIED ordinal=true card_cost=true parent=true sibling=true key=true continuation=true live_rng_unchanged=true");
    }
}
