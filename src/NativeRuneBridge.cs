using System.Reflection;
using System.Globalization;
using System.Collections.Concurrent;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Commands;

namespace HextechSolverCompat;

// Reuse the reviewed synchronous native callback on a detached, forkable model.
// This is deliberately separate from the shared read-only root relic identity.
internal sealed partial class NativeRuneState(HextechRelicBase model) : IPredictionStateForkable, IPredictionForkBoundary
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();
    internal readonly HextechRelicBase Model = model;
    internal MysterySeedState? StableGeneration;
    internal int? FrozenDeckCount;
    internal SimCreatureState? NearDeathCreature;
    internal NativeGeneratedCardContext? GeneratedTags;
    internal int RootFinishedShivHistory;
    internal int FinishedShivHistory;
    internal NativeSelfUpgradeState? SelfUpgrades;
    internal IReadOnlyDictionary<Creature, int>? SoulBirthHps;
    internal IReadOnlyList<NativeRuneBridge.FrozenOrbChannel>? FrozenOrbChannels;
    private PredictedCard?[] _pendingCards = [];
    private readonly Dictionary<FieldInfo, PredictedCard?> _cardFields = [];
    private readonly Dictionary<PredictedCard, bool> _replayRollCards = [];
    internal static readonly FieldInfo FanCard = AccessTools.DeclaredField(typeof(FanTheHammerRune), "_damageReducedCard");
    private static readonly FieldInfo ReplayRolls = AccessTools.DeclaredField(typeof(JeweledGauntletRune), "_pendingReplayRolls");
    private static bool IsReplayRolls(FieldInfo field) => field == ReplayRolls;
    private static readonly FieldInfo PendingQueue = AccessTools.DeclaredField(typeof(SellOffRune), "_pendingDiscardedCards");
    private static readonly FieldInfo CreditedExecutions = AccessTools.DeclaredField(typeof(CollectorRune), "_creditedExecutions");
    private static bool IsReviewedExecutions(FieldInfo field) => field == CreditedExecutions;
    private static bool IsReviewedQueue(FieldInfo field) => field == PendingQueue;
    private static bool IsConversionCard(FieldInfo field) => field == FanCard || field.FieldType == typeof(CardModel)
        && (field.DeclaringType == typeof(AttributeConversionRelicBase) && field.Name == "_pendingCardSource"
            || field.DeclaringType == typeof(DecisionsDecisionsUpgradeRune) && field.Name == "_pendingReplayCard");
    private static bool IsConversionCreature(FieldInfo field) => field.DeclaringType == typeof(AttributeConversionRelicBase)
        && field.Name == "_pendingApplier" && field.FieldType == typeof(Creature);
    private static bool IsReviewedColorRewardId(FieldInfo field)
        => field.DeclaringType == typeof(ColorDiscoveryRune) && field.Name == "_pendingRewardCardId"
            && field.FieldType == typeof(ModelId);
    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(decimal)
        || type == typeof(string) || Nullable.GetUnderlyingType(type) is { } underlying && IsScalar(underlying);
    internal static HextechRelicBase Clone(HextechRelicBase source)
    {
        var clone = PredictionUtils.CloneModelForSimulation(source);
        // MemberwiseClone retains observers on relics. Detach those just as the
        // solver already does for card previews, so simulated status changes
        // cannot notify observers attached to the live relic.
        for (Type? type = source.GetType(); type is not null && typeof(AbstractModel).IsAssignableFrom(type); type = type.BaseType)
            foreach (EventInfo observer in type.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                AccessTools.DeclaredField(type, observer.Name)?.SetValue(clone, null);
        if (source is SellOffRune)
            PendingQueue.SetValue(clone, new Queue<CardModel>((Queue<CardModel>)PendingQueue.GetValue(source)!));
        if (source is CollectorRune)
            CreditedExecutions.SetValue(clone, new HashSet<Creature>((HashSet<Creature>)CreditedExecutions.GetValue(source)!, ReferenceEqualityComparer.Instance));
        if (source is JeweledGauntletRune)
            ReplayRolls.SetValue(clone, new Dictionary<CardModel, bool>((Dictionary<CardModel, bool>)ReplayRolls.GetValue(source)!, ReferenceEqualityComparer.Instance));
        CloneCardLedgers(source, clone);
        CloneNativeDamageQueues(source, clone);
        DetachNativePlayerNatureTimer(source, clone);
        AssertSweepingContextEmpty(source);
        return clone;
    }
    internal static FieldInfo[] Fields(Type type) => FieldCache.GetOrAdd(type, InspectFields);
    private static FieldInfo[] InspectFields(Type type)
    {
        List<FieldInfo> fields = [];
        for (Type? current = type; current is not null && current.Assembly == typeof(ModEntry).Assembly; current = current.BaseType)
            fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        foreach (var field in fields)
            if (!IsReviewedQueue(field) && !IsReplayRolls(field) && !IsReviewedExecutions(field) && !IsConversionCard(field) && !IsConversionCreature(field) && !IsCardLedger(field)
                && !IsSweepingContext(field) && !IsNativeDamagePending(field) && !IsReviewedNatureTimer(field) && !IsReviewedColorRewardId(field) && !IsScalar(field.FieldType) && !typeof(ICombatState).IsAssignableFrom(field.FieldType))
                throw new NotSupportedException($"Native scalar-state bridge cannot detach {type.Name}.{field.Name}: {field.FieldType}.");
        return fields.OrderBy(f => f.DeclaringType!.FullName, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
    }
    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        var copy = new NativeRuneState(Clone(Model)) { StableGeneration = StableGeneration, FrozenDeckCount = FrozenDeckCount,
            SelfUpgrades = (NativeSelfUpgradeState?)SelfUpgrades?.Fork(context), SoulBirthHps = SoulBirthHps,
            FrozenOrbChannels = FrozenOrbChannels, RootFinishedShivHistory = RootFinishedShivHistory, FinishedShivHistory = FinishedShivHistory };
        if (NearDeathCreature is not null) { copy.NearDeathCreature = context.RequireRemap(NearDeathCreature); NativeRuneBridge.BindNativeNearPlayer(copy, copy.NearDeathCreature); }
        copy.GeneratedTags = (NativeGeneratedCardContext?)GeneratedTags?.Fork(context);
        copy._pendingCards = _pendingCards.Select(card => card is null ? null : context.RequireRemap(card)).ToArray();
        foreach (var (field, card) in _cardFields)
        {
            var remapped = card is null ? null : context.RequireRemap(card);
            copy._cardFields.Add(field, remapped);
            field.SetValue(copy.Model, remapped?.MutablePreview);
        }
        foreach (var (card, roll) in _replayRollCards) copy._replayRollCards.Add(context.RequireRemap(card), roll);
        copy.RebindReplayRolls();
        copy.RebindQueue();
        ForkCardLedgers(copy, context);
        context.Register(Model, copy.Model);
        return copy;
    }
    public void AssertForkable()
    {
        AssertNativeDamageQueuesEmpty(Model);
        AssertSweepingContextEmpty(Model);
        if (Model is AttributeConversionRelicBase && Fields(Model.GetType()).Any(field =>
            field.DeclaringType == typeof(AttributeConversionRelicBase)
            && (field.Name == "_pendingAmount" && field.GetValue(Model) is not null
                || field.Name == "_isConverting" && field.GetValue(Model) is true)))
            throw new PredictionUnsupportedException("Cannot fork inside the native attribute conversion command.");
    }
    internal void CaptureQueue(CombatPredictionSimulator simulator)
    {
        if (GeneratedTags is null && Model is BigKnifeRune or InkshadowRune or DeviantCognitionRune
            && !simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().Any())
            GeneratedTags = new NativeGeneratedCardContext(simulator, Model.Owner);
        if (Model is NearDeathFeastRune && NearDeathCreature is null) { NearDeathCreature = simulator.State.GetCreature(Model.Owner.Creature); NativeRuneBridge.BindNativeNearPlayer(this, NearDeathCreature); }
        CaptureCardLedgers(simulator);
        foreach (var field in Fields(Model.GetType()).Where(IsConversionCard))
        {
            var model = (CardModel?)field.GetValue(Model);
            var card = model is null ? null : simulator.State.FindCard(model)
                ?? throw new PredictionUnsupportedException("Native conversion source references an absent branch card.");
            _cardFields[field] = card;
            field.SetValue(Model, card?.MutablePreview);
        }
        if (Model is JeweledGauntletRune)
        {
            _replayRollCards.Clear();
            foreach (var (model, roll) in (Dictionary<CardModel, bool>)ReplayRolls.GetValue(Model)!)
                _replayRollCards.Add(simulator.State.FindCard(model)
                    ?? throw new PredictionUnsupportedException("Native replay query references an absent branch card."), roll);
            RebindReplayRolls();
        }
        if (Model is not SellOffRune) return;
        _pendingCards = ((Queue<CardModel>)PendingQueue.GetValue(Model)!).Select(card =>
            simulator.State.FindCard(card) ?? throw new PredictionUnsupportedException("Native discard queue references an absent branch card.")).ToArray();
        RebindQueue();
    }
    private void RebindReplayRolls()
    {
        if (Model is JeweledGauntletRune)
        {
            var rolls = new Dictionary<CardModel, bool>(ReferenceEqualityComparer.Instance);
            foreach (var (card, roll) in _replayRollCards) rolls.Add(card.MutablePreview, roll);
            ReplayRolls.SetValue(Model, rolls);
        }
    }
    private void RebindQueue()
    {
        if (Model is SellOffRune)
            PendingQueue.SetValue(Model, new Queue<CardModel>(_pendingCards.Select(card => card!.MutablePreview)));
    }
    private static void WriteScalars<T>(T model, ref ModelPredictionStateWriter writer) where T : HextechRelicBase
    {
        if (model is HextechForgeBase forge) writer.Add("nativeForgeStackCount", forge.StackCount);
        foreach (var field in Fields(model.GetType()))
        {
            object? value = field.GetValue(model);
            string name = field.DeclaringType!.Name + "." + field.Name;
            if (IsReviewedNatureTimer(field))
            {
                // External clock input is invalidated/replanned by the native
                // timer event epoch; no Godot node belongs to a searched fork.
                writer.Add(name, (string?)null);
                continue;
            }
            if (IsSweepingContext(field))
            {
                AssertSweepingContextEmpty(model);
                writer.Add(name, (string?)null);
                continue;
            }
            if (IsNativeDamagePending(field))
            {
                AssertNativeDamageQueuesEmpty(model);
                writer.Add(name + ":count", 0);
                continue;
            }
            if (IsReviewedQueue(field) || IsConversionCard(field) || IsReplayRolls(field) || IsCardLedger(field)) continue;
            if (IsReviewedExecutions(field))
            {
                var targets = (HashSet<Creature>)value!;
                writer.Add(name + ":count", targets.Count);
                foreach (var target in targets.OrderBy(creature => creature.CombatId))
                    writer.Add(name + ":" + target.CombatId, true);
                continue;
            }
            if (IsConversionCreature(field)) writer.Add(name, ((Creature?)value)?.CombatId?.ToString(CultureInfo.InvariantCulture));
            else if (typeof(ICombatState).IsAssignableFrom(field.FieldType)) writer.Add(name, value is not null);
            else writer.Add(name, value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        foreach (var variable in model.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Add("var:" + variable.Key, variable.Value.BaseValue.ToString(CultureInfo.InvariantCulture));
    }
    internal static void WriteModel<T>(T model, ref ModelPredictionStateWriter writer) where T : HextechRelicBase
    {
        WriteScalars(model, ref writer);
        WriteLiveCardLedgers(model, ref writer);
        if (model is ChainInSleeveRune) writer.Add("finishedShivHistory", NativeRuneBridge.CountLiveFinishedShivs(model.Owner));
        foreach (var field in Fields(model.GetType()).Where(IsConversionCard))
            writer.AddCards(field.DeclaringType!.Name + "." + field.Name, [(CardModel?)field.GetValue(model)]);
        if (model is ClawUpgradeRune or SowUpgradeRune or ReapUpgradeRune or IronWaveUpgradeRune or SolidTimeRune
            || model is AnthonyBiasRune && NativeSelfUpgradeState.NeedsPermanentDeck(model.Owner))
            NativeSelfUpgradeState.WriteLive(model.Owner, ref writer);
        if (model is AnthonyBiasRune) writer.Add("frozenDeckCount", model.Owner.Deck.Cards.Count);
        if (model is SellOffRune)
            writer.AddCards("discardQueue", ((Queue<CardModel>)PendingQueue.GetValue(model)!).Cast<CardModel?>().ToArray());
        if (model is JeweledGauntletRune)
        {
            var rolls = (Dictionary<CardModel, bool>)ReplayRolls.GetValue(model)!;
            writer.AddCards("replayRolls:false", rolls.Where(pair => !pair.Value).Select(pair => (CardModel?)pair.Key).ToArray(), unordered: true);
            writer.AddCards("replayRolls:true", rolls.Where(pair => pair.Value).Select(pair => (CardModel?)pair.Key).ToArray(), unordered: true);
        }
        if (model is SoulEaterRune) NativeRuneBridge.WriteSoulBirthHps(null, ref writer);
    }
    internal static void WriteState(NativeRuneState state, ref ModelPredictionStateWriter writer)
    {
        WriteScalars(state.Model, ref writer);
        state.WriteCardLedgers(ref writer);
        foreach (var field in Fields(state.Model.GetType()).Where(IsConversionCard))
            writer.AddCards(field.DeclaringType!.Name + "." + field.Name, [state._cardFields[field]]);
        state.SelfUpgrades?.Write(ref writer);
        if (state.FrozenDeckCount is { } deckCount) writer.Add("frozenDeckCount",
            state.SelfUpgrades is { CapturesPermanentDeck: true } deck ? deck.DeckCards.Count : deckCount);
        if (state.Model is ChainInSleeveRune) writer.Add("finishedShivHistory", state.FinishedShivHistory);
        if (state.Model is SellOffRune) writer.AddCards("discardQueue", state._pendingCards);
        if (state.Model is JeweledGauntletRune)
        {
            writer.AddCards("replayRolls:false", state._replayRollCards.Where(pair => !pair.Value).Select(pair => (PredictedCard?)pair.Key).ToArray(), unordered: true);
            writer.AddCards("replayRolls:true", state._replayRollCards.Where(pair => pair.Value).Select(pair => (PredictedCard?)pair.Key).ToArray(), unordered: true);
        }
        if (state.StableGeneration is { } seed) MysterySeedState.Write(seed, ref writer);
        if (state.SoulBirthHps is not null) NativeRuneBridge.WriteSoulBirthHps(state.SoulBirthHps, ref writer);
        if (state.FrozenOrbChannels is not null) NativeRuneBridge.WriteFrozenOrbChannels(state.FrozenOrbChannels, ref writer);
    }
}

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static CombatPredictionSimulator? _simulator;
    private static readonly FieldInfo TurnScopeState = AccessTools.Field(typeof(HextechRelicBase), "_turnScopedCombatState");
    private static readonly MethodInfo NativeDraw = AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Draw),
        [typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool)]);
    private static readonly MethodInfo BranchDraw = AccessTools.Method(typeof(NativeRuneBridge), nameof(Draw));
    private static readonly MethodInfo NativePowerAmount = typeof(Creature).GetMethods().Single(
        m => m.Name == nameof(Creature.GetPowerAmount) && m.IsGenericMethodDefinition);
    private static readonly MethodInfo BranchPowerAmount = AccessTools.Method(typeof(NativeRuneBridge), nameof(GetPowerAmount));

    internal static void Register(Harmony harmony)
    {
        PatchGetter(harmony, typeof(Creature), nameof(Creature.CombatState), nameof(CombatStateGetter));
        PatchGetter(harmony, typeof(CardModel), nameof(CardModel.CombatState), nameof(CardCombatStateGetter));
        PatchGetter(harmony, typeof(Creature), nameof(Creature.CurrentHp), nameof(HpGetter));
        PatchGetter(harmony, typeof(Creature), nameof(Creature.MaxHp), nameof(MaxHpGetter));
        PatchGetter(harmony, typeof(Creature), nameof(Creature.Block), nameof(BlockGetter));
        PatchGetter(harmony, typeof(Creature), nameof(Creature.IsDead), nameof(DeadGetter));
        PatchGetter(harmony, typeof(Creature), nameof(Creature.IsAlive), nameof(AliveGetter));
        PatchGetter(harmony, typeof(CardPile), nameof(CardPile.Cards), nameof(PileCardsGetter));
        PatchGetter(harmony, typeof(CardModel), nameof(CardModel.Pile), nameof(CardPileGetter));
        PatchGetter(harmony, typeof(CombatManager), nameof(CombatManager.IsInProgress), nameof(InProgressGetter));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechRelicBase), "GetOwnerTurnNumber"),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber)), nameof(NativeBranchTurnNumber)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(HextechRelicBase)).Where(m => m.Name == "Flash"))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AllowDisplay)));
        harmony.Patch(AccessTools.Method(typeof(HextechRelicBase), "FlashDeferred"),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AllowDisplay)));
        harmony.Patch(AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged"),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AllowDisplay)));
        // Rewrite the audited call site, rather than skipping the native command
        // with a prefix: other mods' command postfixes would still run after a
        // skipped original and could write live state or apply the effect twice.
        harmony.Patch(AccessTools.Method(typeof(TapDanceRune), nameof(TapDanceRune.AfterCardPlayed)),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteDraw)));
        harmony.Patch(AccessTools.Method(typeof(VenomousBladeRune), nameof(VenomousBladeRune.ModifyDamageAdditiveCompat)),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerAmount)));
        RegisterAfterCardPlayed<ArchmageRune>();
        RegisterAfterCardPlayed<TapDanceRune>();
        RegisterAfterCardPlayed<TwiceThriceRune>();
        RegisterState<VenomousBladeRune>();
        RuneMirrors.RegisterNativeBase<VenomousBladeRune>();
        ModifyDamageMirrors.AdditiveRegistry.Register<VenomousBladeRune>((relic, context) =>
            Invoke(relic, context.Simulator, model => model.ModifyDamageAdditiveCompat(
                context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview)));
        ModifyCardPlayCountMirrors.Registry.Register<TwiceThriceRune>((relic, context) =>
            Invoke(relic, context.Simulator, model => model.ModifyCardPlayCount(context.Card.MutablePreview, context.Target, context.PlayCount)));
        RegisterPowerTurnRunes(harmony);
        RegisterOrbTurnRunes(harmony);
        RegisterBodySlamCommands(harmony);
        RegisterTokenCards(harmony);
        RegisterNativeGeneratedTokens(harmony);
        RegisterNativeChemtechDragon(harmony);
        RegisterNativeAuxiliaryForges(harmony);
        RegisterNativeUniversalSpiral(harmony);
        RegisterNativeOrobasRelics(harmony);
        RegisterOwnerDebuffRunes(harmony);
        RegisterBlockLossRunes(harmony);
        RegisterDamageRunes(harmony);
        RegisterState<RedEnvelopeRune>();
        SummonRuneMirrors.Register(harmony);
        RegisterSimpleCardUpgrades(harmony);
        RegisterResultPileRunes();
        AutomationUpgradeMirrors.Register(harmony);
        MysteryMirrors.Register(harmony);
        RegisterBreadRunes();
        RegisterDualWield();
        RegisterFinalForm(harmony);
        RegisterNativeQueryFamilies();
        RegisterStableGeneration(harmony);
        RegisterNeutralTemporaryPowers(harmony);
        RegisterSideStartBlockFamily(harmony);
        RegisterNativeEventFamily(harmony);
        RegisterNativeAutoPlayFamily(harmony);
        RegisterNativeDrawSustainFamily(harmony);
        RegisterNativeTransientAutoPlay(harmony);
        RegisterNativeScopeReturnFamily(harmony);
        RegisterNativeSellOff(harmony);
        RegisterNativeProjectionFamily(harmony);
        RegisterNativeCostGates(harmony);
        RegisterNativeSelfUpgrades(harmony);
        RegisterNativeSolidTime(harmony);
        RegisterNativeUpgradedAttacks(harmony);
        RegisterNativePowerSideStart(harmony);
        RegisterNativePowerReceived(harmony);
        RegisterNativeSimpleHooks(harmony);
        RegisterNativeBeforeHandDraw(harmony);
        RegisterNativeBuffReactions(harmony);
        RegisterNativeTurnResources(harmony);
        RegisterNativeDragonSouls(harmony);
        RegisterNativeBurnReplay(harmony);
        RegisterNativeBurnReactions(harmony);
        RegisterNativeCapturedAwards();
        RegisterNativeBurnUpgrades(harmony);
        RegisterNativeTurnFlags(harmony);
        RegisterNativeResourceReactions(harmony);
        RegisterNativeSummonResources(harmony);
        RegisterNativeOrbReactions(harmony);
        RegisterNativeBlockSustain(harmony);
        RegisterNativeCleanse(harmony);
        RegisterNativeCircleOfDeath(harmony);
        RegisterNativeOpeningAndGeneration(harmony);
        RegisterNativeDrawExhaustPeriod(harmony);
        RegisterNativeResourceUpgrades(harmony);
        RegisterNativeHandRules(harmony);
        RegisterNativeMissileStars(harmony);
        RegisterNativeEffectiveAttack(harmony);
        RegisterNativeGold(harmony);
        RegisterNativeDeathGrowth(harmony);
        RegisterNativeTurnCostReplay(harmony);
        RegisterNativeDamageUpgrades(harmony);
        RegisterNativeConditionalUpgrades(harmony);
        RegisterNativeOrbShuffle(harmony);
        RegisterNativeOrbPhases(harmony);
        RegisterNativeProgressStartUpgrades(harmony);
        RegisterNativeCardInstanceLedgers(harmony);
        RegisterNativeKeywordPersistence(harmony);
        RegisterNativeForge(harmony);
        RegisterNativeFinishedShivHistory(harmony);
        RegisterNativeNetherBatch(harmony);
        RegisterNativeNearDeath(harmony);
        RegisterNativeAutoPlaySelection(harmony);
        RegisterNativeGeneratedTags(harmony);
        RegisterNativeDuality(harmony);
        RegisterNativeEnemyLifecycle(harmony);
        RegisterNativeOutsideRewards();
        RegisterNativeInstantDeath(harmony);
        RegisterNativeColorDiscovery(harmony);
        RegisterNativeMonsterUpgrades(harmony);
        RegisterNativeEnemyDualWield(harmony);
        RegisterNativeVitalSpark(harmony);
        RegisterNativeMysteryOpening(harmony);
        RegisterNativeVakuuControl(harmony);
        RegisterNativeNatureHealing(harmony);
        RegisterNativeDamageCommandFamily(harmony);
        RegisterNativeSweepingBlade(harmony);
        RegisterNativeRegisteredRunes(harmony);
    }

    private static void RegisterAfterCardPlayed<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        RegisterAfterCardPlayedCallback<T>();
    }

    private static void RegisterAfterCardPlayedCallback<T>() where T : HextechRelicBase
    {
        AfterCardPlayedMirrors.Registry.Register<T>((relic, context) =>
        {
            var previous = _nativeResourcePlay;
            _nativeResourcePlay = context.CardPlay;
            try
            {
                RequireCompleted(Invoke(relic, context.Simulator,
                    model => model.AfterCardPlayed(new ThrowingPlayerChoiceContext(), context.CardPlay)), typeof(T));
            }
            finally { _nativeResourcePlay = previous; }
        });
    }

    private static void RegisterState<T>() where T : HextechRelicBase
    {
        _ = NativeRuneState.Fields(typeof(T));
        ModelPredictionStateMirrors.RegisterRelic<T, NativeRuneState>(
            "native-scalar-callback-v2", (simulator, live) =>
            {
                var state = new NativeRuneState(NativeRuneState.Clone(live));
                if (live is ChainInSleeveRune) state.RootFinishedShivHistory = state.FinishedShivHistory = CountLiveFinishedShivs(live.Owner);
                state.CaptureQueue(simulator);
                return state;
            },
            NativeRuneState.WriteModel<T>, NativeRuneState.WriteState);
    }

    private static IEnumerable<CodeInstruction> RewriteDraw(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeDraw))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = BranchDraw;
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed native draw call changed: expected 1, found {count}.");
    }

    private static IEnumerable<CodeInstruction> RewritePowerAmount(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.IsGenericMethod &&
                method.GetGenericMethodDefinition() == NativePowerAmount)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = BranchPowerAmount.MakeGenericMethod(method.GetGenericArguments());
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"VenomousBlade power query changed: expected 1, found {count}.");
    }

    private static int GetPowerAmount<T>(Creature creature) where T : PowerModel
        => _simulator is null ? creature.GetPowerAmount<T>() : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<T>(creature);

    internal static TResult Invoke<T, TResult>(T relic, CombatPredictionSimulator simulator, Func<T, TResult> callback) where T : HextechRelicBase
    {
        var state = ModelPredictionStateMirrors.Get<NativeRuneState>(simulator, relic);
        var model = (T)state.Model;
        var previous = _simulator;
        var previousSeed = _stableGeneration;
        var previousDeck = _nativeDeckCount;
        var previousSoulBirth = _nativeSoulBirthHps;
        var previousOrbs = _nativeFrozenOrbChannels;
        _simulator = simulator;
        _stableGeneration = state.StableGeneration;
        _nativeDeckCount = state.FrozenDeckCount;
        _nativeSoulBirthHps = state.SoulBirthHps;
        _nativeFrozenOrbChannels = state.FrozenOrbChannels;
        // Captured turn identity belongs to the live/parent combat. Rebind that
        // identity without resetting the captured round or proc count; the
        // original callback itself resets on a genuine round change.
        try
        {
            state.BindCardLedgers();
            state.SelfUpgrades?.BindCurrentCards();
            if (TurnScopeState.GetValue(model) is not null) TurnScopeState.SetValue(model, simulator.State.CombatState);
            return callback(model);
        }
        catch (NativeCallbackChoicePause pause) when (typeof(TResult) == typeof(Task))
        {
            // Non-async native wrappers can throw before returning a Task.
            AcceptNativeChoicePause(pause);
            return (TResult)(object)Task.CompletedTask;
        }
        finally
        {
            try { state.CaptureQueue(simulator); }
            finally { _simulator = previous; _stableGeneration = previousSeed; _nativeDeckCount = previousDeck; _nativeSoulBirthHps = previousSoulBirth; _nativeFrozenOrbChannels = previousOrbs; }
        }
    }

    private static Task<IEnumerable<CardModel>> Draw(PlayerChoiceContext context, decimal count, Player player, bool fromHandDraw)
    {
        if (_simulator is null) return CardPileCmd.Draw(context, count, player, fromHandDraw);
        var drawn = _simulator.Draw(player, count, fromHandDraw);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IEnumerable<CardModel>>(drawn.Select(c => c.MutablePreview).ToArray());
    }

    private static void PatchGetter(Harmony harmony, Type type, string property, string prefix)
        => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(NativeRuneBridge), prefix));

    private static bool CombatStateGetter(ref ICombatState? __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.State.CombatState;
        return false;
    }
    private static bool CardCombatStateGetter(ref ICombatState? __result) => CombatStateGetter(ref __result);
    private static int NativeBranchTurnNumber(PlayerCombatState live)
    {
        if (_simulator is null) return live.TurnNumber;
        var player = _simulator.State.CombatState.Players.SingleOrDefault(player => ReferenceEquals(player.PlayerCombatState, live))
            ?? throw new PredictionUnsupportedException("Native turn counter requested an uncaptured player.");
        return ((SimulatedCombatState)_simulator.State.CombatState).GetPlayerTurnNumber(player);
    }
    private static bool HpGetter(Creature __instance, ref int __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.State.GetCreature(__instance).CurrentHp;
        return false;
    }
    private static bool MaxHpGetter(Creature __instance, ref int __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.State.GetCreature(__instance).MaxHp;
        return false;
    }
    private static bool BlockGetter(Creature __instance, ref int __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.State.GetCreature(__instance).Block;
        return false;
    }
    private static bool PileCardsGetter(CardPile __instance, ref IReadOnlyList<CardModel> __result)
    {
        if (_simulator is null || __instance.Type == PileType.Deck) return true;
        foreach (Player player in _simulator.State.CombatState.Players)
            if (player.PlayerCombatState!.AllPiles.Any(p => ReferenceEquals(p, __instance)))
            {
                // MutablePreview detaches shared preview storage and invalidates
                // the pile fingerprint before any original callback can write.
                __result = _simulator.State.GetPlayerCombatState(player).GetCardPile(__instance.Type)!
                    .Cards.Select(c => c.MutablePreview).ToArray();
                return false;
            }
        throw new PredictionUnsupportedException("Native rune requested an uncaptured combat pile.");
    }
    private static bool CardPileGetter(CardModel __instance, ref CardPile? __result)
    {
        if (_simulator is null) return true;
        foreach (var pile in _simulator.State.GetPlayerCombatState(__instance.Owner).AllPiles)
            if (pile.Cards.Any(c => c.References(__instance)))
            {
                __result = __instance.Owner.PlayerCombatState!.AllPiles.Single(p => p.Type == pile.Type);
                return false;
            }
        __result = null;
        return false;
    }
    private static bool InProgressGetter(ref bool __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.IsInProgress;
        return false;
    }
    private static bool DeadGetter(Creature __instance, ref bool __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.State.GetCreature(__instance).IsDead;
        return false;
    }
    private static bool AliveGetter(Creature __instance, ref bool __result)
    {
        if (_simulator is null) return true;
        __result = !_simulator.State.GetCreature(__instance).IsDead;
        return false;
    }
    private static bool AllowDisplay() => _simulator is null;
}
