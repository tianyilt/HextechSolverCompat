using System.Runtime.CompilerServices;
using System.Globalization;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using NativeCounters = HextechRunes.HextechSelfUpgradeCardStore.Counters;

namespace HextechSolverCompat;

// The original store still performs every increment. Only its table and
// persistent-card identities change when it executes against a branch.
internal sealed class NativeSelfUpgradeState : IPredictionStateForkable
{
    private sealed record PersistentCard(CardModel Source, CardModel Model, bool InDeck, NativeCounters Counters);
    internal static bool HasAnchor(CombatPredictionSimulator simulator)
        => simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().Any()
            || simulator.State.CombatState.Players.SelectMany(player =>
                ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player)).Any(IsRune);
    private static AbstractModel StoreKey(CombatPredictionSimulator simulator)
        => (AbstractModel?)simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault()
            ?? ((SimulatedCombatState)simulator.State.CombatState).Players
                .SelectMany(player => ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player))
                .FirstOrDefault(IsRune)
            ?? throw new PredictionUnsupportedException("Native growth has no captured model anchor.");
    private static readonly ConditionalWeakTable<CardModel, NativeSelfUpgradeState> Owners = new();
    internal readonly ConditionalWeakTable<CardModel, NativeCounters> Table = new();
    private readonly List<PersistentCard> _persistent = [];
    private readonly Dictionary<PredictedCard, int> _links = [];
    private readonly Dictionary<PredictedCard, NativeCounters> _battleCounters = [];
    private SimPlayerCombatState _player = null!;
    private bool _allPersistent;
    internal static bool NeedsPermanentDeck(Player player) => player.Relics.OfType<SolidTimeRune>().Any()
        || player.Creature.CombatState?.Modifiers.OfType<HextechMayhemModifier>()
            .Any(modifier => modifier.GetActiveMonsterHexes().Contains(MonsterHexKind.ThievingHopper)) == true;
    private static bool IsCard(CardModel card) => card is Claw or Sow or Reap or IronWave;
    private static bool IsRune(RelicModel rune) => rune is ClawUpgradeRune or SowUpgradeRune or ReapUpgradeRune or IronWaveUpgradeRune or SolidTimeRune;
    private static NativeCounters CloneCounters(NativeCounters? source) => new() { Damage = source?.Damage ?? 0, Block = source?.Block ?? 0 };
    private static NativeCounters? LiveCounters(CardModel card)
        => HextechSelfUpgradeCardStore.BonusByCard.TryGetValue(card, out var counters) ? counters : null;
    internal static bool Needed(Player player) => NeedsPermanentDeck(player) || player.Relics.Any(IsRune)
        || player.Deck.Cards.Concat(player.PlayerCombatState!.AllCards)
            .Any(card => IsCard(card) || LiveCounters(card.DeckVersion ?? card) is not null);

    internal static NativeSelfUpgradeState? Capture(CombatPredictionSimulator simulator, Player player)
        => simulator.StateStore.Get(StoreKey(simulator), () => Create(simulator, player));
    internal static NativeSelfUpgradeState Require(CombatPredictionSimulator simulator)
        => simulator.StateStore.TryGetReadOnly<NativeSelfUpgradeState>(StoreKey(simulator), out var state) ? state!
            : throw new PredictionUnsupportedException("Permanent native card growth has no captured branch store.");

    private static NativeSelfUpgradeState Create(CombatPredictionSimulator simulator, Player player)
    {
        var state = new NativeSelfUpgradeState { _player = simulator.State.GetPlayerCombatState(player), _allPersistent = NeedsPermanentDeck(player) };
        var deck = player.Deck.Cards.ToArray();
        var battle = player.PlayerCombatState!.AllCards.ToArray();
        foreach (var source in deck.Concat(battle.Select(card => card.DeckVersion).OfType<CardModel>()).Distinct())
        {
            if (!state._allPersistent && !IsCard(source) && LiveCounters(source) is null) continue;
            var model = PredictionUtils.CloneCardStateForSimulation(source);
            model.DeckVersion = null;
            var counters = CloneCounters(LiveCounters(source));
            state._persistent.Add(new(source, model, deck.Contains(source), counters));
            state.Table.Add(model, counters);
            state.Bind(model);
        }
        foreach (var source in battle)
        {
            if (!(state._allPersistent && source.DeckVersion is not null) && !IsCard(source) && LiveCounters(source.DeckVersion ?? source) is null) continue;
            var card = simulator.State.FindCard(source)
                ?? throw new PredictionUnsupportedException("Native growth root card is absent from its branch.");
            if (source.DeckVersion is { } persistent)
            {
                int index = state._persistent.FindIndex(entry => ReferenceEquals(entry.Source, persistent));
                if (index < 0) throw new PredictionUnsupportedException("Native growth persistent root reference was not captured.");
                state._links.Add(card, index);
            }
            else state._battleCounters.Add(card, CloneCounters(LiveCounters(source)));
            state.BindCard(card);
        }
        return state;
    }
    private void Bind(CardModel model)
    {
        lock (Owners)
        {
            if (Owners.TryGetValue(model, out var owner) && !ReferenceEquals(owner, this))
                throw new PredictionUnsupportedException("Native growth card model is shared by two branch stores.");
            if (owner is null) Owners.Add(model, this);
        }
    }
    private void BindCard(PredictedCard card)
    {
        var model = card.MutablePreview;
        Bind(model);
        if (_battleCounters.TryGetValue(card, out var counters) && !Table.TryGetValue(model, out _)) Table.Add(model, counters);
    }
    internal static NativeSelfUpgradeState? OwnerOf(CardModel card) => Owners.TryGetValue(card, out var owner) ? owner : null;
    internal void BindCurrentCards()
    {
        foreach (var card in _player.AllCards)
        {
            if (!(_allPersistent && card.Preview.DeckVersion is not null) && !IsCard(card.Preview) && !_links.ContainsKey(card) && !_battleCounters.ContainsKey(card)) continue;
            if (!_links.ContainsKey(card) && !_battleCounters.ContainsKey(card))
            {
                // New combat cards have no live-side counter. Preserve a
                // captured deck link for native CreateCombatClone copies.
                int index = _persistent.FindIndex(entry => ReferenceEquals(entry.Source, card.Preview.DeckVersion)
                    || ReferenceEquals(entry.Model, card.Preview.DeckVersion));
                if (index >= 0) _links.Add(card, index);
                else if (card.Preview.DeckVersion is not null)
                    throw new PredictionUnsupportedException("Generated growth card has an uncaptured persistent reference.");
                else _battleCounters.Add(card, new());
            }
            BindCard(card);
        }
    }
    internal CardModel? DeckVersion(CardModel card)
    {
        if (_persistent.Any(entry => ReferenceEquals(entry.Model, card))) return null;
        // Original Swipe removes the combat card before consulting DeckVersion.
        // Retain its owned identity for the remainder of that command.
        var predicted = _player.AllCards.Concat(_links.Keys).FirstOrDefault(candidate => ReferenceEquals(candidate.MutablePreview, card));
        if (predicted is null) throw new PredictionUnsupportedException("Native growth received a live or foreign card model.");
        return _links.TryGetValue(predicted, out int index) ? _persistent[index].Model : null;
    }
    internal IReadOnlyList<CardModel> DeckCards => _persistent.Where(entry => entry.InDeck).Select(entry => entry.Model).ToArray();
    internal void RemovePersistent(CardModel model)
    {
        int index = _persistent.FindIndex(entry => ReferenceEquals(entry.Model, model));
        if (index < 0 || !_persistent[index].InDeck)
            throw new PredictionUnsupportedException("Permanent removal received an absent or foreign deck card.");
        _persistent[index] = _persistent[index] with { InDeck = false };
    }
    internal bool CapturesPermanentDeck => _allPersistent;
    internal IEnumerable<CardModel> CombatCards
    {
        get { BindCurrentCards(); return _player.AllCards.Select(card => card.MutablePreview).ToArray(); }
    }

    public object Fork(PredictionForkContext context)
    {
        if (context.TryRemap(this, out NativeSelfUpgradeState? existing)) return existing!;
        var copy = new NativeSelfUpgradeState { _player = context.RequireRemap(_player), _allPersistent = _allPersistent };
        context.Register(this, copy);
        foreach (var entry in _persistent)
        {
            var model = PredictionUtils.CloneCardStateForSimulation(entry.Model);
            var counters = CloneCounters(entry.Counters);
            copy._persistent.Add(new(entry.Source, model, entry.InDeck, counters));
            copy.Table.Add(model, counters);
            context.Register(entry.Model, model);
            copy.Bind(model);
        }
        var active = _player.AllCards.ToHashSet();
        foreach (var entry in _links.Where(entry => active.Contains(entry.Key))) copy._links.Add(context.RequireRemap(entry.Key), entry.Value);
        foreach (var entry in _battleCounters.Where(entry => active.Contains(entry.Key))) copy._battleCounters.Add(context.RequireRemap(entry.Key), CloneCounters(entry.Value));
        foreach (var card in copy._player.AllCards)
            if (copy._links.ContainsKey(card) || copy._battleCounters.ContainsKey(card)) copy.BindCard(card);
        return copy;
    }

    private static void WritePersistent(CardModel model, NativeCounters? counters, bool inDeck, ref ModelPredictionStateWriter writer)
    {
        writer.Add("growthPersistentId", model.Id.Entry);
        writer.Add("growthPersistentInDeck", inDeck);
        writer.Add("growthPersistentUpgrade", model.CurrentUpgradeLevel);
        if (model is MadScience science)
        {
            writer.Add("persistentMadScienceType", (int)science.TinkerTimeType);
            writer.Add("persistentMadScienceRider", (int)science.TinkerTimeRider);
        }
        writer.Add("growthPersistentDamage", counters?.Damage ?? 0);
        writer.Add("growthPersistentBlock", counters?.Block ?? 0);
        foreach (var variable in model.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Add("growthPersistentVar:" + variable.Key, variable.Value.BaseValue.ToString(CultureInfo.InvariantCulture));
    }
    internal void Write(ref ModelPredictionStateWriter writer)
    {
        var cards = _player.AllCards.Where(card => _links.ContainsKey(card) || _battleCounters.ContainsKey(card) || IsCard(card.Preview)).ToArray();
        // Match live deck order followed by surviving combat deck references.
        // Hidden removed entries remain owned for recovery, but are not live
        // resources and must not change the canonical index used by the key.
        var persistent = _persistent.Select((entry, index) => (entry, index)).Where(item => item.entry.InDeck)
            .Select(item => item.index).Concat(cards.Where(_links.ContainsKey).Select(card => _links[card])).Distinct().ToArray();
        writer.Add("growthPersistentCount", persistent.Length);
        foreach (int index in persistent) { var entry = _persistent[index]; WritePersistent(entry.Model, entry.Counters, entry.InDeck, ref writer); }
        writer.Add("growthBattleCount", cards.Length);
        foreach (var card in cards)
        {
            writer.AddCard("growthBattleCard", card);
            int storedIndex = _links.GetValueOrDefault(card, -1);
            int index = storedIndex < 0 ? -1 : Array.IndexOf(persistent, storedIndex);
            writer.Add("growthDeckLink", index);
            if (index >= 0) continue;
            var counters = _battleCounters.GetValueOrDefault(card);
            writer.Add("growthBattleDamage", counters?.Damage ?? 0);
            writer.Add("growthBattleBlock", counters?.Block ?? 0);
        }
    }
    internal static void WriteLive(Player player, ref ModelPredictionStateWriter writer)
    {
        var deck = player.Deck.Cards.ToArray();
        var cards = player.PlayerCombatState!.AllCards.ToArray();
        var persistent = deck.Concat(cards.Select(card => card.DeckVersion).OfType<CardModel>()).Distinct()
            .Where(card => NeedsPermanentDeck(player) || IsCard(card) || LiveCounters(card) is not null).ToArray();
        writer.Add("growthPersistentCount", persistent.Length);
        foreach (var model in persistent) WritePersistent(model, LiveCounters(model), deck.Contains(model), ref writer);
        var battle = cards.Where(card => NeedsPermanentDeck(player) && card.DeckVersion is not null || IsCard(card) || LiveCounters(card.DeckVersion ?? card) is not null).ToArray();
        writer.Add("growthBattleCount", battle.Length);
        foreach (var card in battle)
        {
            writer.AddCard("growthBattleCard", card);
            int index = card.DeckVersion is null ? -1 : Array.IndexOf(persistent, card.DeckVersion);
            writer.Add("growthDeckLink", index);
            if (index >= 0) continue;
            var counters = LiveCounters(card);
            writer.Add("growthBattleDamage", counters?.Damage ?? 0);
            writer.Add("growthBattleBlock", counters?.Block ?? 0);
        }
    }
}
