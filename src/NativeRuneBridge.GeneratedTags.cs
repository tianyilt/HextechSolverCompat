using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

// Membership is frozen from the root relic roster. The combat and card-pile
// references are rebound on every fork; power queries therefore remain dynamic.
internal sealed class NativeGeneratedCardContext : IPredictionStateForkable
{
    internal static readonly ConditionalWeakTable<CardModel, NativeGeneratedCardContext> Owners = new();
    internal readonly SimulatedCombatState Combat;
    internal readonly SimPlayerCombatState Cards;
    internal readonly bool BigKnife, Inkshadow, Deviant, Illusory;
    internal NativeGeneratedCardContext(CombatPredictionSimulator simulator, Player player)
    {
        Combat = (SimulatedCombatState)simulator.State.CombatState;
        Cards = simulator.State.GetPlayerCombatState(player);
        var runes = Combat.RelicsOf(player).ToArray();
        BigKnife = runes.Any(r => r is BigKnifeRune); Inkshadow = runes.Any(r => r is InkshadowRune);
        Deviant = runes.Any(r => r is DeviantCognitionRune); Illusory = runes.Any(r => r is IllusoryWeaponRune);
        BindCards();
    }
    private NativeGeneratedCardContext(NativeGeneratedCardContext source, PredictionForkContext fork)
    {
        Combat = fork.RequireRemap(source.Combat); Cards = fork.RequireRemap(source.Cards);
        BigKnife = source.BigKnife; Inkshadow = source.Inkshadow; Deviant = source.Deviant; Illusory = source.Illusory;
    }
    internal void Bind(CardModel card) { Owners.Remove(card); Owners.Add(card, this); }
    private void BindCards() { foreach (var card in Cards.AllCards) Bind(card.MutablePreview); }
    public object Fork(PredictionForkContext fork)
    {
        if (fork.TryRemap(this, out NativeGeneratedCardContext? existing)) return existing!;
        var copy = new NativeGeneratedCardContext(this, fork); fork.Register(this, copy);
        // Only SovereignBlade's Fan query reads mutable combat state. All other
        // tag queries use immutable membership, so they may retain their root
        // binding without defeating the solver's lazy COW on every search fork.
        if (copy.BigKnife)
            foreach (var card in copy.Cards.AllCards.Where(card => card.Preview is SovereignBlade)) copy.Bind(card.MutablePreview);
        return copy;
    }
}

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeGeneratedTags(Harmony harmony)
    {
        RegisterState<BigKnifeRune>(); RuneMirrors.RegisterNativeBase<BigKnifeRune>();
        RegisterState<DeviantCognitionRune>(); RuneMirrors.RegisterNativeBase<DeviantCognitionRune>();
        RegisterNativeQueryCallbacks<BigKnifeRune>(NativeQueries.Energy | NativeQueries.Stars);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BigKnifeRune), "ShouldMakeBladeFree"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "Pile"), nameof(NativeBranchCardPile)));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "AddGeneratedCardsToCombat"), nameof(RewriteNativeGeneratedBatch));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "GetTargetType"), nameof(OwnedNativeTargetType));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechPlayerRuneHooks), "CardTagsPatch"), "Postfix"), nameof(OwnedNativeCardTags));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(HextechKnifeHelper), "ShouldTreatSovereignBladeAsShiv"), nameof(OwnedNativeBladeShiv));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(HextechKnifeHelper), "ShouldFanOfKnivesAffectSovereignBlade"), nameof(OwnedNativeFanBlade));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(InkshadowRune), "TryApplyInkshadow"), nameof(OwnedNativeInkshadow));
        NativeCallbackContracts.AddNativePostfix(AccessTools.PropertyGetter(typeof(CardModel), "Tags"),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechPlayerRuneHooks), "CardTagsPatch"), "Postfix"), "Natsuki.HextechRunes", Priority.Normal);
        var ritsuTags = AccessTools.TypeByName("STS2RitsuLib.Models.Capabilities.Patches.CardModelCapabilityPatches+TagsPatch")
            ?? throw new PredictionUnsupportedException("Reviewed Ritsu tag pipeline is missing.");
        NativeCallbackContracts.AddNativePostfix(AccessTools.PropertyGetter(typeof(CardModel), "Tags"),
            AccessTools.DeclaredMethod(ritsuTags, "Postfix"), "com.ritsukage.sts2-RitsuLib.framework-core", Priority.Normal);
        // Native dependency sorting and the isolated host may register these
        // two reviewed callbacks in either order. Prediction runs the actual
        // getter pipeline (including OwnedNativeCardTags), rather than
        // assuming the callbacks commute. Its stamp retains order/indices.
        NativeCallbackContracts.AddAlternatePostfixOrder(AccessTools.PropertyGetter(typeof(CardModel), "Tags"),
            AccessTools.DeclaredMethod(ritsuTags, "Postfix"),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechPlayerRuneHooks), "CardTagsPatch"), "Postfix"));
        NativeCallbackContracts.AddNativePostfix(AccessTools.PropertyGetter(typeof(SovereignBlade), "TargetType"),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(BigKnifeRune), "SovereignBladeTargetTypePatch"), "Postfix"), "Natsuki.HextechRunes", Priority.Normal);
        NativeCallbackContracts.AddNativePrefix(AccessTools.Method(typeof(CardPileCmd), "AddGeneratedCardsToCombat",
                [typeof(IEnumerable<CardModel>), typeof(PileType), typeof(Player), typeof(CardPilePosition)]),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(BigKnifeRune), "GeneratedCardsPatch"), "Prefix"), "Natsuki.HextechRunes", Priority.Normal);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(InkshadowRune), "AfterCardGeneratedForCombat"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(InkshadowRune), "AfterCardEnteredCombat"));
        AfterCardGeneratedForCombatMirrors.Registry.Register<InkshadowRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterCardGeneratedForCombat(context.MutablePreviewCard, context.Creator)), typeof(InkshadowRune)));
        RegisterConditionalPlay<BladeOfInk, InkshadowRune>((_, context) => PlayOwnedBladeOfInk(context));
        // BigKnife is already registered; the conditional body does not require
        // a second state registration (RegisterConditionalPlay would duplicate it).
        CompleteNativeCards.Add(typeof(SovereignBlade), typeof(BigKnifeRune));
        ConditionalPlays.Add(typeof(BigKnifeRune), (_, context) => PlayOwnedBigKnife(context));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.ManipulateReality);
    }
    internal static NativeGeneratedCardContext GeneratedContext(CombatPredictionSimulator simulator, Player owner)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault() is { } modifier)
            return ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).GeneratedTags
                ?? throw new PredictionUnsupportedException("Generated cards lack their captured context.");
        var rune = combat.RelicsOf(owner).OfType<HextechRelicBase>().FirstOrDefault(r => r is BigKnifeRune or InkshadowRune or DeviantCognitionRune);
        return rune is null ? new NativeGeneratedCardContext(simulator, owner)
            : ModelPredictionStateMirrors.Get<NativeRuneState>(simulator, rune).GeneratedTags
                ?? throw new PredictionUnsupportedException("Generated rune context is absent.");
    }
    private static bool RewriteNativeGeneratedBatch(CombatPredictionSimulator __instance, ref IReadOnlyList<PredictedCard> cards, Player? creator)
    {
        if (!__instance.IsInProgress || cards.Count == 0) return true;
        var combat = (SimulatedCombatState)__instance.State.CombatState;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        bool doubleStatus = creator is null && modifier is not null
            && ModelPredictionStateMirrors.Get<EnemyState>(__instance, modifier).Has(MonsterHexKind.ManipulateReality);
        List<PredictedCard> rewritten = [];
        foreach (var original in cards)
        {
            var owner = GeneratedContext(__instance, original.Preview.Owner);
            var card = original; owner.Bind(card.MutablePreview);
            if (owner.BigKnife && card.Preview is Shiv)
            {
                card = PredictedCard.Create(CanonicalModels.Card<SovereignBlade>(), card.Preview.Owner);
                owner.Bind(card.MutablePreview);
                if (original.Preview.IsUpgraded) card.Upgrade();
                card.MutablePreview.SetToFreeThisTurn(); card.MutablePreview.ExhaustOnNextPlay = true;
                if (!card.Preview.Keywords.Contains(CardKeyword.Exhaust)) card.MutablePreview.AddKeyword(CardKeyword.Exhaust);
                ApplyOwnedInkshadow(owner, card);
            }
            rewritten.Add(card);
            if (doubleStatus && card.Preview.Type == CardType.Status)
            {
                // CombatState.CloneCard uses the native gameplay clone stage,
                // whereas prediction COW also restores transient source fields.
                var clone = card.CreateClone(); owner.Bind(clone.MutablePreview); rewritten.Add(clone);
            }
        }
        cards = rewritten; return true;
    }
    private static bool OwnedNativeCardTags([HarmonyArgument(0)] CardModel card, [HarmonyArgument(1)] ref IEnumerable<CardTag> tags)
    {
        if (!NativeGeneratedCardContext.Owners.TryGetValue(card, out var owner)) return true;
        if (owner.BigKnife && card is SovereignBlade && !tags.Contains(CardTag.Shiv)) tags = tags.Append(CardTag.Shiv);
        bool attack = card.Type == CardType.Attack || owner.Illusory && (card.CanonicalInstance?.Type ?? card.Type) == CardType.Skill;
        if (owner.Deviant && attack && !tags.Contains(CardTag.Strike)) tags = tags.Append(CardTag.Strike);
        return false;
    }
    private static bool OwnedNativeBladeShiv(CardModel card, Player? owner, ref bool __result)
    {
        if (!NativeGeneratedCardContext.Owners.TryGetValue(card, out var context)) return true;
        __result = card is SovereignBlade && card.Owner == owner && context.BigKnife; return false;
    }
    private static bool OwnedNativeFanBlade(SovereignBlade card, ref bool __result)
    {
        if (!NativeGeneratedCardContext.Owners.TryGetValue(card, out var owner)) return true;
        __result = owner.BigKnife && ((ICombatPredictionHookListenerSource)owner.Combat).HookListeners.OfType<FanOfKnivesPower>().Any(power => power.Owner == card.Owner.Creature);
        return false;
    }
    private static bool OwnedNativeTargetType(PredictedCard card, ref TargetType __result)
    {
        if (card.Preview is not SovereignBlade || !NativeGeneratedCardContext.Owners.TryGetValue(card.Preview, out var owner)
            || !owner.BigKnife || !((ICombatPredictionHookListenerSource)owner.Combat).HookListeners.OfType<FanOfKnivesPower>().Any(power => power.Owner == card.Preview.Owner.Creature)) return true;
        __result = TargetType.AllEnemies; return false;
    }
    private static bool ApplyOwnedInkshadow(NativeGeneratedCardContext owner, PredictedCard card)
    {
        if (!owner.Inkshadow || card.Preview.Enchantment is not null || !card.Preview.Tags.Contains(CardTag.Shiv)) return false;
        var enchantment = (Inky)ModelDb.Enchantment<Inky>().ToMutable();
        if (!enchantment.CanEnchant(card.Preview)) return false;
        card.Enchant(enchantment, 1m); return true;
    }
    private static bool OwnedNativeInkshadow(InkshadowRune __instance, CardModel? card, ref bool __result)
    {
        if (card is null || !NativeGeneratedCardContext.Owners.TryGetValue(card, out var owner)) return true;
        var predicted = _simulator?.State.FindCard(card);
        if (predicted is null) throw new PredictionUnsupportedException("Inkshadow callback received a detached card.");
        __result = card.Owner == __instance.Owner && ApplyOwnedInkshadow(owner, predicted); return false;
    }
    private static Task PlayOwnedBladeOfInk(CardOnPlayMirrorContext context)
    {
        var card = context.Card.Preview;
        foreach (var result in context.Simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(card.Owner, PileType.Hand, card.DynamicVars.Cards.IntValue, card.Owner))
        {
            if (context.Simulator.HasPendingChoice) { PauseNativeChoice(context.Simulator); break; }
            if (result.CardAdded.Preview.Enchantment is not Inky)
            {
                var enchantment = (Inky)ModelDb.Enchantment<Inky>().ToMutable();
                if (enchantment.CanEnchant(result.CardAdded.Preview)) result.CardAdded.Enchant(enchantment, 1m);
            }
        }
        return Task.CompletedTask;
    }
    private static void PlayOwnedBigKnife(CardOnPlayMirrorContext context)
    {
        var card = (SovereignBlade)context.Card.Preview;
        bool fan = NativeGeneratedCardContext.Owners.TryGetValue(card, out var owner)
            && ((ICombatPredictionHookListenerSource)owner.Combat).HookListeners.OfType<FanOfKnivesPower>().Any(power => power.Owner == card.Owner.Creature);
        if (!fan)
        {
            CardOnPlayMirrors.Registry.Invoke(card, context);
            if (!context.Simulator.HasPendingChoice) CardOnPlayMirrors.ApplyRemainingCardSpec(context.Simulator, context.Card, context.Target);
            return;
        }
        var attack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue).FromCard(card, context.CardPlay)
            .WithHitCount(card.DynamicVars.Repeat.IntValue).TargetingAllOpponents(context.CombatState);
        context.Simulator.ExecuteAttack(attack); PauseNativeChoice(context.Simulator);
    }
}
