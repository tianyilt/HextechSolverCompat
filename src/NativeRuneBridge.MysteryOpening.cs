using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeMysteryOpening(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MysteryEnemyHex), "BeforePlayerSideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking)),
            Site(AccessTools.Method(typeof(CardCmd), "Transform",
                [typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle)]), nameof(TransformNativeMysteryCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MysteryEnemyHex), "ChooseCards"),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), "AllCards"), nameof(NativeOwnedCombatCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CardTransformUpgradeHelper), "CanTransformToRandomCardInCombatPiles"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "Pile"), nameof(NativeBranchCardPile)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CardTransformUpgradeHelper), "CreateStableReplacement"),
            Site(AccessTools.PropertyGetter(typeof(CardModel), "CardScope"), nameof(NativeTransformationScope)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CardTransformUpgradeHelper), "RestoreUpgradeLevel"),
            Site(AccessTools.Method(typeof(CardCmd), "Upgrade", [typeof(CardModel), typeof(CardPreviewStyle)]), nameof(UpgradeNativeTransformation)));
        foreach (string method in new[] { "HasRandomTransformationOptions", "CreateStableOptionTransformation",
            "GetStableTransformationOptions", "PreserveUpgradeLevel", "GetUpgradeRestorationSteps", "BuildStableTransformSalt", "FilterForPlayerCount" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(CardTransformUpgradeHelper), method));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(MysteryEnemyHex), "GetPriorityTier"));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.Mystery);
    }

    // The reviewed helper only uses ICardScope.CreateCard. This ephemeral
    // capability writes to its simulator and never exposes the actual scope.
    private sealed class NativeTransformationCardScope(CombatPredictionSimulator simulator) : ICardScope
    {
        T ICardScope.CreateCard<T>(Player owner)
            => (T)simulator.State.CombatState.CreateCard(ModelDb.Card<T>(), owner);
        public CardModel CreateCard(CardModel canonicalCard, Player owner)
            => simulator.State.CombatState.CreateCard(canonicalCard, owner);
        public CardModel CloneCard(CardModel mutableCard)
            => throw new PredictionUnsupportedException("Unreviewed transformation scope CloneCard.");
        public void AddCard(CardModel mutableCard, Player owner)
            => throw new PredictionUnsupportedException("Unreviewed transformation scope AddCard.");
        public void RemoveCard(CardModel card)
            => throw new PredictionUnsupportedException("Unreviewed transformation scope RemoveCard.");
    }
    private static ICardScope? NativeTransformationScope(CardModel card)
        => _simulator is null ? card.CardScope : new NativeTransformationCardScope(_simulator);

    private static void UpgradeNativeTransformation(CardModel card, CardPreviewStyle style)
    {
        if (_simulator is null) { CardCmd.Upgrade(card, style); return; }
        if (_simulator.State.FindCard(card) is { } existing) { existing.Upgrade(); return; }
        if (!card.IsMutable || card.Owner is null)
            throw new PredictionUnsupportedException("Native transformation upgrade is not a detached owned replacement.");
        PredictedCard.FromGenerated(card).Upgrade();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<IEnumerable<CardPileAddResult>> TransformNativeMysteryCards(
        IEnumerable<CardTransformation> transformations, Rng? rng, CardPreviewStyle preview)
        => TransformNativeFixedCards(transformations, rng, preview);
}
