using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeSimpleHooks(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MentalShieldRune), "BeforeTurnEnd"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        RegisterState<MentalShieldRune>();
        RegisterNativeEndTurn<MentalShieldRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PactsEndUpgradeRune), "ModifyDamageAdditiveCompat"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PactsEndUpgradeRune), "CalculateBonusDamage"));
        RegisterNativeQueries<PactsEndUpgradeRune>(NativeQueries.DamageAdditive);
        var getter = AccessTools.PropertyGetter(typeof(PactsEnd), "CanDealDamage");
        var native = AccessTools.Method(AccessTools.Inner(typeof(PactsEndUpgradeRune), "PactsEndPatch"), "Postfix");
        NativeCallbackContracts.AddNativePostfix(getter, native, "Natsuki.HextechRunes", Priority.Normal);
        var play = AccessTools.Method(typeof(BespokeCardMirrors), nameof(BespokeCardMirrors.PactsEndOnPlay));
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewritePactsEndGate));
        harmony.Patch(play, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(play, rewrite);
    }

    private static bool SkipNativePactsEnd(bool belowStockThreshold, PactsEnd card, CardOnPlayMirrorContext context)
        => belowStockThreshold && !((SimulatedCombatState)context.CombatState).RelicsOf(card.Owner)
            .OfType<PactsEndUpgradeRune>().Any();

    private static IEnumerable<CodeInstruction> RewritePactsEndGate(IEnumerable<CodeInstruction> instructions)
    {
        int gates = 0, attacks = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is System.Reflection.MethodInfo method && method.Name == "AttackAllOpponents") attacks++;
            if (instruction.opcode == OpCodes.Blt || instruction.opcode == OpCodes.Blt_S)
            {
                // The native CanDealDamage postfix turns a false threshold into
                // true for this exact held rune. Use captured branch membership
                // at the SDK's corresponding gate; retain its ordinary attack.
                var target = instruction.operand;
                instruction.opcode = OpCodes.Clt;
                instruction.operand = null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(SkipNativePactsEnd));
                yield return new CodeInstruction(OpCodes.Brtrue, target);
                gates++;
            }
            else yield return instruction;
        }
        if (gates != 1 || attacks != 1) throw new InvalidOperationException("Pinned PactsEnd threshold/attack shape changed.");
    }
}
