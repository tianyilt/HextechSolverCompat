using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterAfterCardCommandFamily(Harmony harmony)
    {
        PatchSingleGenerator<MoltenFistUpgradeRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed));
        RegisterAfterCardPlayed<MoltenFistUpgradeRune>();
        PatchAfterCardCommands<VenerateUpgradeRune>(harmony, nameof(RewriteStarCommands));
        RegisterAfterCardPlayed<VenerateUpgradeRune>();
        PatchAfterCardCommands<SkyDrillUpgradeRune>(harmony, nameof(RewriteNativeAttack));
        RegisterAfterCardPlayed<SkyDrillUpgradeRune>();
    }

    private static void PatchAfterCardCommands<T>(Harmony harmony, string transpiler) where T : HextechRelicBase
    {
        var callback = AccessTools.DeclaredMethod(typeof(T), nameof(HextechRelicBase.AfterCardPlayed));
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeof(T).Name} native after-card shape changed.");
        harmony.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), transpiler));
    }

    private static IEnumerable<CodeInstruction> RewriteStarCommands(IEnumerable<CodeInstruction> instructions)
    {
        int reads = 0, gains = 0;
        var query = AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.Stars));
        var command = AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainStars), [typeof(decimal), typeof(Player)]);
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(query))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeBranchStars));
                reads++;
            }
            else if (instruction.Calls(command))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GainNativeStars));
                gains++;
            }
            yield return instruction;
        }
        if (reads != 1 || gains != 1)
            throw new InvalidOperationException($"Reviewed native star callback changed: reads={reads}, gains={gains}.");
    }

    private static int NativeBranchStars(PlayerCombatState live)
    {
        if (_simulator is null) return live.Stars;
        var player = _simulator.State.CombatState.Players.SingleOrDefault(player => ReferenceEquals(player.PlayerCombatState, live))
            ?? throw new PredictionUnsupportedException("Native star query owner is absent in the captured branch.");
        return _simulator.State.GetPlayerCombatState(player).Stars;
    }

    private static Task GainNativeStars(decimal amount, Player player)
    {
        if (_simulator is null) return PlayerCmd.GainStars(amount, player);
        if (amount != decimal.Truncate(amount) || amount < int.MinValue || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Native stars command requires integral amounts.");
        _simulator.GainStars(player, amount);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }
}
