using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeResourceUpgrades(Harmony harmony)
    {
        RegisterState<StardustUpgradeRune>(); RuneMirrors.RegisterNativeBase<StardustUpgradeRune>();
        var payment = AccessTools.DeclaredMethod(typeof(CombatPredictionSimulator), "SpendResources");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeStardustPayment));
        harmony.Patch(payment, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(payment, rewrite);

        var helper = AccessTools.DeclaredMethod(typeof(HextechPlayerRuneHooks), "JuggernautUpgradeAfterBlockGained");
        PatchEventCallback(harmony, helper,
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.CombatState)), nameof(NativeUpgradePowerCombat)),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount)), nameof(NativeJuggernautAmount)),
            NativeRelicSite<JuggernautUpgradeRune>(nameof(NativeResourceRelic)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature)]), nameof(NativeInfernoDamage)));
        RegisterState<JuggernautUpgradeRune>(); RuneMirrors.RegisterNativeBase<JuggernautUpgradeRune>();
        var nativePrefix = AccessTools.DeclaredMethod(AccessTools.Inner(typeof(JuggernautUpgradeRune), "JuggernautPatch"), "Prefix");
        NativeCallbackContracts.AddNativePrefix(AccessTools.DeclaredMethod(typeof(JuggernautPower), "AfterBlockGained"),
            nativePrefix, "Natsuki.HextechRunes", Priority.Low);
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(AfterBlockGainedMirrors), "HandleJuggernautPower"),
            nameof(NativeJuggernautResponse));
    }

    private static IEnumerable<CodeInstruction> RewriteNativeStardustPayment(IEnumerable<CodeInstruction> instructions)
    {
        var lose = AccessTools.Method(typeof(SimPlayerCombatState), nameof(SimPlayerCombatState.LoseStars), [typeof(decimal)]);
        var spent = AccessTools.Method(typeof(ICombatPredictionCardEventSink), nameof(ICombatPredictionCardEventSink.AfterStarsSpent));
        int losses = 0, callbacks = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(lose))
            {
                // Preserve the ordinary payment order and resource reporting;
                // suppress the loss itself, never compensate with a star gain.
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeUpgradeLoseStars)); losses++;
            }
            else if (instruction.Calls(spent))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeUpgradeStarsSpent)); callbacks++;
            }
            yield return instruction;
        }
        if (losses != 1 || callbacks != 1)
            throw new InvalidOperationException($"Reviewed Stardust payment sites changed: loss={losses}, callbacks={callbacks}.");
    }

    private static bool PreserveNativeStardust(CombatPredictionSimulator simulator, PredictedCard card)
    {
        var rune = ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(card.Preview.Owner)
            .OfType<StardustUpgradeRune>().FirstOrDefault();
        return rune is not null && Invoke(rune, simulator, _ => StardustUpgradeRune.ShouldPreserveStars(card.MutablePreview));
    }

    private static void NativeUpgradeLoseStars(SimPlayerCombatState state, decimal amount,
        CombatPredictionSimulator simulator, PredictedCard card)
    { if (!PreserveNativeStardust(simulator, card)) state.LoseStars(amount); }

    private static void NativeUpgradeStarsSpent(ICombatPredictionCardEventSink sink,
        CombatPredictionSimulator simulator, PredictedCard card, int amount)
    { if (!PreserveNativeStardust(simulator, card)) sink.AfterStarsSpent(simulator, card, amount); }

    private static ICombatState NativeUpgradePowerCombat(PowerModel power)
        => _simulator?.State.CombatState ?? power.CombatState;
    private static int NativeJuggernautAmount(PowerModel power)
        => _simulator is null ? power.Amount
            : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<JuggernautPower>(power.Owner);

    private static bool NativeJuggernautResponse(JuggernautPower power, AfterBlockGainedMirrorContext context)
    {
        if (power.Owner.Player is not { } player || !((SimulatedCombatState)context.CombatState)
            .RelicsOf(player).OfType<JuggernautUpgradeRune>().Any()) return true;
        InvokeNativePower(power, context.Simulator,
            model => HextechPlayerRuneHooks.JuggernautUpgradeAfterBlockGained(model, context.Creature, context.Amount));
        return false;
    }
}
