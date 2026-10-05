using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    // Associations are rebound to the fork's creature and model/tracking state;
    // no live Creature or live modifier is mutated by the primitive adapters.
    private sealed record NearDeathBinding(NativeRuneState? Rune, EnemyState? Enemy)
    {
        internal NearDeathFeastRune? Player => Rune?.Model as NearDeathFeastRune;
        internal HextechMayhemCombatTrackingState Tracking => Enemy!.NativeTurns!.Model;
        internal bool Active(SimCreatureState creature) => Player is { } rune ? rune.SavedNearDeathActive
            : Tracking.NearDeathFeastEnemyDebt.ContainsKey(creature.Creature.CombatId!.Value);
        internal int Debt(SimCreatureState creature) => Player is { } rune ? rune.SavedNearDeathDebt
            : Tracking.NearDeathFeastEnemyDebt.GetValueOrDefault(creature.Creature.CombatId!.Value);
        internal int Limit(SimCreatureState creature) => Math.Max(1, (int)Math.Floor(creature.MaxHp
            * (Player is not null ? .5m : .05m * Math.Clamp(Enemy!.StrengthTier, 1, 3))));
        internal bool Dying(SimCreatureState creature) => Active(creature) && creature.CurrentHp > 0 && Debt(creature) < Limit(creature);
        internal void ClearEnemy(SimCreatureState creature)
        {
            uint id = creature.Creature.CombatId!.Value;
            Tracking.NearDeathFeastEnemyDebt.Remove(id); Tracking.NearDeathFeastEnemyStrength.Remove(id);
        }
        internal void Write(SimCreatureState creature, bool dying, int debt)
        {
            if (Player is { } rune) { rune.SavedNearDeathActive = dying; rune.SavedNearDeathDebt = debt; }
            else if (dying) Tracking.NearDeathFeastEnemyDebt[creature.Creature.CombatId!.Value] = debt;
            else ClearEnemy(creature);
        }
    }
    private static readonly ConditionalWeakTable<SimCreatureState, NearDeathBinding> NativeNearCreatures = new();
    private static readonly Func<bool, int, int, decimal, int, HextechNearDeathHpLoss> NativeNearLoss =
        AccessTools.DeclaredMethod(typeof(HextechNearDeathHpLoss), "Resolve")
            .CreateDelegate<Func<bool, int, int, decimal, int, HextechNearDeathHpLoss>>();
    private static readonly Func<HextechEnemyHexContext, Creature, decimal, Task> NativeNearEnemyHp =
        AccessTools.DeclaredMethod(typeof(NearDeathFeastEnemyHex), "AfterCurrentHpChanged")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, decimal, Task>>(new NearDeathFeastEnemyHex());

    internal static void BindNativeNearPlayer(NativeRuneState state, SimCreatureState creature)
    { NativeNearCreatures.Remove(creature); NativeNearCreatures.Add(creature, new(state, null)); }
    internal static void BindNativeNearEnemy(EnemyState state, SimCreatureState creature)
    {
        if (!state.Has(MonsterHexKind.NearDeathFeast) || creature.Creature.Side != CombatSide.Enemy || creature.Creature.CombatId is null) return;
        if (!state.NearDeathCreatures.Contains(creature)) state.NearDeathCreatures.Add(creature);
        NativeNearCreatures.Remove(creature); NativeNearCreatures.Add(creature, new(null, state));
    }
    private static void RegisterNativeNearDeath(Harmony harmony)
    {
        void Prefix(System.Reflection.MethodBase target, string name)
        {
            var prefix = AccessTools.Method(typeof(NativeRuneBridge), name);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            NativeCallbackContracts.AddNativePrefix(target, prefix, "HextechSolverCompat", Priority.Normal);
        }
        // Leaf readers must be patched before native callbacks can inline them.
        Prefix(AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "GetRune"), nameof(NativeNearGetRune));
        Prefix(AccessTools.DeclaredMethod(typeof(HextechEnemyNearDeath), "TryGetContext"), nameof(NativeNearGetEnemy));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "GetDeathNegativeHpLimit"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "MaxHp"), nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "IsDyingButAlive"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CurrentHp"), nameof(NativeEnemyCurrentHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "SyncNearDeathStrength"), SingleNativePowerSite<StrengthPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechEnemyNearDeath), "SyncStrengthAfterHpChanged"),
            Site(AccessTools.PropertyGetter(typeof(HextechMayhemModifier), "CombatTracking"), nameof(NativeNearTracking)), SingleNativePowerSite<StrengthPower>());
        foreach (var type in new[] { typeof(HextechNearDeathHpLoss), typeof(NearDeathFeastEnemyHex) })
            foreach (var method in AccessTools.GetDeclaredMethods(type)) NativeCallbackContracts.Add(method);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "AfterCurrentHpChanged"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(NearDeathFeastRune), "ModifyBlockMultiplicative"));
        RegisterState<NearDeathFeastRune>(); RuneMirrors.RegisterNativeBase<NearDeathFeastRune>();
        AfterCurrentHpChangedMirrors.Registry.Register<NearDeathFeastRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCurrentHpChanged(context.Creature, context.Delta)), typeof(NearDeathFeastRune)));
        ModifyBlockMultiplicativeMirrors.Registry.Register<NearDeathFeastRune>((rune, context) => Invoke(rune, context.Simulator,
            model => model.ModifyBlockMultiplicative(context.Target, context.Amount, context.Props, context.CardSource?.MutablePreview, context.CardPlay)));
        Prefix(AccessTools.DeclaredMethod(typeof(SimCreatureState), "LoseHp"), nameof(NativeNearLoseHp));
        Prefix(AccessTools.PropertySetter(typeof(SimCreatureState), "CurrentHp"), nameof(NativeNearSetHp));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(CombatPredictionSimulator)).Where(method => method.Name == "GainBlock"))
            Prefix(method, nameof(NativeNearBlock));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(CombatPredictionSimulator)).Where(method => method.Name == "Kill"))
            Prefix(method, nameof(NativeNearKill));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.NearDeathFeast);
    }
    private static bool NativeNearGetRune(Creature creature, ref NearDeathFeastRune? __result)
    {
        if (_simulator is not { } sim) return true;
        var root = creature.Player is { } owner ? ((SimulatedCombatState)sim.State.CombatState).RelicsOf(owner).OfType<NearDeathFeastRune>().SingleOrDefault() : null;
        __result = root is null ? null : (NearDeathFeastRune)ModelPredictionStateMirrors.Get<NativeRuneState>(sim, root).Model;
        return false;
    }
    private static bool NativeNearGetEnemy(Creature creature, out HextechMayhemModifier? modifier, out uint combatId, ref bool __result)
    {
        modifier = null; combatId = 0;
        if (_simulator is null || _nativeEnemyTurn is not { } scope) return true;
        __result = creature.Side == CombatSide.Enemy && creature.CombatId is not null && scope.State.Has(MonsterHexKind.NearDeathFeast);
        if (__result) { modifier = scope.Modifier; combatId = creature.CombatId!.Value; }
        return false;
    }
    private static HextechMayhemCombatTrackingState NativeNearTracking(HextechMayhemModifier modifier)
        => _simulator is not null && _nativeEnemyTurn is { } scope ? scope.State.NativeTurns!.Model : modifier.CombatTracking;
    private static bool NativeNearLoseHp(SimCreatureState __instance, decimal amount, ValueProp props, ref DamageResult __result)
    {
        if (amount <= 0m || !NativeNearCreatures.TryGetValue(__instance, out var binding)) return true;
        if (!binding.Active(__instance) && __instance.CurrentHp - (int)Math.Min(amount, 999999999m) >= 1) return true;
        var outcome = NativeNearLoss(binding.Active(__instance), binding.Debt(__instance), __instance.CurrentHp, amount, binding.Limit(__instance));
        binding.Write(__instance, outcome.Dying, outcome.Debt);
        __instance.CurrentHp = outcome.CurrentHp;
        __result = new DamageResult(__instance.Creature, props) { UnblockedDamage = outcome.HpLoss, WasTargetKilled = outcome.Killed, OverkillDamage = outcome.OverkillDamage };
        return false;
    }
    private static void NativeNearSetHp(SimCreatureState __instance, ref int __0)
    {
        if (!NativeNearCreatures.TryGetValue(__instance, out var binding)) return;
        if (__0 >= 0) { if (__0 > 1 && binding.Enemy is not null) binding.ClearEnemy(__instance); return; }
        int debt = Math.Max(0, -__0), limit = binding.Limit(__instance);
        binding.Write(__instance, debt < limit, debt < limit ? debt : limit);
        __0 = debt < limit ? 1 : 0;
    }
    internal static bool NativeNearHeal(CombatPredictionSimulator __instance, Creature creature)
        => !NativeNearCreatures.TryGetValue(__instance.State.GetCreature(creature), out var binding) || !binding.Dying(__instance.State.GetCreature(creature));
    private static bool NativeNearBlock(CombatPredictionSimulator __instance, Creature creature, ref decimal __result)
    { if (NativeNearHeal(__instance, creature)) return true; __result = 0m; return false; }
    private static void NativeNearKill(CombatPredictionSimulator __instance, object __0)
    {
        foreach (var creature in __0 is Creature single ? new[] { single } : (IReadOnlyList<Creature>)__0)
        {
            var state = __instance.State.GetCreature(creature);
            if (!NativeNearCreatures.TryGetValue(state, out var binding)) continue;
            binding.Write(state, false, binding.Player is not null ? binding.Limit(state) : 0); state.CurrentHp = 0;
        }
    }
    internal static void DispatchNativeNearEnemyHp(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature creature, decimal delta)
    {
        if (state.Has(MonsterHexKind.NearDeathFeast))
            InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeNearEnemyHp(context, creature, delta));
    }
}
