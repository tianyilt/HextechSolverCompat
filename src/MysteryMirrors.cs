using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Frozen;

namespace HextechSolverCompat;

internal sealed class MysterySeedState : IPredictionStateForkable
{
    internal readonly string Seed;
    internal readonly string PlayerKey;
    internal readonly int Act;
    internal readonly int ScalingAct;
    internal readonly int Floor;
    private readonly FrozenDictionary<CardPoolModel, CardModel[]> _pools;
    private readonly string _poolIdentity;
    internal MysterySeedState(HextechRelicBase rune) : this(rune.Owner) { }
    internal MysterySeedState(Player player)
    {
        var run = (RunState)player.RunState;
        Seed = run.Rng.StringSeed;
        Act = run.CurrentActIndex;
        ScalingAct = HextechPlayerContextHelper.GetActNumberForScaling(player);
        Floor = run.TotalFloor;
        PlayerKey = HextechStableRandom.PlayerKey(player);
        // The solver's optional optimization captures only character/colorless
        // pools. Entropy can also inspect curses, statuses or another character's
        // cards. Freeze all native canonical pools at root capture instead of
        // consulting live unlock data later in a search branch.
        Dictionary<CardPoolModel, CardModel[]> pools = new(ReferenceEqualityComparer.Instance);
        foreach (var pool in ModelDb.AllCardPools)
        {
            if (pool.GetType().Assembly != typeof(CardModel).Assembly || pool.IsMutable || pool.IsMock
                || !ReferenceEquals(pool, ModelDb.GetById<CardPoolModel>(pool.Id))) continue;
            var cards = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint).ToArray();
            if (cards.Any(card => card.IsMutable || !ReferenceEquals(card, card.CanonicalInstance)))
                throw new PredictionUnsupportedException($"Mystery cannot freeze mutable pool {pool.Id}.");
            pools.Add(pool, cards);
        }
        _pools = pools.ToFrozenDictionary(ReferenceEqualityComparer.Instance);
        _poolIdentity = string.Join(";", pools.OrderBy(pair => pair.Key.Id.ToString(), StringComparer.Ordinal)
            .Select(pair => pair.Key.Id + "=" + string.Join(",", pair.Value.Select(HextechStableRandom.CardKey))));
    }
    public object Fork(PredictionForkContext context) => MemberwiseClone();
    internal static void Write(MysterySeedState state, ref ModelPredictionStateWriter writer)
    {
        writer.Add("seed", state.Seed);
        writer.Add("player", state.PlayerKey);
        writer.Add("act", state.Act);
        writer.Add("scalingAct", state.ScalingAct);
        writer.Add("floor", state.Floor);
        writer.Add("transformation_pools", state._poolIdentity);
    }
    internal IReadOnlyList<CardModel> Pool(CardPoolModel pool) => _pools.TryGetValue(pool, out var cards)
        ? cards : throw new PredictionUnsupportedException($"Mystery has no captured canonical pool {pool.Id}.");
    internal int Index(int count, IEnumerable<string?> salt)
    {
        List<string?> parts = [Seed, "|act:", Act.ToString(), "|floor:", Floor.ToString()];
        foreach (var part in salt) { parts.Add("|"); parts.Add(part ?? ""); }
        return HextechStableRandom.IndexFromRawParts(count, parts.ToArray());
    }
}

internal static class MysteryMirrors
{
    internal static void Register(Harmony harmony)
    {
        ModelPredictionStateMirrors.RegisterRelic<MysteryRune, MysterySeedState>("mystery-stable-transform-v2",
            (_, live) => new(live),
            (MysteryRune live, ref ModelPredictionStateWriter writer) => MysterySeedState.Write(new(live), ref writer),
            MysterySeedState.Write);
        harmony.Patch(AccessTools.Method(typeof(TurnStartChoiceSupport), nameof(TurnStartChoiceSupport.ResolveCapturedChoice)),
            prefix: new HarmonyMethod(typeof(MysteryMirrors), nameof(ResolveEntropy)));
    }

