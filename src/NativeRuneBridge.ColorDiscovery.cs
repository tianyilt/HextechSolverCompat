using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeColorDiscovery(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ColorDiscoveryRune), "BeforeHandDraw"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(CardSelectCmd), "FromSimpleGrid",
                [typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs)]), nameof(SelectNativeColorDiscovery)),
            Site(AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        foreach (string name in new[] { "BeforeCombatStart", "AfterCombatVictory", "PickOptions", "GetOtherCharacterCards", "GetOtherCharacterPools" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ColorDiscoveryRune), name));
        RegisterBeforeHandDrawRune<ColorDiscoveryRune>();
    }
    private static Task<IEnumerable<CardModel>> SelectNativeColorDiscovery(PlayerChoiceContext context,
        IReadOnlyList<CardModel> cards, Player player, CardSelectorPrefs prefs)
    {
        if (_simulator is not { } simulator) return CardSelectCmd.FromSimpleGrid(context, cards, player, prefs);
        if (prefs.MinSelect != 1 || prefs.MaxSelect != 1 || cards.Count is < 1 or > 3
            || cards.Any(card => !card.IsMutable || simulator.State.FindCard(card) is not null))
            throw new PredictionUnsupportedException("Native ColorDiscovery selector contract changed.");
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var options = cards.Select(PredictedCard.FromGenerated).ToArray();
        var spec = new CardChoiceSpec(PlanChoiceEffect.GenerateToHand, PileType.None, 1, 1, options, options, ReplacementValue: 0d);
        var request = new TurnStartChoiceRequest("COLOR_DISCOVERY_RUNE", spec.Effect, PileType.None, 1, spec,
            Timing: combat.ActiveActionChoiceTiming);
        var cursor = (TurnStartChoiceCursor?)NativeActionChoices.GetValue(combat);
        if (cursor is null || !cursor.TryTake(request, out var choice))
        {
            combat.SetPendingTurnStartChoice(request);
            PauseNativeChoice(simulator);
            throw new PredictionUnsupportedException("Native ColorDiscovery selector did not suspend.");
        }
        var selected = TurnStartChoiceSupport.ResolveTokens(choice!, options, 1, 1);
        combat.ClearPendingTurnStartChoice();
        // Keep the original callback's pending reward ID, free cost and insertion order.
        return Task.FromResult<IEnumerable<CardModel>>(selected.Select(card => card.MutablePreview).ToArray());
    }
}

