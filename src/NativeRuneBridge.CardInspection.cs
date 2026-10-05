using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record InspectedDrawFrame(Player Player, int Remaining, int MaxHand,
        List<PredictedCard> Drawn, IReadOnlyList<PredictedCard>? Selected = null, int Next = 0,
        bool Shuffled = false, CombatPredictionCardDrawnEntry? PendingEntry = null,
        PredictedCard? PendingCard = null) : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CombatPredictionHistoryEntry> DeferredEntries => PendingEntry is null ? [] : [PendingEntry];
        public void PrepareFork(PredictionForkContext context)
        {
            CombatPredictionSimulator.ForkExecutionCardList(Drawn, context);
            if (Selected is not null)
                foreach (var card in Selected)
                    if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Drawn = context.RequireRemap(Drawn),
                Selected = Selected?.Select(card => context.RequireRemap(card)).ToArray(),
                PendingEntry = PendingEntry is null ? null : context.RequireRemap(PendingEntry),
                PendingCard = PendingCard is null ? null : context.RequireRemap(PendingCard) };
        public bool Resume(CombatPredictionSimulator simulator) => ContinueInspectedDraw(simulator, this);
    }

    private static void RegisterNativeCardInspection(Harmony harmony)
    {
        RegisterState<CardInspectionRune>(); RuneMirrors.RegisterNativeBase<CardInspectionRune>();
        var method = AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "DrawCore");
        var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(InspectedDrawPrefix));
        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(method, prefix, "HextechSolverCompat", Priority.Normal);
    }

    private static bool InspectedDrawPrefix(CombatPredictionSimulator __instance, Player player,
        int drawCount, bool fromHandDraw, ref IReadOnlyList<PredictedCard> __result)
    {
        var combat = (SimulatedCombatState)__instance.State.CombatState;
        if (!fromHandDraw || !combat.RelicsOf(player).Any(rune => rune is CardInspectionRune)) return true;
        List<PredictedCard> drawn = [];
        __result = drawn;
        if (drawCount <= 0 || __instance.IsOverOrEnding || !HookMirrors.ShouldDraw(__instance, player, true, out _)) return false;
        int space = Math.Max(0, __instance.GetMaxHandSize(player) - __instance.State.GetPlayerCombatState(player).Hand.Cards.Count);
        if (space == 0) return false;
        ContinueInspectedDraw(__instance, new(player, Math.Min(space, drawCount), __instance.GetMaxHandSize(player), drawn));
        return false;
    }

    private static bool ContinueInspectedDraw(CombatPredictionSimulator simulator, InspectedDrawFrame frame)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var owner = simulator.State.GetPlayerCombatState(frame.Player);
        if (frame.PendingEntry is not null)
        {
            simulator.History.CardDrawResolved(frame.PendingEntry, frame.PendingCard!);
            frame = frame with { PendingEntry = null, PendingCard = null };
        }
        while (frame.Remaining > 0 && !simulator.IsOverOrEnding && owner.Hand.Cards.Count < frame.MaxHand)
        {
            if (frame.Selected is null)
            {
                // Native selected draw shuffles BEFORE presenting the options,
                // even if some cards remain in Draw. Its shuffle hooks may
                // suspend; resume at selection, never repeat that shuffle.
                if (!frame.Shuffled && owner.DrawPile.Cards.Count < frame.Remaining && !owner.DiscardPile.IsEmpty)
                {
                    simulator.Shuffle(frame.Player);
                    frame = frame with { Shuffled = true };
                    if (simulator.HasPendingChoice) { simulator.AppendExecutionContinuation(frame); return false; }
                }
                int count = Math.Min(frame.Remaining, Math.Min(owner.DrawPile.Cards.Count, frame.MaxHand - owner.Hand.Cards.Count));
                if (count <= 0) break;
                var options = owner.DrawPile.Cards.ToArray();
                var spec = new CardChoiceSpec(PlanChoiceEffect.MoveToHand, PileType.Draw, count, count,
                    options, options, 0d, IsImplicitAllSelection: count == options.Length);
                var request = new TurnStartChoiceRequest("CARD_INSPECTION_RUNE", spec.Effect, spec.SourcePile,
                    count, spec, Timing: combat.ActiveActionChoiceTiming);
                var cursor = (TurnStartChoiceCursor?)NativeActionChoices.GetValue(combat);
                IReadOnlyList<PredictedCard> selected;
                if (count == options.Length)
                {
                    selected = cursor is not null && cursor.TryTakeIfMatches(request, out var implicitChoice)
                        ? CardChoiceSupport.ResolveStandaloneChoice(simulator, implicitChoice!, options, count, PileType.Draw) : options;
                    combat.ClearPendingTurnStartChoice();
                }
                else
                {
                    if (cursor is null || !cursor.TryTake(request, out var choice))
                    {
                        combat.SetPendingTurnStartChoice(request);
                        simulator.CaptureExecutionChoice(frame with { Shuffled = true });
                        return false;
                    }
                    selected = CardChoiceSupport.ResolveStandaloneChoice(simulator, choice!, options, count, PileType.Draw);
                    if (selected.Distinct().Count() != count)
                        throw new InvalidPlannedChoiceBranchException("Selected draw requires distinct physical cards.");
                    combat.ClearPendingTurnStartChoice();
                }
                frame = frame with { Selected = selected, Next = 0 };
            }
            int roundDrawn = 0;
            for (int index = frame.Next; index < frame.Selected.Count; index++)
            {
                if (frame.Remaining == 0 || simulator.IsOverOrEnding || owner.Hand.Cards.Count >= frame.MaxHand) break;
                var card = frame.Selected[index];
                if (!owner.DrawPile.Cards.Contains(card)) continue;
                frame.Drawn.Add(card);
                simulator.AddToPile(card, owner.Hand);
                var entry = simulator.History.CardDrawn(card, true);
                if (simulator.State.CombatState is ICombatPredictionCardEventSink events) events.RecordCardDrawn(card, true);
                frame = frame with { Remaining = frame.Remaining - 1, Next = index + 1 };
                roundDrawn++;
                HookMirrors.AfterCardDrawn(simulator, card, true);
                if (simulator.HasPendingChoice)
                {
                    simulator.AppendExecutionContinuation(frame with { PendingEntry = entry, PendingCard = card });
                    return false;
                }
                simulator.History.CardDrawResolved(entry, card);
            }
            if (roundDrawn == 0 && frame.Next == 0) break;
            frame = frame with { Selected = null, Next = 0, Shuffled = false };
        }
        return true;
    }
}