    private static CardModel[] Options(MysterySeedState seed, PredictedCard card)
    {
        CardModel original = card.Preview;
        if (!original.IsTransformable) return [];
        var pool = CombatCardGenerationExtensions.TransformationOptionPool(original);
        var cached = seed.Pool(pool);
        try { return CardTransformUpgradeHelper.GetStableTransformationOptions(original, cached, true); }
        catch (InvalidOperationException) { return []; }
    }

    private static bool ResolveEntropy(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        Player player, TurnStartChoiceCursor? cursor, TurnStartChoiceRequest request, ref bool __result)
    {
        if (request.SourceId != "ENTROPY_POWER" || request.Effect != PlanChoiceEffect.Transform
            || combat.RelicsOf(player).OfType<MysteryRune>().SingleOrDefault() is not { } rune)
            return true;
        int amount = combat.GetAmount<EntropyPower>(player.Creature);
        var seed = ModelPredictionStateMirrors.Get<MysterySeedState>(simulator, rune);
        var hand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
        var options = hand.Where(card => Options(seed, card).Length > 0).ToArray();
        int count = Math.Min(amount, options.Length);
        if (count <= 0) { __result = true; return false; }
        var spec = new CardChoiceSpec(PlanChoiceEffect.Transform, PileType.Hand, count, count, options,
            hand, 0d, IsImplicitAllSelection: options.Length <= amount);
        request = request with { Spec = spec, Count = count };
        if (cursor == null || !cursor.TryTake(request, out PlanCardChoice? choice))
        {
            if (!combat.HasPendingChoice)
            {
                combat.SetPendingTurnStartChoice(request);
                simulator.CaptureExecutionChoice(new SimulatedCombatState.TurnSelectionExecutionFrame(player, request));
            }
            else simulator.AppendExecutionContinuation(new SimulatedCombatState.TurnSelectionExecutionFrame(player, request));
            __result = false;
            return false;
        }
        var selected = CardChoiceSupport.ResolveStandaloneChoice(simulator, choice!, options, count, PileType.Hand);
        string selectionKey = string.Join(",", selected.Select(card => card.Preview.Id.Entry));
        for (int i = 0; i < selected.Count; i++)
        {
            var card = selected[i];
            var preview = card.Preview;
            var pile = card.GetPile(simulator.State)!;
            int index = pile.Cards.ToList().FindIndex(candidate => ReferenceEquals(candidate, card));
            string actionKey = string.Join(":", preview.Id.Entry, seed.PlayerKey, "play",
                preview.CurrentPlayIndex, "target", preview.CurrentTarget?.CombatId?.ToString() ?? "none",
                "pile", $"{pile.Type}:{index}");
            var replacements = Options(seed, card);
            string?[] salt = ["entropy-transform-replacement", seed.PlayerKey, preview.Id.Entry, i.ToString(),
                actionKey, seed.PlayerKey, combat.RoundNumber.ToString(), amount.ToString(), selectionKey,
                "pool", string.Join(",", replacements.Select(HextechStableRandom.CardKey))];
            var replacement = PredictedCard.Create(replacements[seed.Index(replacements.Length, salt)], player);
            int upgrades = CardTransformUpgradeHelper.GetUpgradeRestorationSteps(
                replacement.Preview.CurrentUpgradeLevel, preview.CurrentUpgradeLevel, replacement.Preview.MaxUpgradeLevel);
            for (int step = 0; step < upgrades && replacement.Preview.IsUpgradable; step++) replacement.Upgrade();
            if (!CardChoiceSupport.TransformCardToGeneratedReplacement(simulator, card, replacement.MutablePreview)
                || combat.HasPendingChoice)
            {
                simulator.RejectExecutionContinuation();
                __result = false;
                return false;
            }
        }
        __result = true;
        return false;
    }
}
