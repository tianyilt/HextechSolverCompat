using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeOrbShuffle(Harmony harmony)
    {
        // Original leaf predicates are patched before callers can be JIT-inlined.
        PatchEventCallback(harmony, NativeLambda(typeof(RecycleBinRune), "ModifyShuffleOrder"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)));
        RegisterState<RecycleBinRune>(); RuneMirrors.RegisterNativeBase<RecycleBinRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(RecycleBinRune), nameof(AbstractModel.ModifyShuffleOrder)));
        ModifyShuffleOrderMirrors.Registry.Register<RecycleBinRune>((rune, context) =>
        {
            var nativeCards = context.Cards.Select(card => card.MutablePreview).ToList();
            Invoke(rune, context.Simulator, model =>
            {
                model.ModifyShuffleOrder(context.Player, nativeCards, context.IsInitialShuffle);
                return true;
            });
            var retained = nativeCards.Select(card => context.State.FindCard(card)
                ?? throw new PredictionUnsupportedException("Native shuffle retained an absent branch card.")).ToArray();
            context.Cards.Clear(); context.Cards.AddRange(retained);
        });

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MarkovBabbleRune), "AfterSideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(OrbQueue), nameof(OrbQueue.Capacity)), nameof(NativeOrbCapacity)),
            Site(AccessTools.PropertyGetter(typeof(OrbQueue), nameof(OrbQueue.Orbs)), nameof(NativeOrbContents)),
            Site(NativeOrbChannel, nameof(ChannelOrb)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechStableCombatSpawns), "CreateOrb"));
        RegisterStableState<MarkovBabbleRune>(); RuneMirrors.RegisterNativeBase<MarkovBabbleRune>();
        RegisterNativeSideStartCallback<MarkovBabbleRune>();

        PatchEventCallback(harmony, NativeLambda(typeof(HextechNightmareHooks), "TriggerNightmare"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechNightmareHooks), "TriggerNightmare"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatCreatureHelper), "GetAliveEnemies"), nameof(NativeOrbAliveEnemies)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature)]), nameof(NativeOrbSingleDamage)));
        RegisterState<NightmareRune>(); RuneMirrors.RegisterNativeBase<NightmareRune>();
        var dark = AccessTools.DeclaredMethod(typeof(DarkOrbMirrors), nameof(DarkOrbMirrors.Passive));
        var darkPostfix = AccessTools.Method(typeof(NativeRuneBridge), nameof(AfterNativeDarkPassive));
        harmony.Patch(dark, postfix: new HarmonyMethod(darkPostfix));
        NativeCallbackContracts.AddNativePostfix(dark, darkPostfix, "HextechSolverCompat", Priority.Normal);
        NativeCallbackContracts.AddNativePostfix(AccessTools.DeclaredMethod(typeof(DarkOrb), nameof(OrbModel.Passive)),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(NightmareRune), "PassivePatch"), "Postfix"), "Natsuki.HextechRunes", Priority.Normal);

        PatchEventCallback(harmony, NativeLambda(typeof(HextechPlayerRuneHooks), "ApplyElectrodynamicsLightningDamage"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsHittable)), nameof(NativeOrbHittable)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechPlayerRuneHooks), "ApplyElectrodynamicsLightningDamage"),
            Site(AccessTools.PropertyGetter(typeof(OrbModel), nameof(OrbModel.CombatState)), nameof(NativeOrbCombat)),
            Site(AccessTools.Method(typeof(VfxCmd), nameof(VfxCmd.PlayOnCreature), [typeof(Creature), typeof(string)]), nameof(SkipNativeOrbVfx)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature)]), nameof(NativeInfernoDamage)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ElectrodynamicsRune), nameof(AbstractModel.AfterPlayerTurnStart)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(NativeOrbChannel, nameof(ChannelOrb)));
        RegisterState<ElectrodynamicsRune>(); RuneMirrors.RegisterNativeBase<ElectrodynamicsRune>();
        AfterPlayerTurnStartMirrors.Register<ElectrodynamicsRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(ElectrodynamicsRune)));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(LightningOrbMirrors), "Damage"), nameof(NativeAllEnemyLightning));
        NativeCallbackContracts.AddNativePrefix(AccessTools.DeclaredMethod(typeof(LightningOrb), "ApplyLightningDamage"),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(ElectrodynamicsRune), "ElectrodynamicsPatch"), "Prefix"), "Natsuki.HextechRunes", Priority.Low);
    }

    private static MethodInfo NativeLambda(Type type, string callback)
        => AccessTools.GetDeclaredMethods(AccessTools.Inner(type, "<>c"))
            .Single(method => method.Name.StartsWith("<" + callback + ">b__", StringComparison.Ordinal));

    private static int NativeOrbCapacity(OrbQueue queue)
    {
        if (_simulator is not { } sim) return queue.Capacity;
        var player = sim.State.CombatState.Players.Single(owner => ReferenceEquals(owner.PlayerCombatState?.OrbQueue, queue));
        return sim.State.GetPlayerCombatState(player).OrbQueue.Capacity;
    }

    private static IReadOnlyList<Creature> NativeOrbAliveEnemies(ICombatState combat)
        => _simulator is { } sim ? combat.Enemies.Where(enemy => sim.State.GetCreature(enemy).IsAlive).ToArray()
            : HextechCombatCreatureHelper.GetAliveEnemies(combat);

    private static Task<IEnumerable<DamageResult>> NativeOrbSingleDamage(PlayerChoiceContext choice,
        Creature target, decimal value, ValueProp props, Creature dealer)
    {
        if (_simulator is not { } sim) return CreatureCmd.Damage(choice, target, value, props, dealer);
        var results = sim.Damage([target], value, props, dealer); PauseNativeChoice(sim);
        return Task.FromResult<IEnumerable<DamageResult>>(results);
    }

    private static void AfterNativeDarkPassive(DarkOrb orb, OrbPassiveMirrorContext context)
    {
        var combat = (SimulatedCombatState)context.CombatState;
        if (context.Simulator.HasPendingChoice || context.Simulator.IsOverOrEnding) return;
        foreach (var rune in combat.RelicsOf(orb.Owner).OfType<NightmareRune>())
            RequireCompleted(Invoke(rune, context.Simulator, _ => HextechNightmareHooks.TriggerNightmare(
                orb, new ThrowingPlayerChoiceContext(), orb.Owner)), typeof(NightmareRune));
    }

    private static bool NativeOrbHittable(Creature creature)
        => _simulator is { } sim ? sim.State.IsHittable(creature) : creature.IsHittable;
    private static ICombatState NativeOrbCombat(OrbModel orb) => _simulator?.State.CombatState ?? orb.CombatState;
    private static void SkipNativeOrbVfx(Creature target, string effect)
    { if (_simulator is null) VfxCmd.PlayOnCreature(target, effect); }

    private static bool NativeAllEnemyLightning(LightningOrb orb, OrbMirrorContext context, decimal value,
        ref IReadOnlyList<Creature> __result)
    {
        var rune = ((SimulatedCombatState)context.CombatState).RelicsOf(orb.Owner).OfType<ElectrodynamicsRune>().SingleOrDefault();
        if (rune is null) return true;
        var task = Invoke(rune, context.Simulator, _ => HextechPlayerRuneHooks.ApplyElectrodynamicsLightningDamage(
            orb, value, new ThrowingPlayerChoiceContext()));
        RequireCompleted(task, typeof(ElectrodynamicsRune)); __result = task.Result.ToArray(); return false;
    }
}
