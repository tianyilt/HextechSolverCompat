using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static MysterySeedState? _stableGeneration;

    private static void RegisterStableGeneration(Harmony harmony)
    {
        RegisterGeneratedReactions(harmony);
        // Freeze seed/unlocks once. All native sorting, weighting, salts and
        // proc-ordinal rules remain in Hextech's original callback.
        harmony.Patch(AccessTools.Method(typeof(HextechStableRandom), nameof(HextechStableRandom.Index)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(StableIndex)));
        harmony.Patch(AccessTools.Method(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(FrozenGenerationPool)));
        RegisterStableExhaustGenerator<CorruptedBranchRune>(harmony);
        RegisterStableState<BlankCheckRune>();
        RuneMirrors.RegisterNativeBase<BlankCheckRune>();
        PatchSingleGenerator<BlankCheckRune>(harmony, nameof(BlankCheckRune.AfterPlayerTurnStart));
        AfterPlayerTurnStartMirrors.Register<BlankCheckRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(BlankCheckRune)));
        RegisterNativeQueryCallbacks<BlankCheckRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterNativeResultLocation<BlankCheckRune>();
    }

    private static void RegisterStableExhaustGenerator<T>(Harmony harmony) where T : HextechRelicBase
    {
        RegisterStableState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        PatchSingleGenerator<T>(harmony, nameof(HextechRelicBase.AfterCardExhausted));
        AfterCardExhaustedMirrors.Registry.Register<T>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterCardExhausted(
                new ThrowingPlayerChoiceContext(), context.Card.MutablePreview, context.CausedByEthereal)), typeof(T)));
    }

    private static void RegisterStableState<T>() where T : HextechRelicBase
    {
        _ = NativeRuneState.Fields(typeof(T));
        ModelPredictionStateMirrors.RegisterRelic<T, NativeRuneState>("native-stable-generation-v1",
            (simulator, live) =>
            {
                var state = new NativeRuneState(NativeRuneState.Clone(live)) { StableGeneration = new(live) };
                if (live is VoltaicUpgradeRune) state.FrozenOrbChannels = CaptureFrozenOrbChannels(live.Owner);
                state.CaptureQueue(simulator);
                return state;
            },
            (T live, ref ModelPredictionStateWriter writer) =>
            {
                NativeRuneState.WriteModel(live, ref writer);
                MysterySeedState.Write(new(live), ref writer);
                if (live is VoltaicUpgradeRune) WriteFrozenOrbChannels(CaptureFrozenOrbChannels(live.Owner), ref writer);
            }, NativeRuneState.WriteState);
    }

    private static void PatchSingleGenerator<T>(Harmony harmony, string method) where T : HextechRelicBase
    {
        var callback = AccessTools.DeclaredMethod(typeof(T), method);
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeof(T).Name} exhaust callback shape changed.");
        harmony.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteSingleGeneratedCard)));
    }

    private static bool StableIndex(int count, string?[] saltParts, ref int __result)
    {
        if (_nativePotionGeneration is { } potionSeed)
        { __result = potionSeed.Index(count, saltParts); return false; }
        if (_nativeEnemyTurn is { } enemy && enemy.State.StableSeed is { } seed)
        { __result = seed.Index(count, saltParts); return false; }
        if (_stableGeneration is null) return true;
        __result = _stableGeneration.Index(count, saltParts);
        return false;
    }

    private static bool FrozenGenerationPool(CardPoolModel __instance, ref IEnumerable<CardModel> __result)
    {
        if (_stableGeneration is null) return true;
        __result = _stableGeneration.Pool(__instance);
        return false;
    }

    private static IEnumerable<CodeInstruction> RewriteSingleGeneratedCard(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var native = AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(AddNativeGeneratedCard));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed single-card generator changed: {count} call sites.");
    }

    private static Task<CardPileAddResult?> AddNativeGeneratedCard(CardModel card, PileType pileType,
        bool addedByPlayer, CardPilePosition position, bool previewNonHandAdds)
    {
        if (_simulator is null)
            return HextechCardGeneration.AddGeneratedCardToCombat(card, pileType, addedByPlayer, position, previewNonHandAdds);
        // Random generators freeze their pool/seed in separate query bridges.
        // This insertion command also serves reviewed fixed-card generators.
        if (!card.IsMutable || _simulator.State.FindCard(card) is not null)
            throw new PredictionUnsupportedException("Native generator did not create a detached new card.");
        var result = _simulator.AddGeneratedCardToCombat(PredictedCard.FromGenerated(card), pileType,
            addedByPlayer ? card.Owner : null, position);
        PauseNativeChoice(_simulator);
        return Task.FromResult<CardPileAddResult?>(new CardPileAddResult
        {
            success = result.Success, cardAdded = card, oldPile = null,
            targetPile = pileType, modifyingModels = []
        });
    }
}
