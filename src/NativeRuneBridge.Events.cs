using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record NativeCallSite(MethodInfo Original, MethodInfo Replacement, int Count);
    private static readonly Dictionary<MethodBase, NativeCallSite[]> EventCallSites = [];

    private static NativeCallSite Site(MethodInfo original, string replacement, int count = 1)
        => new(original, AccessTools.Method(typeof(NativeRuneBridge), replacement), count);

    private static void PatchEventCallback(Harmony harmony, MethodInfo callback, params NativeCallSite[] sites)
    {
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        MethodBase target = machine is null ? callback : AccessTools.Method(machine, "MoveNext");
        if (machine is not null) NativeCallbackContracts.Add(callback);
        EventCallSites.Add(target, sites);
        NativeCallbackContracts.Add(target, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)));
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)));
    }

    private static void RegisterNativeEventFamily(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var pile = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile));
        var ending = AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RenewalRune), "AfterCardDiscarded"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeDraw, nameof(Draw)));
        RegisterDiscardReaction<RenewalRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NowYouSeeMeRune), "AfterCardDiscarded"),
            Site(dead, nameof(NativeBranchIsDead)), Site(pile, nameof(NativeBranchCardPile)),
            Site(ending, nameof(NativeBranchEnding)),
            Site(AccessTools.Method(typeof(CombatManager), "IsPartOfPlayerTurn"), nameof(NativeBranchPlayerTurn)),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Exhaust),
                [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool), typeof(bool)]), nameof(ExhaustNativeCard)));
        RegisterDiscardReaction<NowYouSeeMeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AdaptiveCapacitorRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.AddSlots)), nameof(AddNativeOrbSlots)));
        RegisterState<AdaptiveCapacitorRune>();
        RuneMirrors.RegisterNativeBase<AdaptiveCapacitorRune>();
        AfterPlayerTurnStartMirrors.Register<AdaptiveCapacitorRune>((relic, context) => RequireCompleted(
            Invoke(relic, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(AdaptiveCapacitorRune)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(UnsealedThroneRune), "HandleStarsChanged"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        RegisterStarsReaction<UnsealedThroneRune>();
        PatchEventCallback(harmony, AccessTools.Method(typeof(RoyalCommandRune), "HandleStarsChanged"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(ForgeCmd), nameof(ForgeCmd.Forge),
                [typeof(decimal), typeof(Player), typeof(AbstractModel)]), nameof(ForgeNativeBlade)));
        RegisterStarsReaction<RoyalCommandRune>();
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition
                && method.GetParameters()[0].ParameterType == typeof(Creature)).MakeGenericMethod(typeof(VigorPower));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ChargeUpRune), "AfterStarsSpent"),
            Site(dead, nameof(NativeBranchIsDead)), new(apply,
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(typeof(VigorPower)), 1));
        RegisterState<ChargeUpRune>();
        RuneMirrors.RegisterNativeBase<ChargeUpRune>();
        PatchEventCallback(harmony, AccessTools.Method(typeof(VampireCrawlerRune), "ShouldCopyPlayedPowerToDiscard"),
            Site(ending, nameof(NativeBranchEnding)), Site(pile, nameof(NativeBranchCardPile)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), nameof(NativeBranchProgress)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(VampireCrawlerRune), "AfterCardPlayed"),
            Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.Method(typeof(ICombatState), nameof(ICombatState.CloneCard)), nameof(CloneNativeCombatCard)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(AddNativeRawCard)));
        RegisterAfterCardPlayed<VampireCrawlerRune>();
        harmony.Patch(AccessTools.Method(typeof(PowerLifecycleSupport), "AfterStarsSpent"),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(DispatchNativeStarsSpent)));
        NativeCallbackContracts.Install(harmony);
        NativeCallbackContracts.Validate();
    }

    private static void RegisterDiscardReaction<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterCardDiscardedMirrors.Registry.Register<T>((relic, context) => RequireCompleted(
            Invoke(relic, context.Simulator, model => model.AfterCardDiscarded(new ThrowingPlayerChoiceContext(), context.MutablePreviewCard)), typeof(T)));
    }

    private static void RegisterStarsReaction<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), "AfterStarsGained"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), "AfterStarsSpent"));
        AfterStarsGainedMirrors.Registry.Register<T>((relic, context) => RequireCompleted(
            Invoke(relic, context.Simulator, model => model.AfterStarsGained(context.Amount, context.Gainer)), typeof(T)));
    }

    private static IEnumerable<CodeInstruction> RewriteEventCallSites(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var sites = EventCallSites[__originalMethod];
        int[] found = new int[sites.Length];
        foreach (var instruction in instructions)
        {
            for (int index = 0; index < sites.Length; index++)
            {
                if (!instruction.Calls(sites[index].Original)) continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = sites[index].Replacement;
                found[index]++;
                break;
            }
            yield return instruction;
        }
        for (int index = 0; index < sites.Length; index++)
            if (found[index] != sites[index].Count)
                throw new InvalidOperationException($"Native event site changed: {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name} {sites[index].Original} expected={sites[index].Count} actual={found[index]}.");
    }

    private static ICombatState? NativeBranchCombat(Creature creature)
        => _simulator?.State.CombatState ?? creature.CombatState;
    private static bool NativeBranchEnding(CombatManager manager)
        => _simulator is { } sim ? sim.IsOverOrEnding || sim.HasPendingChoice : manager.IsOverOrEnding;
    private static bool NativeBranchProgress(CombatManager manager)
        => _simulator is { } sim ? sim.IsInProgress : manager.IsInProgress;
    private static bool NativeBranchPlayerTurn(CombatManager manager, Player player)
        => _simulator is { } sim ? sim.State.CombatState.CurrentSide == CombatSide.Player
            && sim.State.CombatState.Players.Contains(player) : manager.IsPartOfPlayerTurn(player);

    private static Task<CardPileAddResult?> ExhaustNativeCard(PlayerChoiceContext context, CardModel card, bool ethereal, bool skipVisuals)
    {
        if (_simulator is null) return CardCmd.Exhaust(context, card, ethereal, skipVisuals);
        if (_simulator.IsOverOrEnding) return Task.FromResult<CardPileAddResult?>(null);
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native exhaust card is absent in its branch.");
        var oldPile = NativeBranchCardPile(card);
        _simulator.Exhaust(predicted, ethereal);
        PauseNativeChoice(_simulator);
        return Task.FromResult<CardPileAddResult?>(new CardPileAddResult { success = true, cardAdded = card,
            oldPile = oldPile, targetPile = PileType.Exhaust, modifyingModels = [] });
    }

    private static Task AddNativeOrbSlots(Player player, int amount)
    {
        if (_simulator is null) return OrbCmd.AddSlots(player, amount);
        _simulator.AddOrbSlots(player, amount);
        return Task.CompletedTask;
    }

    private static CardModel CloneNativeCombatCard(ICombatState combat, CardModel card)
    {
        if (_simulator is null) return combat.CloneCard(card);
        var source = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native combat clone source is absent in the branch.");
        // CombatState.CloneCard keeps native AfterCloned resets, but does not
        // set CreateClone's clone-of identity or reset ExhaustOnNextPlay.
        var clone = PredictionUtils.CloneModelForSimulation(source.MutablePreview);
        PredictionModModelSupport.CloneCardAttachedModels(source.Preview, clone);
        return clone;
    }

    private static Task<IEnumerable<SovereignBlade>> ForgeNativeBlade(decimal amount, Player player, AbstractModel? source)
    {
        if (_simulator is null) return ForgeCmd.Forge(amount, player, source);
        return Task.FromResult(ForgeOwnedNativeBlade(_simulator, amount, player, source));
    }

    private static bool DispatchNativeStarsSpent(CombatPredictionSimulator simulator, SimulatedCombatState combat, PredictedCard card, int amount)
    {
        if (!combat.Players.SelectMany(combat.RelicsOf).Any(relic => relic is UnsealedThroneRune or RoyalCommandRune or ChargeUpRune or TrinityRune or GalacticGiftRune)) return true;
        if (amount <= 0 || simulator.IsOverOrEnding) return false;
        var player = card.Preview.Owner;
        // Snapshot the native listener membership once, then dispatch every
        // command separately in that order (rather than grouping relic types).
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is RelicModel { IsMelted: true }) continue;
            switch (listener)
            {
                case UnsealedThroneRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterStarsSpent(amount, player)), rune.GetType()); break;
                case RoyalCommandRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterStarsSpent(amount, player)), rune.GetType()); break;
                case ChargeUpRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterStarsSpent(amount, player)), rune.GetType()); break;
                case TrinityRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterStarsSpent(amount, player)), rune.GetType()); break;
                case GalacticGiftRune rune:
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterStarsSpent(amount, player)), rune.GetType()); break;
                case MiniRegent relic when relic.Owner == player:
                    var mini = combat.GetStatefulRelicState(relic);
                    if (mini.Current == 0)
                    {
                        combat.SetStatefulRelicState(relic, mini with { Current = 1 });
                        combat.Apply<StrengthPower>(player.Creature, relic.DynamicVars.Strength.IntValue, player.Creature);
                    }
                    break;
                case GalacticDust relic when relic.Owner == player:
                    var dust = combat.GetStatefulRelicState(relic);
                    int total = dust.Current + amount, threshold = relic.DynamicVars.Stars.IntValue;
                    combat.SetStatefulRelicState(relic, dust with { Current = total % threshold });
                    for (int proc = 0; proc < total / threshold; proc++)
                    {
                        simulator.GainBlock(player.Creature, relic.DynamicVars.Block.IntValue, ValueProp.Unpowered);
                        if (simulator.HasPendingChoice) break;
                    }
                    break;
                case ChildOfTheStarsPower power when power.Owner.Player == player && power.Amount > 0:
                    simulator.GainBlock(power.Owner, power.Amount * amount, ValueProp.Unpowered); break;
                default:
                    if (AccessTools.Method(listener.GetType(), nameof(AbstractModel.AfterStarsSpent)).DeclaringType != typeof(AbstractModel))
                        throw new PredictionUnsupportedException($"Unreviewed AfterStarsSpent callback: {listener.GetType().Name}.");
                    break;
            }
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); return false; }
        }
        return false;
    }
}
