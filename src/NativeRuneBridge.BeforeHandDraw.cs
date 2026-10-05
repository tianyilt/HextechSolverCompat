using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
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
    private static readonly System.Reflection.FieldInfo NativeRootHistory =
        AccessTools.DeclaredField(typeof(SimulatedCombatState), "_rootHistory");

    private static void RegisterNativeBeforeHandDraw(Harmony harmony)
    {
        // Keep the original pool filtering, stable salts, keyword/cost changes
        // and per-relic order. Only command/read boundaries are redirected.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ViolenceRune), "BeforeHandDraw"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
                [typeof(CardModel), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)]), nameof(MoveNativeCard)));
        RegisterBeforeHandDrawRune<ViolenceRune>();
        RegisterGeneratedBeforeHandDrawRune<SingularityAIRune>(harmony);
        RegisterGeneratedBeforeHandDrawRune<MindOverMatterRune>(harmony);
        RegisterGeneratedBeforeHandDrawRune<SnakebiteRune>(harmony);

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechRelicBase), "CountOwnedCardsDrawnFromHistory"),
            Site(AccessTools.Method(typeof(HextechCombatHistoryHelper), "CountOwnedCardsDrawn"), nameof(NativeBranchOwnedDraws)));
        var getRelic = typeof(Player).GetMethods().Single(method => method.Name == nameof(Player.GetRelic) && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechCardEffectTypes), "ShouldTreatSkillAsAttack"),
            new NativeCallSite(getRelic.MakeGenericMethod(typeof(IllusoryWeaponRune)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeResourceRelic)).MakeGenericMethod(typeof(IllusoryWeaponRune)), 1));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCardEffectTypes), "IsAttackForEffects"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCardEffectTypes), "IsOriginalOwnedSkill"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCardEffectTypes), "IsSkillForEffects"));

        var target = AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "ContinueRelicsBeforeHandDraw");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteBeforeHandDrawDispatch));
        harmony.Patch(target, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(target, rewrite);
    }

    private static void RegisterBeforeHandDrawRune<T>() where T : HextechRelicBase
    {
        RegisterStableState<T>();
        RuneMirrors.RegisterNativeBase<T>();
    }

    private static void RegisterGeneratedBeforeHandDrawRune<T>(Harmony harmony) where T : HextechRelicBase
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(T), "BeforeHandDraw"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        RegisterBeforeHandDrawRune<T>();
    }

    private static int NativeBranchOwnedDraws(Player? player)
    {
        if (_simulator is null) return HextechCombatHistoryHelper.CountOwnedCardsDrawn(player);
        if (player is null) return 0;
        var root = (RootCombatHistorySnapshot)NativeRootHistory.GetValue(_simulator.State.CombatState)!;
        // Native counts card owners, not the draw actor. Use the frozen root
        // entries and immutable branch history snapshots with that same rule.
        return root.CardsDrawn.Count(entry => entry.Card.Owner?.NetId == player.NetId)
            + _simulator.History.OfType<CombatPredictionCardDrawnEntry>()
                .Count(entry => entry.Card.Owner.NetId == player.NetId);
    }

    private static void DispatchNativeBeforeHandDraw(RelicModel relic, CombatPredictionSimulator simulator,
        Player player, SimulatedCombatState combat)
    {
        if (relic is not (ViolenceRune or SingularityAIRune or MindOverMatterRune or SnakebiteRune
            or WraithRune or ExplosionArtRune or SummonForthRune or SendThemInRune or SubroutineUpgradeRune or ColorDiscoveryRune
            or SoulCallingRune or NeowsGrudgeRune)) return;
        var rune = (HextechRelicBase)relic;
        RequireCompleted(Invoke(rune, simulator, model => model.BeforeHandDraw(player,
            new ThrowingPlayerChoiceContext(), combat)), rune.GetType());
    }

    private static IEnumerable<CodeInstruction> RewriteBeforeHandDrawDispatch(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var pending = AccessTools.PropertyGetter(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.HasPendingChoice));
        var melted = AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.IsMelted));
        int gates = 0;
        if (code.Count(i => i.Calls(melted)) != 1 || code.Count(i => i.Calls(pending)) != 1
            || !code.Any(i => i.opcode == OpCodes.Stloc_1))
            throw new InvalidOperationException("Pinned ordered BeforeHandDraw relic loop changed.");
        for (int index = 0; index < code.Count; index++)
        {
            var instruction = code[index];
            if (index + 1 < code.Count && instruction.opcode == OpCodes.Ldarg_1 && code[index + 1].Calls(pending))
            {
                // Every stock switch branch joins here. Moving its labels to
                // the insertion preserves native listener order and resumes
                // Toolbox at the next relic before calling these callbacks.
                var first = new CodeInstruction(OpCodes.Ldloc_1);
                first.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(DispatchNativeBeforeHandDraw));
                gates++;
            }
            yield return instruction;
        }
        if (gates != 1) throw new InvalidOperationException("Pinned BeforeHandDraw pending-choice join changed.");
    }
}
