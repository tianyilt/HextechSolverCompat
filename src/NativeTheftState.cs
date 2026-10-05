using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

namespace HextechSolverCompat;

// Frozen permanent-card payloads. Death creates a reward, not an immediate
// return to the deck; escape creates no reward. Actual reward selection remains
// the original game transaction outside the solver's combat search.
internal sealed class NativeTheftState : IPredictionStateForkable
{
    private readonly SortedDictionary<uint, List<CardModel>> _stolen = [];
    private readonly List<CardModel> _rewards = [];
    private readonly SortedSet<uint> _planned = [];
    private static CardModel Freeze(CardModel card)
    {
        var copy = PredictionUtils.CloneCardStateForSimulation(card);
        copy.DeckVersion = null;
        return copy;
    }
    internal static NativeTheftState Capture(HextechMayhemModifier modifier)
    {
        var state = new NativeTheftState();
        var combat = modifier.ActiveRunState.Players.Single().Creature.CombatState!;
        foreach (var enemy in combat.Enemies.Where(enemy => enemy.IsAlive))
        {
            if (enemy.Monster?.NextMove.Intents.Any(intent => intent is ThievingHopperTheftIntent) == true)
                state._planned.Add(enemy.CombatId!.Value);
            foreach (var swipe in enemy.Powers.OfType<SwipePower>())
                if (swipe.StolenCard?.DeckVersion is { } card)
                    state.Steal(enemy.CombatId!.Value, card);
        }
        if (modifier.ActiveRunState.CurrentRoom is CombatRoom room)
            foreach (var reward in room.ExtraRewards.Values.SelectMany(rewards => rewards).OfType<SpecialCardReward>())
                if (!(bool)AccessTools.Field(typeof(SpecialCardReward), "_wasTaken").GetValue(reward)!
                    && Equals(AccessTools.Field(typeof(SpecialCardReward), "_customDescriptionEncounterSourceId").GetValue(reward),
                        ModelDb.Encounter<ThievingHopperWeak>().Id))
                    state._rewards.Add(Freeze((CardModel)AccessTools.Field(typeof(SpecialCardReward), "_card").GetValue(reward)!));
        return state;
    }
    internal void Steal(uint id, CardModel permanent)
    {
        if (!_stolen.TryGetValue(id, out var cards)) _stolen.Add(id, cards = []);
        cards.Add(Freeze(permanent));
    }
    internal void SetPlanned(uint id, bool planned) { if (planned) _planned.Add(id); else _planned.Remove(id); }
    internal void Escape(uint id) { _stolen.Remove(id); _planned.Remove(id); }
    internal void BeforeDeath(uint id)
    {
        if (_stolen.TryGetValue(id, out var cards) && cards.Count > 0)
        {
            _rewards.Add(cards[0]); cards.RemoveAt(0);
            if (cards.Count == 0) _stolen.Remove(id);
        }
        _planned.Remove(id);
    }
    public object Fork(PredictionForkContext context)
    {
        var copy = new NativeTheftState();
        foreach (var (id, cards) in _stolen) copy._stolen.Add(id, cards.Select(Freeze).ToList());
        copy._rewards.AddRange(_rewards.Select(Freeze));
        copy._planned.UnionWith(_planned);
        return copy;
    }
    private static void WriteCard(CardModel card, ref ModelPredictionStateWriter writer)
    {
        writer.Add("theftCardId", card.Id.Entry);
        writer.Add("theftUpgrade", card.CurrentUpgradeLevel);
        writer.Add("theftEnchantment", card.Enchantment is null ? null : EnchantmentStateSupport.Describe(card.Enchantment));
        writer.Add("theftAffliction", card.Affliction?.Id.Entry);
        writer.Add("theftAfflictionAmount", card.Affliction?.Amount ?? 0);
        writer.Add("theftKeywordCount", card.Keywords.Count);
        foreach (var keyword in card.Keywords.Order()) writer.Add("theftKeyword", (int)keyword);
        foreach (var variable in card.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Add("theftVar:" + variable.Key, variable.Value.BaseValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (card is MegaCrit.Sts2.Core.Models.Cards.MadScience science)
        {
            writer.Add("theftScienceType", (int)science.TinkerTimeType);
            writer.Add("theftScienceRider", (int)science.TinkerTimeRider);
        }
    }
    internal void Write(ref ModelPredictionStateWriter writer)
    {
        writer.Add("theftStolenCount", _stolen.Values.Sum(cards => cards.Count));
        writer.Add("theftPlannedCount", _planned.Count);
        foreach (uint id in _planned) writer.Add("theftPlannedOwner", id);
        foreach (var (id, cards) in _stolen)
            foreach (var card in cards) { writer.Add("theftOwner", id); WriteCard(card, ref writer); }
        writer.Add("theftRewardCount", _rewards.Count);
        foreach (var card in _rewards) WriteCard(card, ref writer);
    }
}
