using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly List<MethodBase> GeneratedReactionCallbacks = [];
    private static readonly Dictionary<MethodBase, MethodInfo> GeneratedReactionPatches = [];
    private static readonly Dictionary<MethodBase, (int Stars, int Draws, int Clones, int Adds, int Upgrades, int Dead)> GeneratedReactionCalls = [];

    private static void RegisterGeneratedReactions(Harmony harmony)
    {
        // These four original callbacks preserve creator predicates and listener order.
        // Echo uses raw insertion: dispatching generation again would recurse.
        var pileQuery = AccessTools.Method(typeof(EchoRune), "TryGetEchoPile");
        GeneratedReactionPatches.Add(pileQuery, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEchoPileRead)));
        harmony.Patch(pileQuery,
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteEchoPileRead)));
        RegisterGeneratedReaction<CondensedRadianceRune>(harmony, (1, 0, 0, 0, 0, 1));
        RegisterGeneratedReaction<ByproductRune>(harmony, (0, 1, 0, 0, 0, 1));
        RegisterGeneratedReaction<EchoRune>(harmony, (0, 0, 1, 1, 0, 1));
        RegisterGeneratedReaction<ManipulateRealityRune>(harmony, (0, 0, 0, 0, 1, 0));
        ValidateGeneratedReactionContracts();
        harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "CaptureLiveStamp"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AppendGeneratedReactionComposition)));
    }

    private static void RegisterGeneratedReaction<T>(Harmony harmony,
        (int Stars, int Draws, int Clones, int Adds, int Upgrades, int Dead) expected) where T : HextechRelicBase
    {
        var callback = AccessTools.DeclaredMethod(typeof(T), nameof(AbstractModel.AfterCardGeneratedForCombat));
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        MethodBase target = machine is null ? callback : AccessTools.Method(machine, "MoveNext");
        GeneratedReactionCallbacks.Add(callback);
        GeneratedReactionPatches.Add(target, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteGeneratedReaction)));
        GeneratedReactionCalls.Add(target, expected);
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteGeneratedReaction)));
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterCardGeneratedForCombatMirrors.Registry.Register<T>((relic, context) =>
            RequireCompleted(Invoke(relic, context.Simulator, model => model.AfterCardGeneratedForCombat(
                context.MutablePreviewCard, context.Creator)), typeof(T)));
    }

    internal static void ValidateGeneratedReactionContracts()
    {
        foreach (var target in GeneratedReactionCallbacks.Concat(GeneratedReactionPatches.Keys).Distinct())
        {
            var patches = Harmony.GetPatchInfo(target);
            bool patched = GeneratedReactionPatches.TryGetValue(target, out var expected);
            if (patches is null)
            {
                if (patched) throw new PredictionUnsupportedException("Native generation reaction transpiler is missing.");
                continue;
            }
            if (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 || patches.Finalizers.Count != 0
                || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0
                || patches.Transpilers.Count != (patched ? 1 : 0)
                || patched && (patches.Transpilers[0].owner != "HextechSolverCompat"
                    || patches.Transpilers[0].PatchMethod != expected || patches.Transpilers[0].priority != Priority.Normal
                    || patches.Transpilers[0].before.Length != 0 || patches.Transpilers[0].after.Length != 0))
                throw new PredictionUnsupportedException($"Unreviewed native generation reaction composition: {target.DeclaringType?.Name}.{target.Name}.");
        }
    }

    private static void AppendGeneratedReactionComposition(ref string? __result)
    {
        if (__result is null) return;
        string signature = string.Join(';', GeneratedReactionCallbacks.Concat(GeneratedReactionPatches.Keys).Distinct()
            .OrderBy(method => method.DeclaringType!.FullName + ":" + method.Name, StringComparer.Ordinal)
            .Select(method => AdaptedCardOnPlayMirrors.DescribeActual((MethodInfo)method, Harmony.GetPatchInfo(method), includeIndex: true)));
        __result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(__result + ":native-generated-reactions-v1:" + signature)));
    }

    private static IEnumerable<CodeInstruction> RewriteGeneratedReaction(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var stars = AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainStars));
        var clone = AccessTools.Method(typeof(CardModel), nameof(CardModel.CreateClone));
        var add = AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]);
        var upgrade = AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Upgrade), [typeof(CardModel), typeof(CardPreviewStyle)]);
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        (int Stars, int Draws, int Clones, int Adds, int Upgrades, int Dead) found = (0, 0, 0, 0, 0, 0);
        foreach (var instruction in instructions)
        {
            string? replacement = null;
            if (instruction.Calls(stars)) { found.Stars++; replacement = nameof(GainNativeStars); }
            else if (instruction.Calls(NativeDraw)) { found.Draws++; replacement = nameof(Draw); }
            else if (instruction.Calls(clone)) { found.Clones++; replacement = nameof(CloneNativeGeneratedCard); }
            else if (instruction.Calls(add)) { found.Adds++; replacement = nameof(AddNativeRawCard); }
            else if (instruction.Calls(upgrade)) { found.Upgrades++; replacement = nameof(UpgradeNativeGeneratedCard); }
            else if (instruction.Calls(dead)) { found.Dead++; replacement = nameof(NativeBranchIsDead); }
            if (replacement is not null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), replacement);
            }
            yield return instruction;
        }
        if (found != GeneratedReactionCalls[__originalMethod])
            throw new InvalidOperationException($"Native generation reaction changed: {__originalMethod} {found}.");
    }

    private static IEnumerable<CodeInstruction> RewriteEchoPileRead(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeBranchCardPile));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Native Echo pile query changed: {count}.");
    }

    private static bool NativeBranchIsDead(Creature creature)
        => _simulator is { } sim ? sim.State.GetCreature(creature).IsDead : creature.IsDead;

    private static CardPile? NativeBranchCardPile(CardModel card)
    {
        if (_simulator is null) return card.Pile;
        CardPile? pile = null;
        CardPileGetter(card, ref pile);
        return pile;
    }

    private static CardModel CloneNativeGeneratedCard(CardModel card)
    {
        if (_simulator is null) return card.CreateClone();
        var source = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native clone source is absent in the branch.");
        return source.CreateClone().MutablePreview;
    }

    private static Task<CardPileAddResult> AddNativeRawCard(CardModel card, PileType pile,
        CardPilePosition position, AbstractModel? source, bool silent)
    {
        if (_simulator is null) return CardPileCmd.Add(card, pile, position, source, silent);
        if (!card.IsMutable || _simulator.State.FindCard(card) is not null)
            throw new PredictionUnsupportedException("Native raw insertion requires a detached new clone.");
        var result = _simulator.AddToPile([PredictedCard.FromGenerated(card)], pile, position).Single();
        PauseNativeChoice(_simulator);
        return Task.FromResult(new CardPileAddResult { success = result.Success, cardAdded = card,
            oldPile = null, targetPile = pile, modifyingModels = [] });
    }

    private static void UpgradeNativeGeneratedCard(CardModel card, CardPreviewStyle style)
    {
        if (_simulator is null) { CardCmd.Upgrade(card, style); return; }
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native generated upgrade target is absent in the branch.");
        predicted.Upgrade();
    }
}
