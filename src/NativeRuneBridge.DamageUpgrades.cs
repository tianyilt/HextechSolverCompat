using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Func<Func<Task>, Task> OriginalOutbreakGuardRunner =
        AccessTools.Method(typeof(HextechCombatHooks), "RunWithOutbreakPowerPoisonResponseGuard").CreateDelegate<Func<Func<Task>, Task>>();
    private static readonly Func<Func<Task>, Task> OriginalSleightGuardRunner =
        AccessTools.Method(typeof(HextechCombatHooks), "RunWithSleightOfFleshPowerDebuffResponseGuard").CreateDelegate<Func<Func<Task>, Task>>();

    private static void RegisterNativeDamageUpgrades(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SerpentsFangRune), "AfterDamageGiven"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<PoisonPower>());
        RegisterNativeDamageHook<SerpentsFangRune>();
        foreach (var name in new[] { "RunWithOutbreakPowerPoisonResponseGuard", "RunWithSleightOfFleshPowerDebuffResponseGuard",
                     "get_IsResolvingOutbreakPowerPoisonResponse", "get_IsResolvingSleightOfFleshPowerDebuffResponse" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatHooks), name));

        var infernalLambdas = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(InfernalConduitRune), "<>c"));
        PatchEventCallback(harmony, infernalLambdas.Single(method => method.Name.Contains("BeforeTurnEnd") && method.ReturnType == typeof(bool)),
            Site(alive, nameof(NativeBranchIsAlive)));
        PatchEventCallback(harmony, infernalLambdas.Single(method => method.Name.Contains("BeforeTurnEnd") && method.ReturnType == typeof(int)),
            new NativeCallSite(NativePowerAmount.MakeGenericMethod(typeof(HextechBurnPower)), BranchPowerAmount.MakeGenericMethod(typeof(HextechBurnPower)), 1));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(InfernalConduitRune), "BeforeTurnEnd"),
            Site(combat, nameof(NativeBranchCombat), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(InfernalConduitRune), "AfterDamageGiven"),
            SingleNativePowerSite<HextechBurnPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(InfernalConduitRune), "AfterPlayerTurnStartEarly"),
            Site(AccessTools.Method(typeof(PlayerCmd), "GainEnergy"), nameof(GainNativeEnergy)));
        RegisterState<InfernalConduitRune>(); RegisterNativeEndTurn<InfernalConduitRune>();
        AfterDamageGivenMirrors.Registry.Register<InfernalConduitRune>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterDamageGiven(new ThrowingPlayerChoiceContext(), context.Dealer, context.Result,
                context.Props, context.Target, context.Source?.MutablePreview)), typeof(InfernalConduitRune)));
        AfterPlayerTurnStartMirrors.RegisterEarly<InfernalConduitRune>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterPlayerTurnStartEarly(new ThrowingPlayerChoiceContext(), context.Player)), typeof(InfernalConduitRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BoneBreakUpgradeRune), "BeforeCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "IsOstyAlive"), nameof(NativeBranchOstyAlive)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "Osty"), nameof(NativeBranchOsty)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "MaxHp"), nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BoneBreakUpgradeRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechGameApiCompat)).Single(method => method.Name == "Damage"
                && method.GetParameters().Length == 7 && method.GetParameters()[1].ParameterType == typeof(IEnumerable<Creature>)), nameof(NativeDiveDamage)),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        RegisterNativeBeforeAfterCard<BoneBreakUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BorrowedTimeUpgradeRune), "BeforeCardPlayed"),
            new NativeCallSite(NativePowerAmount.MakeGenericMethod(typeof(BorrowedTimePower)), BranchPowerAmount.MakeGenericMethod(typeof(BorrowedTimePower)), 1));
        var getBorrowed = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPower"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 0).MakeGenericMethod(typeof(BorrowedTimePower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BorrowedTimeUpgradeRune), "AfterCardPlayed"),
            new(getBorrowed, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(BorrowedTimePower)), 1),
            SingleNativePowerSite<BorrowedTimePower>());
        RegisterNativeBeforeAfterCard<BorrowedTimeUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(UnleashUpgradeRune), "AfterDamageGiven"),
            Site(dead, nameof(NativeBranchIsDead)), Site(AccessTools.Method(typeof(OstyCmd), "Summon",
                [typeof(PlayerChoiceContext), typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        RegisterNativeDamageHook<UnleashUpgradeRune>();

        PatchEventCallback(harmony, AccessTools.Method(typeof(FallingStarUpgradeRune), "GetTargets"), Site(combat, nameof(NativeBranchCombat), 2));
        PatchEventCallback(harmony, AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(FallingStarUpgradeRune), "<>c"))
            .Single(method => method.Name.Contains("AfterCardPlayed") && method.ReturnType == typeof(bool)), Site(dead, nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FallingStarUpgradeRune), "AfterCardPlayed"),
            Site(AccessTools.Method(typeof(CreatureCmd), "Stun", [typeof(Creature), typeof(string)]), nameof(StunNativeCreature)));
        RegisterAfterCardPlayed<FallingStarUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MiseryUpgradeRune), "AfterCardPlayed"),
            Site(combat, nameof(NativeBranchCombat), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), "Powers"), nameof(NativeRemovalPowers)),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
                && !method.IsGenericMethodDefinition), nameof(ApplyNativeCopiedDebuff)));
        RegisterAfterCardPlayed<MiseryUpgradeRune>();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRuneApiCompat), "PrepareTemporaryPowerReapply"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SnakebiteUpgradeRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)), Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeBranchPileCards)),
            Site(AccessTools.Method(typeof(CardCmd), "Preview", [typeof(CardModel), typeof(float), typeof(CardPreviewStyle)]), nameof(PreviewNativeRetainedCard)));
        RegisterState<SnakebiteUpgradeRune>(); RegisterNativeEndTurn<SnakebiteUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(NeutralizeUpgradeRune), "<>c"))
            .Single(method => method.Name.Contains("AfterCardDiscarded") && method.ReturnType == typeof(bool)), Site(dead, nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NeutralizeUpgradeRune), "AfterCardDiscarded"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat), 2),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "IsInProgress"), nameof(NativeBranchProgress)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding)),
            Site(AccessTools.Method(typeof(CardModel), "CreateClone"), nameof(CloneNativeGeneratedCard)),
            Site(AccessTools.Method(typeof(CardPileCmd), "Add", [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(AddNativeRawCard)),
            Site(AccessTools.Method(typeof(HextechAutoPlayHelper), "AutoPlayTransientCardAndCleanup"), nameof(AutoPlayNativeTransientCard)));
        RegisterDiscardReaction<NeutralizeUpgradeRune>();
        // These change native card upgrade limits and post-victory deck growth.
        // A combat root retains the exact existing levels; actual victory keeps
        // the original helper, without speculative writes to the live deck.
        RegisterState<StrikeUpgradeRune>(); RuneMirrors.RegisterNativeBase<StrikeUpgradeRune>();
        RegisterState<DefendUpgradeRune>(); RuneMirrors.RegisterNativeBase<DefendUpgradeRune>();
        var maxUpgrade = AccessTools.PropertyGetter(typeof(CardModel), "MaxUpgradeLevel");
        NativeCallbackContracts.AddNativePostfix(maxUpgrade, AccessTools.Method(
            AccessTools.Inner(typeof(HextechStarterUpgradeHooks), "MaxUpgradeLevelPatch"), "Postfix"), "Natsuki.HextechRunes", Priority.Normal);
    }

    private static void RegisterNativeBeforeAfterCard<T>() where T : HextechRelicBase
    {
        RegisterAfterCardPlayed<T>();
        BeforeCardPlayedMirrors.Registry.Register<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.BeforeCardPlayed(context.CardPlay)), typeof(T)));
    }
    private static Task StunNativeCreature(Creature creature, string? nextMoveId)
    {
        if (_simulator is not { } sim) return CreatureCmd.Stun(creature, nextMoveId);
        ((SimulatedCombatState)sim.State.CombatState).ForceStunnedMove(creature, nextMoveId);
        return Task.CompletedTask;
    }
    private static Task ApplyNativeCopiedDebuff(PowerModel power, Creature target, decimal amount,
        Creature? applier, CardModel? source, bool silent)
    {
        if (_simulator is not { } sim) return HextechPowerCmdCompat.Apply(power, target, amount, applier, source, silent);
        if (amount != decimal.Truncate(amount) || amount < int.MinValue || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Copied native debuff amount must be integral.");
        var combat = (SimulatedCombatState)sim.State.CombatState;
        combat.ApplyPowerFromSource(power.GetType(), target, (int)amount, applier, source);
        PowerLifecycleSupport.ResolvePowerAmountChanges(sim, combat); PauseNativeChoice(sim);
        return Task.CompletedTask;
    }
    private static TaskCompletionSource? PreviewNativeRetainedCard(CardModel card, float duration, CardPreviewStyle style)
    {
        if (_simulator is null) return CardCmd.Preview(card, duration, style);
        var complete = new TaskCompletionSource(); complete.SetResult(); return complete;
    }
    internal static void RunNativeOutbreakBody(CardOnPlayMirrorContext context)
    {
        RequireCompleted(OriginalOutbreakGuardRunner(() =>
        {
            CardOnPlayMirrors.Registry.Invoke((Outbreak)context.Card.MutablePreview, context);
            if (context.Simulator.HasPendingChoice)
                context.Simulator.AppendExecutionContinuation(new CardOnPlayMirrors.CardSpecExecutionFrame(context.Card, context.CardPlay.Target));
            else CardOnPlayMirrors.ApplyRemainingCardSpec(context.Simulator, context.Card, context.CardPlay.Target);
            return Task.CompletedTask;
        }), typeof(Outbreak));
        FlushNativeInstantDoom(context.Simulator);
    }
}
