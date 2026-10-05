using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeOpeningAndGeneration(Harmony harmony)
    {
        // These four install state before the prediction root. The original
        // opening/after-combat commands still run in the game; stock captured
        // powers and pets supply their subsequent combat lifecycle.
        RegisterCapturedOpening<BeginningAndEndRune>();
        RegisterCapturedOpening<KeystoneHunterRune>();
        RegisterCapturedOpening<FleshAndBoneRune>();
        RegisterCapturedOpening<LingeringMightRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(FleshAndBoneRune), "AfterCombatEnd"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(LingeringMightRune), "AfterCombatEnd"));

        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var batch = AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardsToCombat");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(WraithRune), "BeforeHandDraw"),
            Site(dead, nameof(NativeBranchIsDead)), Site(batch, nameof(AddNativeGeneratedCards)));
        RegisterBeforeHandDrawRune<WraithRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(WraithRune), "ModifyDamageMultiplicativeCompat"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        RegisterNativeQueryCallbacks<WraithRune>(NativeQueries.DamageMultiplier);

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ExplosionArtRune), "BeforeHandDraw"),
            Site(dead, nameof(NativeBranchIsDead)), Site(batch, nameof(AddNativeGeneratedCards)));
        RegisterBeforeHandDrawRune<ExplosionArtRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SummonForthRune), "BeforeHandDraw"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards)), nameof(NativeRemovalAllCards)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)));
        var bladeFilter = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(SummonForthRune), "<>c"))
            .Single(method => method.Name.StartsWith("<BeforeHandDraw>b__", StringComparison.Ordinal));
        PatchEventCallback(harmony, bladeFilter,
            Site(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), nameof(NativeBranchCardPile)));
        RegisterBeforeHandDrawRune<SummonForthRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SummonForthRune), "BeforeCombatStart"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ServantMasterRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.Method(typeof(OstyCmd), nameof(OstyCmd.Summon),
                [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        RegisterCapturedOpening<ServantMasterRune>();
        AfterPlayerTurnStartMirrors.Register<ServantMasterRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(ServantMasterRune)));
    }

    private static void RegisterCapturedOpening<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), "BeforeCombatStart"));
    }

    private static Task<IReadOnlyList<CardPileAddResult>> AddNativeGeneratedCards(IEnumerable<CardModel> cards,
        PileType pile, bool addedByPlayer, CardPilePosition position, bool previewNonHandAdds)
    {
        if (_simulator is null) return HextechCardGeneration.AddGeneratedCardsToCombat(cards, pile, addedByPlayer, position, previewNonHandAdds);
        var generated = cards.ToArray();
        return AddNativeGeneratedBatch(generated, pile, addedByPlayer ? generated.FirstOrDefault()?.Owner : null, position);
    }
}
