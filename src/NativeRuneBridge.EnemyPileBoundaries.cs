using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyPileKinds = [MonsterHexKind.DuffsVintage,
        MonsterHexKind.ForgottenSoul, MonsterHexKind.Aeonglass, MonsterHexKind.IInspect];
    private static readonly Func<HextechEnemyHexContext, PlayerChoiceContext, CombatSide, CombatRoom?, Task>[] NativePileEnd =
    [
        AccessTools.DeclaredMethod(typeof(DuffsVintageEnemyHex), "BeforeTurnEnd").CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CombatSide, CombatRoom?, Task>>(new DuffsVintageEnemyHex()),
        AccessTools.DeclaredMethod(typeof(ForgottenSoulEnemyHex), "BeforeTurnEnd").CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CombatSide, CombatRoom?, Task>>(new ForgottenSoulEnemyHex())
    ];
    private static readonly Func<HextechEnemyHexContext, PlayerChoiceContext, Player, Task> NativeAeonglassStart =
        AccessTools.DeclaredMethod(typeof(AeonglassEnemyHex), "AfterPlayerTurnStartLate").CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, Player, Task>>(new AeonglassEnemyHex());
    private static readonly Func<HextechEnemyHexContext, Player, bool, bool> NativeInspectDraw =
        AccessTools.DeclaredMethod(typeof(IInspectEnemyHex), "ShouldDraw").CreateDelegate<Func<HextechEnemyHexContext, Player, bool, bool>>(new IInspectEnemyHex());

    private static void RegisterNativeEnemyPileBoundaries(Harmony harmony)
    {
        foreach (var type in new[] { typeof(DuffsVintageEnemyHex), typeof(ForgottenSoulEnemyHex) })
        {
            var sites = new List<NativeCallSite>
            {
                Site(AccessTools.PropertyGetter(typeof(CombatRoom), "CombatState"), nameof(NativePileRoomCombat)),
                Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "GetAlivePlayerSideCreaturesTakingTurn"), nameof(NativePileTakingTurn)),
                Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeBranchPileCards))
            };
            if (type == typeof(ForgottenSoulEnemyHex)) sites.Add(Site(AccessTools.Method(typeof(CardPileCmd), "Add",
                [typeof(IEnumerable<CardModel>), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativePileCards)));
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "BeforeTurnEnd"), sites.ToArray());
        }
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DuffsVintageEnemyHex), "ShouldFlush"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AeonglassEnemyHex), "AfterPlayerTurnStartLate"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AeonglassEnemyHex), "AfterShuffle"),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), "AllCards"), nameof(NativeOwnedCombatCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(IInspectEnemyHex), "ShouldDraw"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking)));
        foreach (string name in new[] { "TryPreventExtraDraw" }) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(IInspectEnemyHex), name));
        foreach (string name in new[] { "IsPlayerTurnStart", "EnterPlayerPlayPhase" }) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechMayhemCombatTrackingState), name));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyPileKinds);
    }

    private static ICombatState NativePileRoomCombat(CombatRoom room) => _simulator?.State.CombatState ?? room.CombatState;
    private static IReadOnlyList<Creature> NativePileTakingTurn(ref HextechEnemyHexContext context, ICombatState combat)
        => _simulator is null ? context.GetAlivePlayerSideCreaturesTakingTurn(combat)
            : combat.PlayerCreatures.Where(creature => _simulator.State.GetCreature(creature).IsAlive).ToArray();
    private static Task<IReadOnlyList<CardPileAddResult>> MoveNativePileCards(IEnumerable<CardModel> cards, PileType pile,
        CardPilePosition position, AbstractModel? source, bool skipVisuals)
    {
        if (_simulator is null) return CardPileCmd.Add(cards, pile, position, source, skipVisuals);
        var result = cards.ToArray().Select(card => MoveNativeCard(card, pile, position, source, skipVisuals).GetAwaiter().GetResult()).ToArray();
        return Task.FromResult<IReadOnlyList<CardPileAddResult>>(result);
    }
    internal static void DispatchNativeEnemyPileEnd(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, CombatSide side)
    {
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        {
            for (int index = 0; index < NativePileEnd.Length; index++)
                if (state.Has(NativeEnemyPileKinds[index])) RequireCompleted(NativePileEnd[index](context,
                    new ThrowingPlayerChoiceContext(), side, modifier.ActiveRunState.CurrentRoom as CombatRoom), index == 0 ? typeof(DuffsVintageEnemyHex) : typeof(ForgottenSoulEnemyHex));
            return Task.CompletedTask;
        });
    }
    internal static void DispatchNativeEnemyPileStart(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, Player player)
    {
        if (state.Has(MonsterHexKind.Aeonglass)) InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeAeonglassStart(context, new ThrowingPlayerChoiceContext(), player));
    }
    internal static bool NativeEnemyShouldDraw(HextechMayhemModifier modifier, EnemyState state, CombatPredictionSimulator simulator, Player player, bool fromHandDraw)
    {
        if (!state.Has(MonsterHexKind.IInspect)) return true;
        bool result = true;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { result = NativeInspectDraw(context, player, fromHandDraw); return Task.CompletedTask; });
        return result;
    }
    private static void EnterNativeEnemyPlayPhase(CombatPredictionSimulator simulator, Player player)
    {
        foreach (var modifier in simulator.State.CombatState.IterateHookListeners().OfType<HextechMayhemModifier>())
            ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).NativeTurns?.Model.EnterPlayerPlayPhase(player.NetId);
    }
}
