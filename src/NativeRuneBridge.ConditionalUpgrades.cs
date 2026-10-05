using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    internal sealed record FrozenOrbChannel(Player Owner, OrbModel Canonical);
    [ThreadStatic] private static IReadOnlyList<FrozenOrbChannel>? _nativeFrozenOrbChannels;
    private static readonly FieldInfo NativePilePlayer = AccessTools.DeclaredField(typeof(PlayerCombatState), "_player");
    private static readonly FieldInfo NativeActionChoices = AccessTools.DeclaredField(typeof(SimulatedCombatState), "_activeActionChoices");
    private static readonly Comparison<(CardTransformation, CardPile, int, CardModel)> OriginalNativePileIndexSort =
        AccessTools.DeclaredMethod(typeof(CardCmd), "PileIndexSort")
            .CreateDelegate<Comparison<(CardTransformation, CardPile, int, CardModel)>>();

    private static void RegisterNativeConditionalUpgrades(Harmony harmony)
    {
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(CardCmd), "PileIndexSort"));
        var cardCombat = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.CombatState));
        var creatureCombat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var pileCards = AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards));
        var allCards = AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards));
        var varBlock = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
            [typeof(Creature), typeof(BlockVar), typeof(CardPlay), typeof(bool)]);
        var add = AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]);
        var generated = AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat");
        var animation = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim),
            [typeof(Creature), typeof(string), typeof(float)]);

        PatchEventCallback(harmony, AccessTools.Method(typeof(HiddenGemUpgradeRune), "PlayUpgraded"),
            Site(animation, nameof(QuantumAnim)),
            Site(pileCards, nameof(NativeBranchPileCards)), Site(creatureCombat, nameof(NativeBranchCombat)),
            NativeRelicSite<HiddenGemUpgradeRune>(nameof(NativeCapturedRelic)), Site(add, nameof(MoveNativeCard)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HiddenGemUpgradeRune), "IsEligibleReplayTarget"));
        RegisterConditionalPlay<HiddenGem, HiddenGemUpgradeRune>((_, context) => HiddenGemUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (HiddenGem)context.Card.MutablePreview, context.CardPlay), stableGeneration: true);

        PatchEventCallback(harmony, AccessTools.Method(typeof(JackpotUpgradeRune), "OnPlayUpgraded"),
            Site(NativeAttackExecute, nameof(ExecuteNativeAttack)), Site(creatureCombat, nameof(NativeBranchCombat)),
            NativeRelicSite<JackpotUpgradeRune>(nameof(NativeCapturedRelic)),
            Site(AccessTools.PropertyGetter(typeof(RunRngSet), nameof(RunRngSet.CombatCardGeneration)), nameof(NativeGenerationRng)),
            Site(AccessTools.Method(typeof(CardFactory), nameof(CardFactory.GetForCombat)), nameof(CreateNativeRandomCards)),
            Site(generated, nameof(AddNativeGeneratedCard)));
        RegisterConditionalPlay<Jackpot, JackpotUpgradeRune>((_, context) => JackpotUpgradeRune.OnPlayUpgraded(
            (Jackpot)context.Card.MutablePreview, new ThrowingPlayerChoiceContext(), context.CardPlay), stableGeneration: true);

        PatchEventCallback(harmony, AccessTools.Method(typeof(CompactUpgradeRune), "PlayUpgraded"),
            Site(cardCombat, nameof(NativeUpgradedCardCombat)), Site(varBlock, nameof(GainNativeVarBlock)),
            Site(pileCards, nameof(NativeBranchPileCards)), NativeRelicSite<CompactUpgradeRune>(nameof(NativeCapturedRelic)),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Transform),
                [typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle)]), nameof(TransformNativeFixedCards)));
        RegisterConditionalPlay<Compact, CompactUpgradeRune>((_, context) => CompactUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (Compact)context.Card.MutablePreview, context.CardPlay));

        PatchEventCallback(harmony, AccessTools.Method(typeof(FlakCannonUpgradeRune), "GetStatusesToExhaust"),
            Site(allCards, nameof(NativeOwnedCombatCards)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(FlakCannonUpgradeRune), "GetStatusesIncludingExhaust"),
            Site(allCards, nameof(NativeOwnedCombatCards)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(FlakCannonUpgradeRune), "PlayUpgraded"),
            Site(cardCombat, nameof(NativeUpgradedCardCombat)), Site(NativeAttackExecute, nameof(ExecuteNativeAttack)),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Exhaust), [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool), typeof(bool)]), nameof(ExhaustNativeCard)),
            Site(AccessTools.Method(typeof(CalculatedVar), nameof(CalculatedVar.Calculate), [typeof(Creature)]), nameof(NativeFlakCalculatedHits)));
        RegisterConditionalPlay<FlakCannon, FlakCannonUpgradeRune>((_, context) => FlakCannonUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (FlakCannon)context.Card.MutablePreview, context.CardPlay));
        NativeCallbackContracts.AddNativePostfix(AccessTools.Method(typeof(FlakCannon), "GetStatuses"),
            AccessTools.Method(AccessTools.Inner(typeof(FlakCannonUpgradeRune), "FlakCannonStatusesPatch"), "Postfix"),
            "Natsuki.HextechRunes", Priority.Normal);
        RegisterNativeSdkPrefix(harmony, AccessTools.Method(typeof(CalculatedVarSpecRegistry), nameof(CalculatedVarSpecRegistry.TryCalculate)),
            nameof(NativeFlakCalculation));

        PatchEventCallback(harmony, AccessTools.Method(typeof(SurvivorUpgradeRune), "PlayUpgraded"),
            Site(varBlock, nameof(GainNativeVarBlock)), Site(NativeDecimalBlock, nameof(GainNativeBlock)),
            Site(pileCards, nameof(NativeBranchPileCards)), NativeRelicSite<SurvivorUpgradeRune>(nameof(NativeCapturedRelic)),
            Site(AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard),
                [typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel)]), nameof(SelectNativeDiscard)),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Discard), [typeof(PlayerChoiceContext), typeof(IEnumerable<CardModel>)]), nameof(DiscardNativeCards)));
        RegisterConditionalPlay<Survivor, SurvivorUpgradeRune>((_, context) => SurvivorUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (Survivor)context.Card.MutablePreview, context.CardPlay));
        RegisterNativeSdkPrefix(harmony, AccessTools.Method(typeof(CardChoiceSupport), nameof(CardChoiceSupport.GetSpec)), nameof(NativeSurvivorChoiceSpec));

        PatchEventCallback(harmony, AccessTools.Method(typeof(NightmareUpgradeRune), "ResolveNewNightmares"),
            Site(cardCombat, nameof(NativeUpgradedCardCombat)), Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Powers)), nameof(NativeRemovalPowers)),
            Site(AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.BeforeHandDraw), [typeof(Player), typeof(PlayerChoiceContext), typeof(ICombatState)]), nameof(GenerateNativeNightmareImmediately)));
        RegisterConditionalPlay<Nightmare, NightmareUpgradeRune>((_, context) => PlayNativeImmediateNightmare(context));
        foreach (var name in new[] { "ResolveManualCardChoice", "ResolveNestedCardChoice" })
            RegisterNativeSdkPrefix(harmony, AccessTools.GetDeclaredMethods(typeof(SimulatedCombatState))
                .Single(method => method.Name.EndsWith("." + name, StringComparison.Ordinal)), nameof(SkipCompletedNativeChoice));

        var channels = new List<NativeCallSite>
        {
            Site(animation, nameof(QuantumAnim)),
            Site(AccessTools.PropertyGetter(typeof(CombatHistory), nameof(CombatHistory.Entries)), nameof(NativeVoltaicHistory)),
            NativeRelicSite<VoltaicUpgradeRune>(nameof(NativeCapturedRelic)),
        };
        var channel = AccessTools.GetDeclaredMethods(typeof(OrbCmd)).Single(method => method.Name == nameof(OrbCmd.Channel) && method.IsGenericMethodDefinition);
        foreach (var type in new[] { typeof(LightningOrb), typeof(FrostOrb), typeof(DarkOrb), typeof(PlasmaOrb), typeof(GlassOrb) })
            channels.Add(new(channel.MakeGenericMethod(type), AccessTools.Method(typeof(NativeRuneBridge), nameof(ChannelNativeOrbType)).MakeGenericMethod(type), 1));
        PatchEventCallback(harmony, AccessTools.Method(typeof(VoltaicUpgradeRune), "PlayUpgraded"), channels.ToArray());
        RegisterConditionalPlay<Voltaic, VoltaicUpgradeRune>((_, context) => VoltaicUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (Voltaic)context.Card.MutablePreview, context.CardPlay), stableGeneration: true);
        var channelTarget = AccessTools.Method(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.OrbChannel),
            [typeof(Player), typeof(OrbModel)]);
        var channelRewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeChannelHistory));
        harmony.Patch(channelTarget, transpiler: new HarmonyMethod(channelRewrite));
        NativeCallbackContracts.Add(channelTarget, channelRewrite);

        PatchEventCallback(harmony, AccessTools.Method(typeof(CreativeAiUpgradeRune), "GenerateUpgradedPowerCards"),
            NativeRelicSite<CreativeAiUpgradeRune>(nameof(NativeCapturedRelic)),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount)), nameof(NativeCreativeAmount)),
            Site(AccessTools.PropertyGetter(typeof(RunRngSet), nameof(RunRngSet.CombatCardGeneration)), nameof(NativeGenerationRng)),
            Site(AccessTools.Method(typeof(CardFactory), nameof(CardFactory.GetDistinctForCombat)), nameof(CreateNativeDistinctCards)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.AddGeneratedCardToCombat),
                [typeof(CardModel), typeof(PileType), typeof(Player), typeof(CardPilePosition)]), nameof(AddNativeCreativeCard)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(CreativeAiUpgradeRune), "UpgradeGeneratedCard"));
        RegisterStableState<CreativeAiUpgradeRune>(); RuneMirrors.RegisterNativeBase<CreativeAiUpgradeRune>();
        NativeCallbackContracts.AddNativePrefix(AccessTools.DeclaredMethod(typeof(CreativeAiPower), nameof(AbstractModel.BeforeHandDraw)),
            AccessTools.Method(AccessTools.Inner(typeof(CreativeAiUpgradeRune), "CreativeAiPatch"), "Prefix"), "Natsuki.HextechRunes", Priority.Low);
        var beforeDraw = AccessTools.Method(typeof(TurnStartPowerSupport), "ContinueBeforeHandDraw");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeCreativeGeneration));
        harmony.Patch(beforeDraw, transpiler: new HarmonyMethod(rewrite)); NativeCallbackContracts.Add(beforeDraw, rewrite);
    }

    private static T? NativeCapturedRelic<T>(Player player) where T : HextechRelicBase
    {
        if (_simulator is null) return player.GetRelic<T>();
        var rune = ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(player).OfType<T>().SingleOrDefault();
        return rune is null ? null : (T)ModelPredictionStateMirrors.Get<NativeRuneState>(_simulator, rune).Model;
    }

    private static Rng NativeGenerationRng(RunRngSet rng)
        => _simulator?.Rng.CombatCardGeneration ?? rng.CombatCardGeneration;

    private static IEnumerable<CardModel> CreateNativeRandomCards(Player player, IEnumerable<CardModel> pool, int count, Rng rng)
        => _simulator is not { } sim ? CardFactory.GetForCombat(player, pool, count, rng)
            : pool.TakeRandomForCombat(player, count, rng, ((SimulatedCombatState)sim.State.CombatState).CardMultiplayerConstraint)
                .Select(canonical => sim.State.CombatState.CreateCard(canonical, player)).ToArray();

    private static IEnumerable<CardModel> CreateNativeDistinctCards(Player player, IEnumerable<CardModel> pool, int count, Rng rng)
        => _simulator is not { } sim ? CardFactory.GetDistinctForCombat(player, pool, count, rng)
            : pool.TakeRandomDistinctForCombat(player, count, rng, ((SimulatedCombatState)sim.State.CombatState).CardMultiplayerConstraint)
                .Select(canonical => sim.State.CombatState.CreateCard(canonical, player)).ToArray();

    private static IEnumerable<CardModel> NativeOwnedCombatCards(PlayerCombatState state)
        => _simulator is null ? state.AllCards
            : _simulator.State.GetPlayerCombatState((Player)NativePilePlayer.GetValue(state)!).AllCards
                .Select(card => card.MutablePreview).ToArray();

    private static Task<IEnumerable<CardPileAddResult>> TransformNativeFixedCards(IEnumerable<CardTransformation> transformations,
        Rng? rng, CardPreviewStyle preview)
    {
        if (_simulator is not { } sim) return CardCmd.Transform(transformations, rng, preview);
        var combat = (SimulatedCombatState)sim.State.CombatState;
        // Native batch Transform removes each original while capturing its
        // current index, then inserts the replacements in a separate pass.
        // Sequential in-place replacement changes later draw-pile positions.
        var pending = new List<(CardTransformation Transformation, PredictedCard Original, PredictedCard Replacement, SimCardPile Pile, int Index, CardPile NativePile)>();
        var results = new List<CardPileAddResult>();
        foreach (var transformation in transformations)
        {
            var original = sim.State.FindCard(transformation.Original)
                ?? throw new PredictionUnsupportedException("Native transform source is missing from its branch.");
            var replacement = transformation.Replacement
                ?? throw new PredictionUnsupportedException("Reviewed transform requires a fixed detached replacement.");
            if (!replacement.IsMutable || sim.State.FindCard(replacement) is not null || rng is not null)
                throw new PredictionUnsupportedException("Native fixed transform acquired a random/live replacement.");
            var oldPile = NativeBranchCardPile(transformation.Original)
                ?? throw new PredictionUnsupportedException("Native batch transform source pile identity is absent.");
            var pile = original.GetPile(sim.State)
                ?? throw new PredictionUnsupportedException("Native batch transform original is not in a branch pile.");
            int index = pile.Cards.ToList().FindIndex(card => ReferenceEquals(card, original));
            if (index < 0) throw new PredictionUnsupportedException("Native batch transform original index is absent.");
            pending.Add((transformation, original, PredictedCard.FromGenerated(replacement), pile, index, oldPile));
            pile.Remove(original);
        }
        // Vanilla sorts by pile type and then the captured, removal-adjusted
        // index. Reuse its exact comparator and List.Sort tie behavior.
        pending.Sort((first, second) => OriginalNativePileIndexSort(
            (first.Transformation, first.NativePile, first.Index, first.Replacement.MutablePreview),
            (second.Transformation, second.NativePile, second.Index, second.Replacement.MutablePreview)));
        var generations = new List<(PredictedCard Card, CombatPredictionCardGeneratedEntry Entry)>();
        foreach (var item in pending)
        {
            var replacement = item.Replacement;
            replacement.MutablePreview.HasBeenRemovedFromState = false;
            replacement.NotifyHookListenerStructureChanged();
            item.Pile.Insert(Math.Min(item.Index, item.Pile.Cards.Count), replacement);
            generations.Add((replacement, sim.History.CardGenerated(replacement, replacement.Preview.Owner, CardGenerationResultKind.Fixed)));
            combat.AfterCardEnteredCombat(sim, replacement);
            PauseNativeChoice(sim);
            item.Original.MutablePreview.AfterTransformedFrom();
            replacement.MutablePreview.AfterTransformedTo();
            results.Add(new CardPileAddResult { success = true, cardAdded = replacement.MutablePreview, oldPile = item.NativePile,
                targetPile = item.Pile.Type, modifyingModels = [] });
        }
        // Original generation reactions run after the full batch is inserted.
        foreach (var (card, entry) in generations)
        {
            HookMirrors.AfterCardGeneratedForCombat(sim, card, card.Preview.Owner);
            PauseNativeChoice(sim);
            sim.History.CardGenerationResolved(entry, card);
        }
        foreach (var item in pending)
        {
            item.Original.MutablePreview.HasBeenRemovedFromState = true;
            item.Original.NotifyHookListenerStructureChanged();
            combat.AfterCardRemovedFromCombat(item.Original);
        }
        return Task.FromResult<IEnumerable<CardPileAddResult>>(results);
    }

    private static decimal NativeFlakCalculatedHits(CalculatedVar variable, Creature? target)
    {
        if (_simulator is not { } sim) return variable.Calculate(target);
        var card = sim.State.CombatState.Players.SelectMany(player => sim.State.GetPlayerCombatState(player).AllCards)
            .Single(card => card.Preview is FlakCannon && card.Preview.DynamicVars.Any(pair => ReferenceEquals(pair.Value, variable)));
        return CalculateNativeFlak(sim, card);
    }

    private static decimal CalculateNativeFlak(CombatPredictionSimulator simulator, PredictedCard card)
    {
        int statuses = simulator.State.GetPlayerCombatState(card.Preview.Owner).AllCards
            .Count(candidate => candidate.Preview.Type == CardType.Status);
        var vars = card.Preview.DynamicVars;
        return vars.CalculationBase.BaseValue + (vars.TryGetValue("CalculationExtra", out var extra) ? extra : vars.ExtraDamage).BaseValue * statuses;
    }

    private static bool NativeFlakCalculation(CombatPredictionSimulator simulator, PredictedCard card,
        ref decimal value, ref bool __result)
    {
        if (card.Preview is not FlakCannon || !((SimulatedCombatState)simulator.State.CombatState)
            .RelicsOf(card.Preview.Owner).OfType<FlakCannonUpgradeRune>().Any()) return true;
        value = CalculateNativeFlak(simulator, card); __result = true; return false;
    }

    private static CardChoiceSpec SurvivorSpec(CombatPredictionSimulator sim, PredictedCard source)
    {
        var hand = sim.State.GetPlayerCombatState(source.Preview.Owner).Hand.Cards;
        var options = hand.Where(card => !card.References(source.MutablePreview)).ToArray();
        return new(PlanChoiceEffect.Discard, PileType.Hand, 0, options.Length, options, hand, ReplacementValue: 0d);
    }

    private static bool NativeSurvivorChoiceSpec(CombatPredictionSimulator simulator, PredictedCard playedCard, ref CardChoiceSpec? __result)
    {
        if (playedCard.Preview is DecisionsDecisions && ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(playedCard.Preview.Owner).OfType<DecisionsDecisionsUpgradeRune>().Any())
        { __result = NativeDecisionsSpec(simulator, playedCard); return false; }
        if (playedCard.Preview is not Survivor || !((SimulatedCombatState)simulator.State.CombatState)
            .RelicsOf(playedCard.Preview.Owner).OfType<SurvivorUpgradeRune>().Any()) return true;
        __result = SurvivorSpec(simulator, playedCard); return false;
    }

    private static Task<IEnumerable<CardModel>> SelectNativeDiscard(PlayerChoiceContext context, Player player,
        CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        if (_simulator is not { } sim) return CardSelectCmd.FromHandForDiscard(context, player, prefs, filter, source);
        if (source is not Survivor || filter is not null || prefs.MinSelect != 0)
            throw new PredictionUnsupportedException("Reviewed Survivor selector contract changed.");
        var card = sim.State.FindCard((CardModel)source) ?? throw new PredictionUnsupportedException("Native selector source is absent.");
        var combat = (SimulatedCombatState)sim.State.CombatState;
        var spec = SurvivorSpec(sim, card);
        if (prefs.MaxSelect != spec.MaxCount) throw new PredictionUnsupportedException("Native discard selector limit differs from its branch.");
        var request = new TurnStartChoiceRequest("", PlanChoiceEffect.Discard, PileType.Hand, 0, spec, Timing: combat.ActiveActionChoiceTiming);
        var cursor = (TurnStartChoiceCursor?)NativeActionChoices.GetValue(combat);
        if (cursor is null || !cursor.TryTake(request, out var choice))
        {
            combat.SetPendingTurnStartChoice(request); PauseNativeChoice(sim);
            throw new PredictionUnsupportedException("Native selection did not suspend.");
        }
        if (choice!.Cards.Count > spec.MaxCount) throw new InvalidPlannedChoiceBranchException("Native discard selection exceeds its limit.");
        var selected = CardChoiceSupport.ResolveStandaloneChoice(sim, choice, spec.Options, choice.Cards.Count, PileType.Hand);
        combat.ClearPendingTurnStartChoice();
        return Task.FromResult<IEnumerable<CardModel>>(selected.Select(card => card.MutablePreview).ToArray());
    }

    private static Task DiscardNativeCards(PlayerChoiceContext context, IEnumerable<CardModel> cards)
    {
        if (_simulator is not { } sim) return CardCmd.Discard(context, cards);
        sim.Discard(cards.Select(card => sim.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native discard references an absent branch card.")).ToArray());
        PauseNativeChoice(sim); return Task.CompletedTask;
    }

    private static bool SkipCompletedNativeChoice(SimulatedCombatState __instance, CombatPredictionSimulator simulator,
        PredictedCard card, ref bool __result)
    {
        if (card.Preview is not (Survivor or Nightmare or DecisionsDecisions) || !HasCompleteNativeCardBody(__instance, card)) return true;
        __result = !simulator.HasPendingChoice; return false;
    }

    private static Task PlayNativeImmediateNightmare(CardOnPlayMirrorContext context)
    {
        var combat = (SimulatedCombatState)context.CombatState;
        var card = context.Card;
        var previous = NativeRemovalPowers(card.Preview.Owner.Creature).OfType<NightmarePower>().ToArray();
        CardOnPlayMirrors.Registry.Invoke(card.MutablePreview, context);
        CardOnPlayMirrors.ApplyRemainingCardSpec(context.Simulator, card, context.CardPlay.Target);
        var spec = CardChoiceSupport.GetSpec(context.Simulator, card)
            ?? throw new PredictionUnsupportedException("Native Nightmare lost its selected-card specification.");
        var request = new TurnStartChoiceRequest("", spec.Effect, spec.SourcePile, spec.MinCount, spec, Timing: combat.ActiveActionChoiceTiming);
        var cursor = (TurnStartChoiceCursor?)NativeActionChoices.GetValue(combat);
        if (cursor is null || !cursor.TryTake(request, out var choice))
        {
            combat.SetPendingTurnStartChoice(request); PauseNativeChoice(context.Simulator);
            throw new PredictionUnsupportedException("Native Nightmare selection did not suspend.");
        }
        CardChoiceSupport.Apply(context.Simulator, combat, card, choice!, new HashSet<uint>());
        PauseNativeChoice(context.Simulator);
        return NightmareUpgradeRune.ResolveNewNightmares(Task.CompletedTask, (Nightmare)card.MutablePreview,
            new ThrowingPlayerChoiceContext(), previous);
    }

    private static Task GenerateNativeNightmareImmediately(AbstractModel model, Player player, PlayerChoiceContext context, ICombatState state)
    {
        if (_simulator is not { } sim) return model.BeforeHandDraw(player, context, state);
        if (model is not NightmarePower power) throw new PredictionUnsupportedException("Immediate Nightmare acquired a foreign power callback.");
        ((SimulatedCombatState)sim.State.CombatState).GenerateTurnStartPowerCards(sim, player, power);
        PauseNativeChoice(sim); return Task.CompletedTask;
    }

    internal static IReadOnlyList<FrozenOrbChannel> CaptureFrozenOrbChannels(Player player)
        => CombatManager.Instance.History.Entries.OfType<OrbChanneledEntry>()
            .Where(entry => entry.Actor.Player == player)
            .Select(entry => new FrozenOrbChannel(player, (OrbModel)entry.Orb.CanonicalInstance)).ToArray();

    private static IEnumerable<CodeInstruction> RewriteNativeChannelHistory(IEnumerable<CodeInstruction> instructions)
    {
        var callback = AccessTools.Method(typeof(HookMirrors), nameof(HookMirrors.AfterOrbChanneled));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(callback))
            {
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(AfterNativeChannelHistory));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("SDK channel history callback boundary changed.");
    }

    private static void AfterNativeChannelHistory(CombatPredictionSimulator simulator, Player player, OrbModel orb)
    {
        foreach (var rune in ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player).OfType<VoltaicUpgradeRune>())
        {
            var state = ModelPredictionStateMirrors.Get<NativeRuneState>(simulator, rune);
            var previous = state.FrozenOrbChannels
                ?? throw new PredictionUnsupportedException("Voltaic channel projection was not captured.");
            // New immutable array: the completed channel belongs to this branch.
            state.FrozenOrbChannels = previous.Append(new FrozenOrbChannel(player, (OrbModel)orb.CanonicalInstance)).ToArray();
        }
        HookMirrors.AfterOrbChanneled(simulator, player, orb);
    }

    internal static void WriteFrozenOrbChannels(IReadOnlyList<FrozenOrbChannel> channels, ref ModelPredictionStateWriter writer)
    {
        writer.Add("voltaicRootChannels", channels.Count);
        for (int index = 0; index < channels.Count; index++)
            writer.Add("voltaicRootChannel:" + index, channels[index].Owner.NetId + ":" + channels[index].Canonical.Id);
    }

    private static IEnumerable<CombatHistoryEntry> NativeVoltaicHistory(CombatHistory history)
    {
        if (_simulator is not { } sim) return history.Entries;
        var root = _nativeFrozenOrbChannels ?? throw new PredictionUnsupportedException("Voltaic has no captured channel history.");
        var combat = sim.State.CombatState;
        var entries = new List<CombatHistoryEntry>();
        foreach (var channel in root)
        {
            var orb = (OrbModel)channel.Canonical.ToMutable(); orb.Owner = channel.Owner;
            // The reviewed consumer reads only Actor.Player and Orb's type.
            // Empty players avoid consulting live per-player turn counters.
            entries.Add(new OrbChanneledEntry(orb, combat.RoundNumber, combat.CurrentSide, history, []));
        }
        return entries;
    }

    private static Task ChannelNativeOrbType<T>(PlayerChoiceContext context, Player player) where T : OrbModel
    {
        if (_simulator is null) return OrbCmd.Channel<T>(context, player);
        return ChannelOrb(context, (OrbModel)CanonicalModels.Orb<T>().ToMutable(), player);
    }

    private static Task<CardPileAddResult> AddNativeCreativeCard(CardModel card, PileType pile, Player? creator, CardPilePosition position)
    {
        if (_simulator is not { } sim) return CardPileCmd.AddGeneratedCardToCombat(card, pile, creator, position);
        var result = sim.AddGeneratedCardToCombat(PredictedCard.FromGenerated(card), pile, creator, position);
        PauseNativeChoice(sim);
        return Task.FromResult(new CardPileAddResult { success = result.Success, cardAdded = card,
            oldPile = null, targetPile = pile, modifyingModels = [] });
    }

    private static IEnumerable<CodeInstruction> RewriteNativeCreativeGeneration(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var amount = AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount));
        var value = generator.DeclareLocal(typeof(int));
        bool replaced = false;
        foreach (var instruction in instructions)
        {
            if (!replaced && instruction.Calls(amount))
            {
                // Replace the loop's eligibility read, retaining original order
                // and its existing continue path. Native generation consumes the
                // current branch RNG; returning zero suppresses the stock copy.
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeCreativeEligibilityAmount));
                replaced = true;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Stloc, value);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.HasPendingChoice)));
                var ready = generator.DefineLabel();
                yield return new CodeInstruction(OpCodes.Brfalse, ready);
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                yield return new CodeInstruction(OpCodes.Ret);
                yield return new CodeInstruction(OpCodes.Ldloc, value).WithLabels(ready);
                continue;
            }
            yield return instruction;
        }
        if (!replaced) throw new InvalidOperationException("Pinned BeforeHandDraw power eligibility read changed.");
    }

    private static int NativeCreativeEligibilityAmount(PowerModel power, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Player player)
    {
        int amount = power.Amount;
        if (amount <= 0 || power.Owner.Player != player || power is not CreativeAiPower creative
            || combat.RelicsOf(player).OfType<CreativeAiUpgradeRune>().SingleOrDefault() is not { } rune) return amount;
        RequireCompleted(Invoke(rune, simulator, _ => CreativeAiUpgradeRune.GenerateUpgradedPowerCards(creative, player)), typeof(CreativeAiUpgradeRune));
        if (simulator.HasPendingChoice) simulator.RejectExecutionContinuation();
        return 0;
    }

    private static int NativeCreativeAmount(PowerModel power)
        => _simulator is null ? power.Amount
            : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<CreativeAiPower>(power.Owner);
}
