using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeEffectiveAttack(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(IllusoryWeaponRune), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(NativeAttackExecute, nameof(ExecuteNativeAttack)),
            Site(AccessTools.DeclaredMethod(typeof(HextechPlayerRuneHooks), "ClearIllusoryWeaponPendingPenNib"), nameof(NativeClearIllusoryPenNib)));
        RegisterStableState<IllusoryWeaponRune>(); RuneMirrors.RegisterNativeBase<IllusoryWeaponRune>();
        RegisterAfterCardPlayedCallback<IllusoryWeaponRune>();
        foreach (var (type, patch, callback) in new[] {
            (typeof(Nunchaku), "NunchakuPatch", "AfterCardPlayed"),
            (typeof(Kunai), "KunaiPatch", "AfterCardPlayed"),
            (typeof(Shuriken), "ShurikenPatch", "AfterCardPlayed"),
            (typeof(OrnamentalFan), "OrnamentalFanPatch", "AfterCardPlayed"),
            (typeof(PenNib), "PenNibBeforeCardPlayedPatch", "BeforeCardPlayed"),
            (typeof(PenNib), "PenNibAfterCardPlayedPatch", "AfterCardPlayed") })
            NativeCallbackContracts.AddNativePrefix(AccessTools.DeclaredMethod(type, callback),
                AccessTools.DeclaredMethod(AccessTools.Inner(typeof(IllusoryWeaponRune), patch), "Prefix"), "Natsuki.HextechRunes", Priority.Low);
        NativeCallbackContracts.AddNativePostfix(AccessTools.PropertyGetter(typeof(Finisher), "CanonicalVars"),
            AccessTools.DeclaredMethod(AccessTools.Inner(typeof(IllusoryWeaponRune), "FinisherCanonicalVarsPatch"), "Postfix"),
            "Natsuki.HextechRunes", Priority.Normal);
        RegisterEffectiveAttackRewrite(harmony, AccessTools.DeclaredMethod(typeof(AfterCardPlayedMirrors), "IncrementCounter"));
        RegisterEffectiveAttackRewrite(harmony, AccessTools.DeclaredMethod(typeof(BeforeCardPlayedMirrors), "HandlePenNib"));
        RegisterEffectiveAttackRewrite(harmony, AccessTools.DeclaredMethod(typeof(CalculatedVarSpecRegistry), "TryMultiplier"));
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(AfterCardPlayedMirrors), "HandlePenNib"),
            nameof(NativePreserveIllusoryPenNib));
    }

    private static void RegisterEffectiveAttackRewrite(Harmony harmony, MethodInfo target)
    {
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEffectiveAttack));
        harmony.Patch(target, transpiler: new HarmonyMethod(rewrite)); NativeCallbackContracts.Add(target, rewrite);
    }

    private static IEnumerable<CodeInstruction> RewriteEffectiveAttack(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var type = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Type));
        var attacks = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.GetAttacksPlayedThisTurn));
        int count = 0;
        foreach (var instruction in instructions)
        {
            bool counter = __originalMethod.Name == "IncrementCounter" && instruction.Calls(type);
            bool pen = __originalMethod.Name == "HandlePenNib" && instruction.Calls(type);
            bool finisher = __originalMethod.Name == "TryMultiplier" && instruction.Calls(attacks);
            if (counter || pen || finisher)
            {
                var first = new CodeInstruction(counter || finisher ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                first.labels.AddRange(instruction.labels); instruction.labels.Clear();
                first.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return first;
                if (counter)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Ldarg_3);
                }
                if (finisher) yield return new CodeInstruction(OpCodes.Ldarg_1);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), counter ? nameof(NativeCounterCardType)
                    : pen ? nameof(NativePenNibCardType) : nameof(NativeFinisherAttacks)); count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed effective attack site changed: {__originalMethod} count={count}.");
    }

    private static CardType NativeCounterCardType(CardModel card, RelicModel relic, CardType requested, AfterCardPlayedMirrorContext context)
        => requested == CardType.Attack && relic is Nunchaku or Kunai or Shuriken or OrnamentalFan
            ? NativeEffectiveAttackType(card, context.Simulator) : card.Type;
    private static CardType NativePenNibCardType(CardModel card, BeforeCardPlayedMirrorContext context)
        => NativeEffectiveAttackType(card, context.Simulator);
    private static CardType NativeEffectiveAttackType(CardModel card, CombatPredictionSimulator simulator)
    {
        var rune = ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(card.Owner).OfType<IllusoryWeaponRune>().FirstOrDefault();
        return rune is not null && Invoke(rune, simulator, _ => HextechCardEffectTypes.IsAttackForEffects(card, card.Owner))
            ? CardType.Attack : card.Type;
    }

    internal static bool NativeAttackForEffects(CardModel card, CombatPredictionSimulator simulator)
        => NativeEffectiveAttackType(card, simulator) == CardType.Attack;

    private static int NativeFinisherAttacks(SimulatedCombatState combat, Creature owner,
        CombatPredictionSimulator simulator, PredictedCard card)
    {
        int count = combat.GetAttacksPlayedThisTurn(owner);
        if (card.Preview is Finisher && owner.Player is { } player && combat.RelicsOf(player).OfType<IllusoryWeaponRune>().Any())
            count += combat.GetSkillCardsPlayedThisTurn(owner);
        return count;
    }

    private static bool NativePreserveIllusoryPenNib(PenNib relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Type == CardType.Attack || NativeEffectiveAttackType(context.PreviewCard, context.Simulator) != CardType.Attack)
            return true;
        var state = context.StateStore.Get(relic, () => new PenNibPredictionState(relic));
        // The original after prefix deliberately preserves the armed skill
        // until the rune's own attack has resolved, regardless of relic order.
        return state.AttackToDouble != context.Card.Original;
    }

    private static void NativeClearIllusoryPenNib(Player? player, CardModel card)
    {
        if (_simulator is null) { HextechPlayerRuneHooks.ClearIllusoryWeaponPendingPenNib(player, card); return; }
        if (player is null || NativeResourceRelic<PenNib>(player) is not { } pen) return;
        var state = _simulator.StateStore.Get(pen, () => new PenNibPredictionState(pen));
        if (state.AttackToDouble is { } armed && _simulator.State.FindCard(card) is { } predicted && predicted.References(armed))
            state.AttackToDouble = null;
    }
}
