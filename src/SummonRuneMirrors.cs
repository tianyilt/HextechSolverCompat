using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using System.Reflection.Emit;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static class SummonRuneMirrors
{
    internal static void Register(Harmony harmony)
    {
        RuneMirrors.RegisterNativeBase<BoneGuardRune>();
        RuneMirrors.RegisterNativeBase<PlasterRune>();
        harmony.Patch(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.SummonOsty)),
            prefix: new HarmonyMethod(typeof(SummonRuneMirrors), nameof(BeforeSummon)),
            postfix: new HarmonyMethod(typeof(SummonRuneMirrors), nameof(AfterSummon)),
            transpiler: new HarmonyMethod(typeof(SummonRuneMirrors), nameof(RewriteSummonRoster)));
        var target = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.SummonOsty));
        NativeCallbackContracts.AddNativePrefix(target, AccessTools.Method(typeof(SummonRuneMirrors), nameof(BeforeSummon)),
            "HextechSolverCompat", Priority.Normal, AccessTools.Method(typeof(SummonRuneMirrors), nameof(RewriteSummonRoster)));
        NativeCallbackContracts.AddNativePostfix(target, AccessTools.Method(typeof(SummonRuneMirrors), nameof(AfterSummon)),
            "HextechSolverCompat", Priority.Normal, AccessTools.Method(typeof(SummonRuneMirrors), nameof(RewriteSummonRoster)));
    }

    private static IEnumerable<CodeInstruction> RewriteSummonRoster(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.GetOsty));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SummonRuneMirrors), nameof(GetSummonOsty));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException($"Reviewed Osty summon roster lookup changed: {replaced}.");
    }

    private static Creature? GetSummonOsty(SimulatedCombatState combat, Player player)
        // The native command searches current allies, not the stale Player.Osty
        // reference. A removed pet needs a new creature and combat ID; a dead pet
        // still in the roster is revived in place. The branch roster owns both cases.
        => combat.Allies.FirstOrDefault(creature => creature.Monster is MegaCrit.Sts2.Core.Models.Monsters.Osty
            && creature.PetOwner == player);

    private static void BeforeSummon(SimulatedCombatState __instance, CombatPredictionSimulator simulator, Player player,
        ref int amount, out decimal __state)
    {
        __state = NativeRuneBridge.ModifyCapturedSummon(__instance, simulator, player, amount);
        if (__state < 0m || __state > int.MaxValue)
            throw new CombatSolver.Engine.Common.PredictionUnsupportedException("Native summon amount is outside its captured integer HP range.");
        // Creature.SetMaxHpInternal truncates only the HP write. The original
        // decimal still belongs to AfterSummon and its individual modifiers.
        amount = (int)__state;
    }

    private static void AfterSummon(SimulatedCombatState __instance, CombatPredictionSimulator simulator, Player player, decimal __state)
    {
        decimal amount = __state;
        if (amount <= 0 || !simulator.State.GetCreature(player.Creature).IsAlive) return;
        // The stock summon has finished creating/reviving/growing Osty. Preserve
        // native listener order for the two reviewed AfterSummon callbacks.
        foreach (var listener in __instance.IterateHookListeners().ToArray())
        {
            switch (listener)
            {
                case BoneGuardRune or PlasterRune or DrainRune:
                    NativeRuneBridge.InvokeCapturedSummon((HextechRelicBase)listener, simulator, player, amount);
                    break;
            }
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, __instance);
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); return; }
        }
    }
}
