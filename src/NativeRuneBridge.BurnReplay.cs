using System.Reflection;
using HarmonyLib;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly FieldInfo InfernalTriggered = AccessTools.Field(
        typeof(HextechInfernalDragonSoulPower), "_triggeredThisTurn");

    private static NativeCallSite ManyNativePowerSite<T>() where T : PowerModel
    {
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(m =>
            m.Name == "Apply" && m.IsGenericMethodDefinition && m.GetParameters().Length == 5
            && m.GetParameters()[0].ParameterType == typeof(IEnumerable<Creature>));
        return new(apply.MakeGenericMethod(typeof(T)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerMany)).MakeGenericMethod(typeof(T)), 1);
    }

    private static void RegisterNativeBurnReplay(Harmony harmony)
    {
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        var resolve = AccessTools.DeclaredMethod(typeof(HextechBurnPower), "ResolveBurn");
        PatchEventCallback(harmony, resolve,
            Site(alive, nameof(NativeBranchIsAlive), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp)),
            Site(AccessTools.Method(typeof(HextechBurnVisual), "PlayTickBurst"), nameof(NativeBurnVisual)),
            SingleNativePowerSite<HextechBurnPower>(),
            Site(AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait),
                [typeof(float), typeof(float), typeof(bool), typeof(CancellationToken)]), nameof(QuantumWait)));
        var closure = typeof(HextechBurnPower).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t => AccessTools.GetDeclaredMethods(t))
            .Single(m => m.Name == "<ResolveBurn>b__0");
        PatchEventCallback(harmony, closure, Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
            [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardModel), typeof(CardPlay)]),
            nameof(NativeBurnDamage)));
        RegisterNativeSoulPower<HextechBurnPower>(beforeEndIgnored: false);
        BeforeSideTurnEndMirrors.Registry.Register<HextechBurnPower>((power, context) =>
            InvokeNativePower(power, context.Simulator, model => model.BeforeTurnEndForParticipants(
                new ThrowingPlayerChoiceContext(), context.Side, context.Participants)));
        NativeAfterSidePowers.Add(typeof(HextechBurnPower), (power, simulator, side, participants) =>
            InvokeNativePower((HextechBurnPower)power, simulator, model =>
                model.AfterSideTurnStartForParticipants(side, participants, simulator.State.CombatState)));

        RegisterOwnerDebuffRune<TormentorRune>(harmony);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(TormentorRune), "AfterPowerAmountChanged"));

        RegisterNativePowerCard<TrickMagicCard>(harmony, Site(NativeDraw, nameof(Draw)),
            SingleNativePowerSite<BufferPower>(), SingleNativePowerSite<HextechAttackReplayPower>());
        RuneMirrors.RegisterNativeBase<ClownCollegeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechAttackReplayPower), "AfterModifyingCardPlayCount"),
            Site(AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]), nameof(RemoveNativePower)));
        RegisterNativeSoulPower<HextechAttackReplayPower>();
        ModifyCardPlayCountMirrors.Registry.Register<HextechAttackReplayPower>((power, context) =>
        {
            var previous = _simulator;
            var previousCard = _nativeBeforePlayCard;
            _simulator = context.Simulator; _nativeBeforePlayCard = context.Card;
            try { return power.ModifyCardPlayCount(context.Card.MutablePreview, context.Target, context.PlayCount); }
            finally { _simulator = previous; _nativeBeforePlayCard = previousCard; }
        });
        ModifyCardPlayCountMirrors.AfterRegistry.Register<HextechAttackReplayPower>((power, context) =>
            InvokeNativePower(power, context.Simulator, model =>
                model.AfterModifyingCardPlayCount(context.Card.MutablePreview)));

        RegisterNativePowerCard<InfernalDragonSoulCard>(harmony, SingleNativePowerSite<HextechInfernalDragonSoulPower>());
        RuneMirrors.RegisterNativeBase<InfernalDragonSoulRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechInfernalDragonSoulPower), "AfterCardPlayed"),
            Site(alive, nameof(NativeBranchIsAlive)), ManyNativePowerSite<HextechBurnPower>());
        // This is an iterator, so its MoveNext (not its factory) owns the reads.
        var iterator = typeof(HextechInfernalDragonSoulPower).GetNestedTypes(BindingFlags.NonPublic)
            .Single(t => t.Name.StartsWith("<GetTargets>", StringComparison.Ordinal));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(iterator, "MoveNext"),
            Site(alive, nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatProcTracker), "TryGetNetworkLedger"),
            Site(AccessTools.Method(typeof(HextechPlayerContextHelper), "IsNetworkMultiplayerRun"), nameof(NativeProcIsMultiplayer)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatProcTracker), "HasOwnerTurnProcTriggered"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatProcTracker), "TryConsumeOwnerTurnProc"));
        if (InfernalTriggered.FieldType != typeof(bool))
            throw new InvalidOperationException("Pinned Infernal soul private state changed.");
        PowerHiddenStateMirrors.Register<HextechInfernalDragonSoulPower>(InfernalTriggered.Name,
            (_, power) => (bool)InfernalTriggered.GetValue(power)! ? 1L : 0L);
        RegisterNativeSoulPower<HextechInfernalDragonSoulPower>();
        AfterCardPlayedMirrors.Registry.Register<HextechInfernalDragonSoulPower>((power, context) =>
            InvokeNativePower(power, context.Simulator, model =>
                model.AfterCardPlayed(new ThrowingPlayerChoiceContext(), context.CardPlay)));
        NativeAfterSidePowers.Add(typeof(HextechInfernalDragonSoulPower), (power, simulator, side, participants) =>
            InvokeNativePower((HextechInfernalDragonSoulPower)power, simulator, model =>
                model.AfterSideTurnStartForParticipants(side, participants, simulator.State.CombatState)));
    }

    private static bool NativeProcIsMultiplayer()
    {
        if (_simulator is null) return HextechPlayerContextHelper.IsNetworkMultiplayerRun();
        if (_simulator.State.CombatState.Players.Count != 1)
            throw new PredictionUnsupportedException("Native owner-turn proc supports captured single-player combat only.");
        return false;
    }

    private static void NativeBurnVisual(Creature owner)
    {
        if (_simulator is null) HextechBurnVisual.PlayTickBurst(owner);
    }

    private static Task<IEnumerable<DamageResult>> NativeBurnDamage(PlayerChoiceContext context, Creature target,
        decimal amount, ValueProp props, CardModel? source, CardPlay? play)
    {
        if (_simulator is null) return CreatureCmd.Damage(context, target, amount, props, source, play);
        var card = source is null ? null : _simulator.State.FindCard(source)
            ?? throw new PredictionUnsupportedException("Native Burn card source was not captured.");
        var results = _simulator.Damage([target], amount, props, null, card, play);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<DamageResult>>(results);
    }
}
