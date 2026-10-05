using System.Globalization;
using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal sealed partial class NativeRuneState
{
    private static readonly FieldInfo FreeCards = AccessTools.DeclaredField(typeof(EndlessRotationRune), "_freeCards");
    private static readonly FieldInfo DamageBonuses = AccessTools.DeclaredField(typeof(BloodDebtRune), "_damageBonuses");
    private static readonly FieldInfo StormAtStart = AccessTools.DeclaredField(typeof(StormUpgradeRune), "_stormLightningAtCardStart");
    private static bool IsCardLedger(FieldInfo field) => field == FreeCards || field == DamageBonuses || field == StormAtStart;
    private readonly Dictionary<FieldInfo, List<(PredictedCard Card, decimal Amount)>?> _cardLedgers = [];

    private static List<(CardModel Card, decimal Amount)>? ReadCardLedger(FieldInfo field, HextechRelicBase model)
        => field.GetValue(model) switch
        {
            null => null,
            HashSet<CardModel> cards => cards.Select(card => (card, 0m)).ToList(),
            Dictionary<CardModel, decimal> values => values.Select(pair => (pair.Key, pair.Value)).ToList(),
            Dictionary<CardModel, int> values => values.Select(pair => (pair.Key, (decimal)pair.Value)).ToList(),
            _ => throw new PredictionUnsupportedException("Unreviewed native card ledger shape: " + field.Name)
        };
    private static void SetCardLedger(FieldInfo field, HextechRelicBase model, List<(CardModel Card, decimal Amount)>? entries)
    {
        object? value = entries is null ? null : field == FreeCards
            ? new HashSet<CardModel>(entries.Select(entry => entry.Card), ReferenceEqualityComparer.Instance)
            : field == DamageBonuses
                ? entries.ToDictionary(entry => entry.Card, entry => entry.Amount)
                : entries.ToDictionary(entry => entry.Card, entry => checked((int)entry.Amount));
        field.SetValue(model, value);
    }
    private static void CloneCardLedgers(HextechRelicBase source, HextechRelicBase clone)
    {
        foreach (var field in Fields(source.GetType()).Where(IsCardLedger))
            SetCardLedger(field, clone, ReadCardLedger(field, source));
    }
    internal void BindCardLedgers()
    {
        foreach (var (field, entries) in _cardLedgers)
            SetCardLedger(field, Model, entries?.Select(entry => (entry.Card.MutablePreview, entry.Amount)).ToList());
    }
    private void CaptureCardLedgers(CombatPredictionSimulator simulator)
    {
        foreach (var field in Fields(Model.GetType()).Where(IsCardLedger))
        {
            var entries = ReadCardLedger(field, Model);
            // These callbacks only consume card instances still in combat piles.
            // Removed cards cannot re-enter by these effects; stale map entries
            // are inert and excluded from both live and predicted observations.
            _cardLedgers[field] = entries?.Select(entry => (Card: simulator.State.FindCard(entry.Card), entry.Amount))
                .Where(entry => entry.Card is not null).Select(entry => (entry.Card!, entry.Amount)).ToList();
        }
        BindCardLedgers();
    }
    private void ForkCardLedgers(NativeRuneState copy, PredictionForkContext context)
    {
        foreach (var (field, entries) in _cardLedgers)
            copy._cardLedgers.Add(field, entries?.Select(entry => (context.RequireRemap(entry.Card), entry.Amount)).ToList());
        copy.BindCardLedgers();
    }
    private static void WriteLiveCardLedgers(HextechRelicBase model, ref ModelPredictionStateWriter writer)
    {
        foreach (var field in Fields(model.GetType()).Where(IsCardLedger))
        {
            var cards = model.Owner.PlayerCombatState!.AllCards.ToHashSet();
            var entries = ReadCardLedger(field, model)?.Where(entry => cards.Contains(entry.Card)).ToList();
            string name = "cardLedger:" + field.DeclaringType!.Name + "." + field.Name;
            writer.Add(name + ":present", entries is not null);
            var groups = entries?.GroupBy(entry => entry.Amount).OrderBy(group => group.Key).ToArray();
            writer.Add(name + ":groups", groups?.Length ?? 0);
            if (groups is null) continue;
            for (int index = 0; index < groups.Length; index++)
            {
                writer.Add(name + ":amount:" + index, groups[index].Key.ToString(CultureInfo.InvariantCulture));
                writer.AddCards(name + ":cards:" + index, groups[index].Select(entry => (CardModel?)entry.Card).ToArray(), unordered: true);
            }
        }
    }
    private void WriteCardLedgers(ref ModelPredictionStateWriter writer)
    {
        foreach (var field in Fields(Model.GetType()).Where(IsCardLedger))
        {
            var entries = _cardLedgers[field];
            string name = "cardLedger:" + field.DeclaringType!.Name + "." + field.Name;
            writer.Add(name + ":present", entries is not null);
            var groups = entries?.GroupBy(entry => entry.Amount).OrderBy(group => group.Key).ToArray();
            writer.Add(name + ":groups", groups?.Length ?? 0);
            if (groups is null) continue;
            for (int index = 0; index < groups.Length; index++)
            {
                writer.Add(name + ":amount:" + index, groups[index].Key.ToString(CultureInfo.InvariantCulture));
                writer.AddCards(name + ":cards:" + index, groups[index].Select(entry => (PredictedCard?)entry.Card).ToArray(), unordered: true);
            }
        }
    }
}
