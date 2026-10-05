using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Dictionary<MethodBase, (int Queries, int Direct, int Commands)> OrbPassiveContracts = [];

    private static void RegisterNativeOrbPassiveFamily(Harmony harmony)
    {
        RegisterState<ZapUpgradeRune>();
        RegisterState<LoopUpgradeRune>();
        RuneMirrors.RegisterNativeBase<ZapUpgradeRune>();
        RuneMirrors.RegisterNativeBase<LoopUpgradeRune>();
        RegisterAfterCardPlayedCallback<ZapUpgradeRune>();
        PatchOrbPassiveCallback(harmony, AccessTools.DeclaredMethod(typeof(ZapUpgradeRune), "AfterCardPlayed"), (1, 1, 0));
        PatchOrbPassiveCallback(harmony, AccessTools.DeclaredMethod(typeof(LoopUpgradeRune), "TriggerAll"), (2, 0, 1));
        PatchGetter(harmony, typeof(CombatManager), nameof(CombatManager.IsOverOrEnding), nameof(NativeOrbEndingGetter));
        PowerExpiryBridge.AddContract(AccessTools.DeclaredMethod(typeof(LoopPower), "AfterPlayerTurnStart"),
            AccessTools.Method(AccessTools.Inner(typeof(LoopUpgradeRune), "LoopAllOrbsPatch"), "Prefix"));
        harmony.Patch(AccessTools.Method(typeof(TurnStartPowerSupport), "ApplyAfterPlayerTurnStartPower"),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(ApplyNativeAllOrbLoop)));
    }

    private static void PatchOrbPassiveCallback(Harmony harmony, MethodInfo callback,
        (int Queries, int Direct, int Commands) contract)
    {
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("Native orb passive callback shape changed.");
        var target = AccessTools.Method(machine, "MoveNext");
        OrbPassiveContracts.Add(target, contract);
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNativeOrbPassives)));
    }

    private static IEnumerable<CodeInstruction> RewriteNativeOrbPassives(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        (int Queries, int Direct, int Commands) found = (0, 0, 0);
        var query = AccessTools.PropertyGetter(typeof(OrbQueue), nameof(OrbQueue.Orbs));
        var direct = AccessTools.Method(typeof(OrbModel), nameof(OrbModel.Passive), [typeof(PlayerChoiceContext), typeof(Creature)]);
        var command = AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Passive),
            [typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Creature), typeof(bool)]);
        foreach (var instruction in instructions)
        {
            string? replacement = null;
            if (instruction.Calls(query)) { found.Queries++; replacement = nameof(NativeOrbContents); }
            else if (instruction.Calls(direct)) { found.Direct++; replacement = nameof(NativeDirectOrbPassive); }
            else if (instruction.Calls(command)) { found.Commands++; replacement = nameof(NativeCommandOrbPassive); }
            if (replacement is not null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), replacement);
            }
            yield return instruction;
        }
        if (found != OrbPassiveContracts[__originalMethod])
            throw new InvalidOperationException($"Native orb passive contract changed: {__originalMethod} {found}.");
    }

    private static IReadOnlyList<OrbModel> NativeOrbContents(OrbQueue queue)
    {
        if (_simulator is null) return queue.Orbs;
        var owner = _simulator.State.CombatState.Players.SingleOrDefault(player => ReferenceEquals(player.PlayerCombatState?.OrbQueue, queue))
            ?? throw new PredictionUnsupportedException("Native orb queue is absent from the captured players.");
        return _simulator.State.GetPlayerCombatState(owner).OrbQueue.Orbs;
    }

    private static Task NativeDirectOrbPassive(OrbModel orb, PlayerChoiceContext context, Creature? target)
    {
        if (_simulator is null) return orb.Passive(context, target);
        // A pending inner death choice invalidates continuation optimization;
        // replay from the parent with the explicit choice plan replays the outer
        // native loop, including all remaining captured orbs.
        if (!_simulator.HasPendingChoice) _simulator.OrbPassive(orb, target);
        if (_simulator.HasPendingChoice) _simulator.RejectExecutionContinuation();
        return Task.CompletedTask;
    }

    private static Task NativeCommandOrbPassive(PlayerChoiceContext context, OrbModel orb, Creature? target, bool flag)
        => _simulator is null ? OrbCmd.Passive(context, orb, target, flag) : NativeDirectOrbPassive(orb, context, target);

    private static bool NativeOrbEndingGetter(ref bool __result)
    {
        if (_simulator is null) return true;
        __result = _simulator.IsOverOrEnding || _simulator.HasPendingChoice;
        return false;
    }

    private static bool ApplyNativeAllOrbLoop(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        Player player, PowerModel power, ref bool __result)
    {
        if (power is not LoopPower loop) return true;
        var rune = combat.RelicsOf(player).OfType<LoopUpgradeRune>().SingleOrDefault();
        if (rune is null) return true;
        if (power.Amount > 0 && ReferenceEquals(power.Owner.Player, player))
            RequireCompleted(Invoke(rune, simulator, _ => LoopUpgradeRune.TriggerAll(loop, new ThrowingPlayerChoiceContext(), player)), typeof(LoopUpgradeRune));
        __result = true;
        return false;
    }
}
