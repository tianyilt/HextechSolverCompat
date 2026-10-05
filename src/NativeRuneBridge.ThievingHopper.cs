using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly ThievingHopperEnemyHex NativeThief = new();
    private static readonly Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task> OriginalThievingReceived =
        AccessTools.DeclaredMethod(typeof(ThievingHopperEnemyHex), "AfterEnemyDamageReceived")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, uint, DamageResult, Creature?, CardModel?, Task>>(NativeThief);
    private static readonly Func<Creature, HextechEnemyHexContext, bool> OriginalCanPlanTheft =
        AccessTools.DeclaredMethod(typeof(ThievingHopperEnemyHex), "CanPlanTheft").CreateDelegate<Func<Creature, HextechEnemyHexContext, bool>>();
    private static readonly Func<Creature, bool> OriginalBelowTheft = AccessTools.DeclaredMethod(typeof(ThievingHopperEnemyHex),
        "IsBelowTheftThreshold", [typeof(Creature)]).CreateDelegate<Func<Creature, bool>>();
    private static readonly Func<IReadOnlyList<AbstractIntent>, Creature, int, bool, AbstractIntent[]> OriginalComposeUpgrades =
        AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "ComposeMonsterUpgradeIntents")
            .CreateDelegate<Func<IReadOnlyList<AbstractIntent>, Creature, int, bool, AbstractIntent[]>>();
    private static readonly Func<SimulatedCombatState, Creature, BranchMonsterAiState> ReadNativeMonsterAi =
        AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "GetMonsterAiState").CreateDelegate<Func<SimulatedCombatState, Creature, BranchMonsterAiState>>();
    private static readonly FieldInfo NativeMonsterAiTable = AccessTools.DeclaredField(typeof(SimulatedCombatState), "_monsterAiStates");
    private static readonly MethodInfo NativeMemberwiseClone = AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone");

    private static void RegisterNativeThievingHopper(Harmony harmony)
    {
        var type = typeof(ThievingHopperEnemyHex);
        var dead = AccessTools.PropertyGetter(typeof(Creature), "IsDead");
        var combat = AccessTools.PropertyGetter(typeof(Creature), "CombatState");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "CanPlanTheft"),
            Site(dead, nameof(NativeBranchIsDead)), Site(AccessTools.PropertyGetter(typeof(Creature), "IsStunned"), nameof(NativeThiefIsStunned)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "Powers"), nameof(NativeRemovalPowers)),
            Site(combat, nameof(NativeBranchCombat)), Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking)),
            Site(AccessTools.PropertyGetter(typeof(MonsterModel), "NextMove"), nameof(NativeThiefNextMove), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "IsBelowTheftThreshold", [typeof(Creature)]),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CurrentHp"), nameof(NativeEnemyCurrentHp)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "MaxHp"), nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterEnemyDamageReceived"),
            Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "PlanThievingHopperTheftNow"), nameof(PlanNativeTheftNow)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "StealAndPlanEscape"),
            Site(combat, nameof(NativeBranchCombat), 2), Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking)),
            Site(AccessTools.Method(typeof(CardPileCmd), "RemoveFromCombat", [typeof(CardModel), typeof(bool)]), nameof(RemoveNativeTheftCard)),
            Site(AccessTools.DeclaredMethod(typeof(SwipePower), "Steal"), nameof(StealNativePermanentCard)),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
                && !method.IsGenericMethodDefinition && method.GetParameters().Length == 6), nameof(ApplyNativeSwipe)),
            Site(AccessTools.PropertyGetter(typeof(MonsterModel), "MoveStateMachine"), nameof(NativeThiefMachine)),
            Site(AccessTools.DeclaredMethod(typeof(MonsterModel), "SetMoveImmediate", [typeof(MoveState), typeof(bool)]), nameof(SetNativeTheftMove)));
        var nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(AccessTools.GetDeclaredMethods).ToArray();
        PatchEventCallback(harmony, nested.Single(method => method.Name == "<StealAndPlanEscape>b__12_0"), Site(dead, nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, nested.Single(method => method.Name == "<StealAndPlanEscape>b__12_2"),
            Site(AccessTools.DeclaredMethod(typeof(CardPile), "GetCards", [typeof(Player), typeof(PileType[])]), nameof(NativeTheftPileCards)));
        PatchEventCallback(harmony, nested.Single(method => method.Name == "<StealAndPlanEscape>b__6"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "DeckVersion"), nameof(NativeGrowthDeckVersion), 2),
            Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeGrowthDeckCards)));
        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => !EventCallSites.ContainsKey(method)
            && method.Name is not "AfterEnemyDamageReceived" and not "StealAndPlanEscape" and not "Escape"))
            NativeCallbackContracts.Add(method);
        foreach (var method in nested.Where(method => !EventCallSites.ContainsKey(method))) NativeCallbackContracts.Add(method);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "Escape"));
        // Both the stock and Hextech theft paths rely on the same pinned
        // permanent-card transaction and native reward semantics.
        foreach (string name in new[] { "Steal", "BeforeDeath" })
        {
            var original = AccessTools.DeclaredMethod(typeof(SwipePower), name);
            NativeCallbackContracts.Add(original);
            var machine = original.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
            if (machine is not null) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(machine, "MoveNext"));
        }
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "PlanThievingHopperTheftNow"));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "PrepareMonsterMoveForNextRound"), nameof(PlanNativeTheftAfterRoll));
        AddThiefPrefix(harmony, AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "PrepareMonsterMoveForNextRound"), nameof(CaptureNativeTheftRoll));
        AddThiefPrefix(harmony, AccessTools.DeclaredMethod(typeof(MonsterMoveEffects), "ApplyBeforeAttack"), nameof(CaptureNativeStockTheft));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(MonsterMoveEffects), "ApplyBeforeAttack"), nameof(CompleteNativeStockTheft));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "ForceMonsterMove", [typeof(Creature), typeof(MoveState)]), nameof(SynchronizeNativeTheftMove));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(BranchMonsterAi), "Capture"), nameof(FreezeNativeTheftAi));
        AddThiefPrefix(harmony, AccessTools.DeclaredMethod(typeof(MonsterMoveEffects), "Apply"), nameof(ApplyNativeTheftEscape));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(MonsterMoveEffects), "RemovesOwner"), nameof(NativeTheftRemovesOwner));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(MonsterMoveEffects), "Supports"), nameof(NativeTheftSupports));
        AddThiefPostfix(harmony, AccessTools.DeclaredMethod(typeof(BeforeDeathMirrors), "Invoke"), nameof(NativeTheftBeforeDeath));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.ThievingHopper);
    }
    private static void AddThiefPrefix(Harmony harmony, MethodInfo target, string name)
    {
        var method = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), name);
        harmony.Patch(target, prefix: new HarmonyMethod(method));
        NativeCallbackContracts.AddNativePrefix(target, method, "HextechSolverCompat", Priority.Normal);
    }
    private static void AddThiefPostfix(Harmony harmony, MethodInfo target, string name)
    {
        var method = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), name);
        harmony.Patch(target, postfix: new HarmonyMethod(method));
        NativeCallbackContracts.AddNativePostfix(target, method, "HextechSolverCompat", Priority.Normal);
    }
    private static MoveState NativeThiefNextMove(MonsterModel monster)
        => _simulator is { } sim ? ((SimulatedCombatState)sim.State.CombatState).CurrentMonsterMove(monster.Creature).Move : monster.NextMove;
    private static bool NativeThiefIsStunned(Creature creature)
        => _simulator is { } sim ? creature.Monster is not null && ((SimulatedCombatState)sim.State.CombatState).GetPredictedMoveId(creature) == "STUNNED" : creature.IsStunned;
    private static void SetOwnedAi(SimulatedCombatState combat, Creature creature, BranchMonsterAiState state)
        => ((ForkableDictionary<Creature, BranchMonsterAiState>)NativeMonsterAiTable.GetValue(combat)!)[creature] = state;
    private static void FreezeNativeTheftAi(MonsterModel monster, ref BranchMonsterAiState __result)
    {
        bool needed = monster.Creature.CombatState is SimulatedCombatState shadow
            ? PowerSourceBridge.HasCapturedHex(shadow, MonsterHexKind.ThievingHopper)
            : monster.Creature.CombatState?.Modifiers.OfType<HextechMayhemModifier>().Any(modifier => modifier.HasActiveMonsterHex(MonsterHexKind.ThievingHopper)) == true;
        if (!needed) return;
        var source = __result;
        // A forced state may appear in the native log without being a catalogue
        // entry. Capture the actual objects here, before background search starts.
        var nativeLog = source.Machine.StateLog.ToArray();
        var remapped = source.Machine.States.Values.Concat(nativeLog).Append(source.Current).Distinct()
            .ToDictionary(state => state, state => (MonsterState)NativeMemberwiseClone.Invoke(state, null)!);
        foreach (var (original, copy) in remapped)
            if (original is MoveState move && copy is MoveState owned)
            {
                AccessTools.Field(typeof(MoveState), "<Intents>k__BackingField").SetValue(owned, move.Intents.ToArray());
                if (move.FollowUpState is { } follow && remapped.TryGetValue(follow, out var mapped)) owned.FollowUpState = mapped;
            }
        var machine = (MonsterMoveStateMachine)NativeMemberwiseClone.Invoke(source.Machine, null)!;
        AccessTools.Field(typeof(MonsterMoveStateMachine), "<States>k__BackingField").SetValue(machine,
            source.Machine.States.ToDictionary(pair => pair.Key, pair => remapped[pair.Value]));
        AccessTools.Field(typeof(MonsterMoveStateMachine), "<StateLog>k__BackingField").SetValue(machine,
            nativeLog.Select(state => remapped[state]).ToList());
        foreach (string fieldName in new[] { "_initialState", "_currentState" })
        {
            var field = AccessTools.Field(typeof(MonsterMoveStateMachine), fieldName);
            if (field.GetValue(machine) is MonsterState original && remapped.TryGetValue(original, out var copy)) field.SetValue(machine, copy);
        }
        __result = source with { Machine = machine, Current = (MoveState)remapped[source.Current] };
    }
    private static MonsterMoveStateMachine NativeThiefMachine(MonsterModel monster)
    {
        if (_simulator is not { } sim) return monster.MoveStateMachine!;
        var combat = (SimulatedCombatState)sim.State.CombatState;
        var state = ReadNativeMonsterAi(combat, monster.Creature);
        var copy = (MonsterMoveStateMachine)NativeMemberwiseClone.Invoke(state.Machine, null)!;
        AccessTools.Field(typeof(MonsterMoveStateMachine), "<States>k__BackingField").SetValue(copy, new Dictionary<string, MonsterState>(state.Machine.States));
        AccessTools.Field(typeof(MonsterMoveStateMachine), "<StateLog>k__BackingField").SetValue(copy, new List<MonsterState>(state.Machine.StateLog));
        SetOwnedAi(combat, monster.Creature, state with { Machine = copy });
        return copy;
    }
    private static void SetNativeTheftMove(MonsterModel monster, MoveState move, bool forceTransition)
    {
        if (_simulator is not { } sim) { monster.SetMoveImmediate(move, forceTransition); return; }
        if (!forceTransition || move.Id != ThievingHopperEnemyHex.EscapeMoveId)
            throw new PredictionUnsupportedException("Unreviewed native theft move transition.");
        ((SimulatedCombatState)sim.State.CombatState).ForceMonsterMove(monster.Creature, move);
        _nativeEnemyTurn!.State.Theft!.SetPlanned(monster.Creature.CombatId!.Value, false);
    }
    private static IEnumerable<CardModel> NativeTheftPileCards(Player player, PileType[] piles)
        => _simulator is { } sim ? piles.SelectMany(pile => pile switch {
                PileType.Draw => sim.State.GetPlayerCombatState(player).DrawPile.Cards,
                PileType.Discard => sim.State.GetPlayerCombatState(player).DiscardPile.Cards,
                _ => throw new PredictionUnsupportedException("Theft pile selection changed.") })
            .Select(card => card.MutablePreview).ToArray() : CardPile.GetCards(player, piles);
    private static Task RemoveNativeTheftCard(CardModel card, bool skipVisuals)
    {
        if (_simulator is { } sim) NativeSelfUpgradeState.Require(sim).BindCurrentCards();
        return RemoveNativeSolidCombatCard(card, skipVisuals);
    }
    private static Task StealNativePermanentCard(SwipePower power, CardModel card)
    {
        if (_simulator is not { } sim) return power.Steal(card);
        var permanent = NativeSelfUpgradeState.Require(sim).DeckVersion(card)
            ?? throw new PredictionUnsupportedException("Theft received a generated or foreign card.");
        NativeSelfUpgradeState.Require(sim).RemovePersistent(permanent);
        power._target = card.Owner.Creature;
        power.StolenCard = card;
        // This owned payload outlives removal from the branch's combat piles.
        card.DeckVersion = permanent;
        return Task.CompletedTask;
    }
    private static Task ApplyNativeSwipe(PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? source, bool silent)
    {
        if (_simulator is not { } sim) return HextechPowerCmdCompat.Apply(power, target, amount, applier, source, silent);
        if (power is not SwipePower { StolenCard.DeckVersion: { } permanent } swipe || amount != 1 || !ReferenceEquals(target, applier) || source is not null)
            throw new PredictionUnsupportedException("Native theft power application changed.");
        var combat = (SimulatedCombatState)sim.State.CombatState;
        combat.RecordStolenCard(sim);
        combat.BeginCardPowerApplication(null);
        try
        {
            combat.ApplyWithBeforeApplied<SwipePower>(target, 1, applier, null, (_, model) =>
            {
                var applied = (SwipePower)model;
                applied._target = swipe.Target;
                applied.StolenCard = swipe.StolenCard;
            });
        }
        finally { combat.CompleteCardPowerApplication(null); }
        _nativeEnemyTurn!.State.Theft!.Steal(target.CombatId!.Value, permanent);
        PowerLifecycleSupport.ResolvePowerAmountChanges(sim, combat); PauseNativeChoice(sim);
        return Task.CompletedTask;
    }
    private static Task PlanNativeTheftNow(MonsterModel monster)
    {
        if (_simulator is null) return HextechCombatHooks.PlanThievingHopperTheftNow(monster);
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        var current = NativeThiefNextMove(monster);
        if (current.Intents.Any(intent => intent is ThievingHopperTheftIntent)) return Task.CompletedTask;
        ComposeOwnedTheft(combat, monster.Creature);
        return Task.CompletedTask;
    }
    private static void ComposeOwnedTheft(SimulatedCombatState combat, Creature creature)
    {
        var scope = _nativeEnemyTurn!;
        var context = new HextechEnemyHexContext(scope.Modifier);
        bool plan = OriginalCanPlanTheft(creature, context) && OriginalBelowTheft(creature);
        var state = ReadNativeMonsterAi(combat, creature);
        int strength = scope.State.Has(MonsterHexKind.CeremonialBeast) ? scope.Tier <= 1 ? 1 : scope.Tier == 2 ? 2 : 3 : 0;
        var copy = (MoveState)NativeMemberwiseClone.Invoke(state.Current, null)!;
        AccessTools.Field(typeof(MoveState), "<Intents>k__BackingField").SetValue(copy,
            OriginalComposeUpgrades(state.Current.Intents, creature, strength, plan));
        SetOwnedAi(combat, creature, state with { Current = copy });
        scope.State.Theft!.SetPlanned(creature.CombatId!.Value, plan);
    }
    private static void CaptureNativeTheftRoll(SimulatedCombatState __instance, Creature enemy, out BranchMonsterAiState? __state)
        => __state = enemy.Monster is not null && __instance.ContainsCreature(enemy)
            && PowerSourceBridge.HasCapturedHex(__instance, MonsterHexKind.ThievingHopper) ? ReadNativeMonsterAi(__instance, enemy) : null;
    private static void PlanNativeTheftAfterRoll(SimulatedCombatState __instance, CombatPredictionSimulator simulator,
        Creature enemy, BranchMonsterAiState? __state)
    {
        if (__state is null || !__instance.ContainsCreature(enemy) || enemy.Monster is null || simulator.State.GetCreature(enemy).IsDead) return;
        var after = ReadNativeMonsterAi(__instance, enemy);
        if (!__state.NeedsInitialRoll && after.StateLog.Count == __state.StateLog.Count) return;
        var modifier = __instance.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        if (!state.Has(MonsterHexKind.ThievingHopper)) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, _ => { ComposeOwnedTheft(__instance, enemy); return Task.CompletedTask; });
    }
    private static void CaptureNativeStockTheft(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ForecastMove move, out PowerModel[]? __state)
    {
        __state = null;
        if (move.Owner.Monster?.GetType().Name != "ThievingHopper" || move.Move.Id != "THIEVERY_MOVE"
            || !PowerSourceBridge.HasCapturedHex(combat, MonsterHexKind.ThievingHopper)) return;
        NativeSelfUpgradeState.Require(simulator).BindCurrentCards();
        __state = combat.EffectivePowers().OfType<SwipePower>().Cast<PowerModel>().ToArray();
    }
    private static void CompleteNativeStockTheft(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ForecastMove move, PowerModel[]? __state)
    {
        if (__state is null) return;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        var ledger = NativeSelfUpgradeState.Require(simulator);
        foreach (var swipe in combat.EffectivePowers().OfType<SwipePower>().Where(power => !__state.Contains(power)))
        {
            var card = swipe.StolenCard ?? throw new PredictionUnsupportedException("Stock theft produced no card payload.");
            var permanent = ledger.DeckVersion(card) ?? throw new PredictionUnsupportedException("Stock theft lost its owned permanent card.");
            ledger.RemovePersistent(permanent);
            card.DeckVersion = permanent;
            state.Theft!.Steal(move.Owner.CombatId!.Value, permanent);
        }
    }
    private static void SynchronizeNativeTheftMove(SimulatedCombatState __instance, Creature creature, MoveState move)
    {
        if (PowerSourceBridge.CapturedEnemyState(__instance) is { Theft: { } theft })
            theft.SetPlanned(creature.CombatId!.Value, move.Intents.Any(intent => intent is ThievingHopperTheftIntent));
    }
    internal static void DispatchNativeThievingHit(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target, DamageResult result, Creature? dealer, CardModel? card)
    {
        if (!state.Has(MonsterHexKind.ThievingHopper) || target.CombatId is not { } id) return;
        InvokeNativeEnemyReaction(modifier, state, simulator, context => OriginalThievingReceived(context, target, id, result, dealer, card));
    }
    private static bool ApplyNativeTheftEscape(CombatPredictionSimulator simulator, SimulatedCombatState combat, ForecastMove move, ref bool __result)
    {
        if (move.Move.Id != ThievingHopperEnemyHex.EscapeMoveId) return true;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault()
            ?? throw new PredictionUnsupportedException("Hextech escape has no owned modifier.");
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        if (!state.Has(MonsterHexKind.ThievingHopper)) throw new PredictionUnsupportedException("Hextech escape has no captured theft state.");
        state.Theft!.Escape(move.Owner.CombatId!.Value);
        combat.CreatureEscaped(move.Owner);
        __result = true;
        return false;
    }
    private static void NativeTheftRemovesOwner(string moveId, ref bool __result)
    { if (moveId == ThievingHopperEnemyHex.EscapeMoveId) __result = true; }
    private static void NativeTheftSupports(string moveId, ref bool __result)
    { if (moveId == ThievingHopperEnemyHex.EscapeMoveId) __result = true; }
    private static void NativeTheftBeforeDeath(AbstractModel listener, BeforeDeathMirrorContext context)
    {
        if (listener is not SwipePower { StolenCard: not null } power || !ReferenceEquals(power.Owner, context.Creature)) return;
        var combat = (SimulatedCombatState)context.CombatState;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
        if (state.Has(MonsterHexKind.ThievingHopper)) state.Theft!.BeforeDeath(context.Creature.CombatId!.Value);
    }
}
