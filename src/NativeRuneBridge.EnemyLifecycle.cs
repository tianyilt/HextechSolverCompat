using System.Collections.Frozen;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record NativeEnemyDeathHandler(MonsterHexKind Kind,
        Func<HextechEnemyHexContext, Creature, Task>? Before,
        Func<HextechEnemyHexContext, PlayerChoiceContext, Creature, ICombatState, Task>? After);
    private static NativeEnemyDeathHandler[] NativeEnemyDeaths = [];
    private static readonly PhrogParasiteEnemyHex NativePhrog = new();
    private static readonly Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task> NativePhrogHit =
        AccessTools.DeclaredMethod(typeof(PhrogParasiteEnemyHex), "AfterEnemyDamageGivenImmediate")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task>>(NativePhrog);
    private static readonly ServantMasterEnemyHex NativeServant = new();
    private static readonly Func<HextechEnemyHexContext, Creature, int?, bool, Task> NativeServantSpawn =
        AccessTools.DeclaredMethod(typeof(ServantMasterEnemyHex), "ApplyPersistentToEnemy")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, int?, bool, Task>>(NativeServant);
    private static readonly Func<HextechEnemyHexContext, PowerModel, decimal, Creature?, CardModel?, Task> NativeServantPower =
        AccessTools.DeclaredMethod(typeof(ServantMasterEnemyHex), "AfterPowerAmountChanged")
            .CreateDelegate<Func<HextechEnemyHexContext, PowerModel, decimal, Creature?, CardModel?, Task>>(NativeServant);
    private static readonly Func<HextechEnemyHexContext, PlayerChoiceContext, CombatSide, ICombatState, Task> NativeDeadCleanup =
        AccessTools.DeclaredMethod(typeof(GetExcitedEnemyHex), "BeforeSideTurnStart")
            .CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CombatSide, ICombatState, Task>>(new GetExcitedEnemyHex());

    private static void RegisterNativeEnemyLifecycle(Harmony harmony)
    {
        var semantics = typeof(SimulatedCombatState).GetInterfaceMap(typeof(ICombatPredictionCreatureSemantics));
        int removalSlot = Array.FindIndex(semantics.InterfaceMethods, method => method.Name == nameof(ICombatPredictionCreatureSemantics.ShouldRemoveAfterDeath));
        if (removalSlot < 0) throw new InvalidOperationException("Native death removal contract is absent.");
        RegisterNativeSdkPrefix(harmony, semantics.TargetMethods[removalSlot], nameof(KeepOwnedPainfulStabsCorpse));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "BeforeDeath"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "AfterDeath"));
        var combat = AccessTools.PropertyGetter(typeof(Creature), "CombatState");
        var dead = AccessTools.PropertyGetter(typeof(Creature), "IsDead");
        var alive = AccessTools.PropertyGetter(typeof(Creature), "IsAlive");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PhrogParasiteEnemyHex), "AfterEnemyDamageGivenImmediate"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "TryApplyServantMasterIllusion"),
            Site(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "TryApplyServantMasterIllusion"), nameof(ApplyNativeServantService)));
        var has = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "HasPower" && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechServantMasterIllusionService), "TryApply"),
            Site(alive, nameof(NativeBranchIsAlive)), Site(combat, nameof(NativeBranchCombat)),
            new NativeCallSite(has.MakeGenericMethod(typeof(MinionPower)), AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeLifecycleHasPower)).MakeGenericMethod(typeof(MinionPower)), 1),
            new NativeCallSite(has.MakeGenericMethod(typeof(IllusionPower)), AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeLifecycleHasPower)).MakeGenericMethod(typeof(IllusionPower)), 1),
            SingleNativePowerSite<IllusionPower>());
        foreach (string name in new[] { "ApplyPersistentToEnemy", "AfterPowerAmountChanged" }) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ServantMasterEnemyHex), name));
        var power = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPower" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        var painful = new NativeCallSite(power.MakeGenericMethod(typeof(PainfulStabsPower)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(PainfulStabsPower)), 1);
        var remove = AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatCreatureHelper), "RemovePainfulStabsBeforeDeath"),
            Site(combat, nameof(NativeBranchCombat)), painful, Site(remove, nameof(RemoveNativePower)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatCreatureHelper), "CleanUpRetainedPainfulStabsEnemies"),
            Site(combat, nameof(NativeBranchCombat)), Site(dead, nameof(NativeBranchIsDead)), painful, Site(remove, nameof(RemoveNativePower)));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatCreatureHelper), "RemoveRetainedDeadEnemyIfNeeded"), nameof(RemoveOwnedRetainedEnemy));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GetExcitedEnemyHex), "AfterDeath"),
            ManyNativePowerSite<StrengthPower>(), ManyNativePowerSite<PainfulStabsPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SoulEaterEnemyHex), "AfterDeath"),
            Site(alive, nameof(NativeBranchIsAlive)),
            Site(AccessTools.Method(typeof(CreatureCmd), "GainMaxHp", [typeof(Creature), typeof(decimal)]), nameof(GainNativeMaxHp)));
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        NativeEnemyDeaths = effects.Where(effect => effect is GetExcitedEnemyHex or TestSubjectEnemyHex or SoulEaterEnemyHex).Select(effect =>
        {
            var before = AccessTools.DeclaredMethod(effect.GetType(), "BeforeDeath"); var after = AccessTools.DeclaredMethod(effect.GetType(), "AfterDeath");
            foreach (string name in new[] { "BeforeSideTurnStart", "BeforeDeath" })
                if (AccessTools.DeclaredMethod(effect.GetType(), name) is { } callback) NativeCallbackContracts.Add(callback);
            return new NativeEnemyDeathHandler(AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(),
                before?.CreateDelegate<Func<HextechEnemyHexContext, Creature, Task>>(effect),
                after?.CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, Creature, ICombatState, Task>>(effect));
        }).ToArray();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SoulEaterEnemyHex), "ResolveMaxHpGain"));
        CompatibilityGuard.EnemyHexes.UnionWith([MonsterHexKind.PhrogParasite, MonsterHexKind.ServantMaster, MonsterHexKind.GetExcited, MonsterHexKind.TestSubject, MonsterHexKind.SoulEater]);
    }
    private static bool KeepOwnedPainfulStabsCorpse(SimulatedCombatState __instance, Creature creature, ref bool __result)
    {
        if (!__instance.Modifiers.OfType<HextechMayhemModifier>().Any() || __instance.GetAmount<PainfulStabsPower>(creature) <= 0) return true;
        __result = false;
        return false;
    }
    private static bool NativeLifecycleHasPower<T>(Creature creature) where T : PowerModel
        => _simulator is null ? creature.HasPower<T>() : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<T>(creature) > 0;
    private static Task ApplyNativeServantService(HextechMayhemModifier modifier, Creature creature, Creature? applier, CardModel? cardSource)
    {
        if (_simulator is null) return modifier.TryApplyServantMasterIllusion(creature, applier, cardSource);
        var state = _nativeEnemyTurn?.State.NativeTurns?.Model ?? throw new PredictionUnsupportedException("ServantMaster tracking context is absent.");
        return HextechServantMasterIllusionService.TryApply((RunState)_simulator.State.CombatState.RunState, state, creature, applier, cardSource);
    }
    private static bool RemoveOwnedRetainedEnemy(ICombatState combatState, Creature enemy)
    {
        if (_simulator is null) return true;
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        if (enemy.Side == CombatSide.Enemy && _simulator.State.GetCreature(enemy).IsDead && combat.ContainsCreature(enemy)
            && ((ICombatPredictionCreatureSemantics)combat).ShouldRemoveAfterDeath(enemy)) combat.RemoveCreature(enemy, false);
        return false;
    }
    internal static void DispatchNativePhrogHit(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator,
        Creature dealer, DamageResult result, Creature target, CardModel? source)
    {
        if (state.Has(MonsterHexKind.PhrogParasite)) InvokeNativeEnemyReaction(modifier, state, simulator, context => NativePhrogHit(context, dealer, result, target, source));
    }
    internal static void DispatchNativeServantSpawn(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, Creature creature)
    {
        if (state.Has(MonsterHexKind.ServantMaster)) InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeServantSpawn(context, creature, null, false));
    }
    internal static void DispatchNativeServantPower(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, SimulatedPowerAmountChange change, PredictedCard? cardSource)
    {
        if (state.Has(MonsterHexKind.ServantMaster) && change.Power is MinionPower && change.Delta > 0)
            InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeServantPower(context, change.Power, change.Delta, change.Applier, cardSource?.MutablePreview));
    }
    internal static void DispatchNativeEnemyDeath(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, Creature creature, bool before, bool wasRemovalPrevented = false)
    {
        var previous = _nativeSoulBirthHps; _nativeSoulBirthHps = state.SoulBirthHps;
        try { InvokeNativeEnemyReaction(modifier, state, simulator, async context =>
        {
            // The modifier filters retained corpses and boss phase transitions before dispatching any enemy AfterDeath effect.
            if (!before && (wasRemovalPrevented || creature.Side != CombatSide.Enemy || !FlyingKickTrueDeath(creature))) return;
            foreach (var handler in NativeEnemyDeaths.Where(handler => state.Has(handler.Kind)))
                if (before && handler.Before is { } first) await first(context, creature);
                else if (!before && handler.After is { } last) await last(context, new ThrowingPlayerChoiceContext(), creature, simulator.State.CombatState);
        }); }
        finally { _nativeSoulBirthHps = previous; }
    }
    internal static void DispatchNativeDeadCleanup(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, CombatSide side)
    {
        foreach (var handler in NativeEnemyDeaths.Where(handler => state.Has(handler.Kind) && handler.Kind is MonsterHexKind.GetExcited or MonsterHexKind.TestSubject))
            InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeDeadCleanup(context, new ThrowingPlayerChoiceContext(), side, simulator.State.CombatState));
    }
    internal static IReadOnlyDictionary<Creature, int> CaptureNativeEnemySoulBirth(CombatPredictionSimulator simulator)
    {
        foreach (var enemy in simulator.State.CombatState.Enemies) _ = simulator.State.GetCreature(enemy);
        return simulator.State.CombatState.Enemies.ToFrozenDictionary(enemy => enemy, OriginalSoulBirthHp);
    }
}
