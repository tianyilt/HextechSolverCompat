using System.Collections.Frozen;
using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static IReadOnlyDictionary<Creature, int>? _nativeSoulBirthHps;
    private static readonly Func<Creature, int> OriginalSoulBirthHp = AccessTools.Method(typeof(SoulEaterRune), "GetScaledInitialMonsterMaxHp")
        .CreateDelegate<Func<Creature, int>>();
    private static readonly FieldInfo WriterReferenceCombat = AccessTools.Field(typeof(ModelPredictionStateWriter), "_referenceCombat");

    private static void RegisterNativeDeathGrowth(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var hp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
        var maxHp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var trueDeath = AccessTools.Method(typeof(HextechMonsterInteractionPolicy), "IsTrueCombatDeath", [typeof(Creature)]);
        var gainMax = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp), [typeof(Creature), typeof(decimal)]);
        // Patch leaf readers before compiling their callers; otherwise the runtime
        // can inline an unpatched live display query into the reward callback.
        PatchEventCallback(harmony, AccessTools.Method(typeof(SoulEaterRune), "IsTransientInfiniteHpState"),
            Site(maxHp, nameof(NativeBranchMaxHp)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.HpDisplay)), nameof(NativeDeathHpDisplay)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(SoulEaterRune), "GetRewardMaxHpForDeath"),
            Site(maxHp, nameof(NativeBranchMaxHp), 3),
            Site(AccessTools.Method(typeof(SoulEaterRune), "GetScaledInitialMonsterMaxHp"), nameof(NativeFrozenSoulBirthHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SoulEaterRune), "AfterDeath"),
            Site(dead, nameof(NativeBranchIsDead)), Site(trueDeath, nameof(FlyingKickTrueDeath)), Site(gainMax, nameof(GainNativeMaxHp)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(SoulEaterRune), "GetScaledInitialMonsterMaxHp"));
        ModelPredictionStateMirrors.RegisterRelic<SoulEaterRune, NativeRuneState>("native-death-birth-hp-v1",
            CaptureNativeSoulState,
            NativeRuneState.WriteModel<SoulEaterRune>, NativeRuneState.WriteState);
        RegisterNativeDeathCallback<SoulEaterRune>(registerState: false);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(JudicatorRune), "ModifyDamageMultiplicativeCompat"),
            Site(hp, nameof(NativeEnemyCurrentHp)), Site(maxHp, nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(JudicatorRune), "AfterDeath"),
            Site(dead, nameof(NativeBranchIsDead)), Site(trueDeath, nameof(FlyingKickTrueDeath)),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.Energy)), nameof(NativeDeathEnergy)),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.MaxEnergy)), nameof(NativeDeathMaxEnergy), 2),
            Site(AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.SetEnergy)), nameof(SetNativeDeathEnergy)));
        RegisterNativeDeathCallback<JudicatorRune>();
        RegisterNativeQueryCallbacks<JudicatorRune>(NativeQueries.DamageMultiplier);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CorpseExplosionRune), "AfterDeath"),
            Site(dead, nameof(NativeBranchIsDead)), Site(trueDeath, nameof(FlyingKickTrueDeath)),
            Site(combat, nameof(NativeBranchCombat)), Site(maxHp, nameof(NativeBranchMaxHp)),
            Site(AccessTools.Method(typeof(HextechCombatVfx), "CorpseBloomBurst"), nameof(NativeCorpseBloom)),
            ManyNativePowerSite<PoisonPower>());
        var corpsePredicate = typeof(CorpseExplosionRune).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.Contains("AfterDeath")
                && method.ReturnType == typeof(bool) && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(Creature));
        PatchEventCallback(harmony, corpsePredicate,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        RegisterNativeDeathCallback<CorpseExplosionRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DiveBomberRune), "AfterDeath"),
            Site(combat, nameof(NativeBranchCombat)), Site(maxHp, nameof(NativeBranchMaxHp)),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechGameApiCompat)).Single(method => method.Name == "Damage"
                && method.GetParameters().Length == 7 && method.GetParameters()[1].ParameterType == typeof(IEnumerable<Creature>)), nameof(NativeDiveDamage)));
        var divePredicate = typeof(DiveBomberRune).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.Contains("AfterDeath")
                && method.ReturnType == typeof(bool) && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(Creature));
        PatchEventCallback(harmony, divePredicate,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(DiveBomberRune), "GetDamage"));
        RegisterNativeDeathCallback<DiveBomberRune>();
        RegisterNativeStackGrowth(harmony);
    }

    private static void RegisterNativeDeathCallback<T>(bool registerState = true) where T : HextechRelicBase
    {
        if (registerState) RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterDeathMirrors.Registry.Register<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterDeath(new ThrowingPlayerChoiceContext(), context.Creature, context.WasRemovalPrevented, 0f)), typeof(T)));
    }
    private static NativeRuneState CaptureNativeSoulState(CombatPredictionSimulator simulator, SoulEaterRune live)
    {
        var enemies = live.Owner.Creature.CombatState!.Enemies.ToArray();
        // Creature state is lazy in the pinned SDK. Materialize display as
        // well as HP at capture, before a later fork can consult live data.
        foreach (var enemy in enemies) _ = simulator.State.GetCreature(enemy);
        return new NativeRuneState(NativeRuneState.Clone(live))
        { SoulBirthHps = enemies.ToFrozenDictionary(enemy => enemy, OriginalSoulBirthHp) };
    }
    private static HpDisplay NativeDeathHpDisplay(Creature creature)
        => _simulator is not { } sim ? creature.HpDisplay : sim.State.GetCreature(creature).HpDisplay;
    private static int NativeFrozenSoulBirthHp(Creature creature)
        => _simulator is null ? OriginalSoulBirthHp(creature)
            : (_nativeSoulBirthHps ?? throw new PredictionUnsupportedException("SoulEater birth metadata not captured."))
                .TryGetValue(creature, out var hp) ? hp : OriginalSoulBirthHp(creature);
    internal static void WriteSoulBirthHps(IReadOnlyDictionary<Creature, int>? captured, ref ModelPredictionStateWriter writer)
    {
        var combat = (ICombatState?)WriterReferenceCombat.GetValue(writer)
            ?? throw new PredictionUnsupportedException("SoulEater metadata writer lacks the exact bound combat.");
        foreach (var enemy in combat.Enemies.OrderBy(enemy => enemy.CombatId))
            writer.Add("soulBirthHp:" + enemy.CombatId, captured?.TryGetValue(enemy, out var hp) == true ? hp : OriginalSoulBirthHp(enemy));
    }
    private static Player NativeDeathPlayer(PlayerCombatState state)
        => _simulator!.State.CombatState.Players.Single(player => ReferenceEquals(player.PlayerCombatState, state));
    private static int NativeDeathEnergy(PlayerCombatState state)
        => _simulator is null ? state.Energy : _simulator.State.GetPlayerCombatState(NativeDeathPlayer(state)).Energy;
    private static int NativeDeathMaxEnergy(PlayerCombatState state)
        => _simulator is null ? state.MaxEnergy : PersistentPowerSupport.GetModifiedMaxEnergy(
            (SimulatedCombatState)_simulator.State.CombatState, NativeDeathPlayer(state));
    private static Task SetNativeDeathEnergy(decimal amount, Player player)
    {
        if (_simulator is null) return PlayerCmd.SetEnergy(amount, player);
        var state = _simulator.State.GetPlayerCombatState(player);
        state.GainEnergy(amount - state.Energy);
        return Task.CompletedTask;
    }
    private static void NativeCorpseBloom(Creature target, IReadOnlyList<Creature> enemies)
    { if (_simulator is null) HextechCombatVfx.CorpseBloomBurst(target, enemies); }
    private static Task<IEnumerable<DamageResult>> NativeDiveDamage(PlayerChoiceContext context, IEnumerable<Creature> enemies,
        decimal damage, MegaCrit.Sts2.Core.ValueProps.ValueProp props, Creature? dealer, CardModel? source,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay? play)
    {
        if (_simulator is null) return HextechGameApiCompat.Damage(context, enemies, damage, props, dealer, source, play);
        var card = source is null ? null : _simulator.State.FindCard(source)
            ?? throw new PredictionUnsupportedException("DiveBomber damage source not captured.");
        var result = _simulator.Damage(enemies.ToArray(), damage, props, dealer, card, play);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<DamageResult>>(result);
    }
}
