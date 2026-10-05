using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeHeal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
        [typeof(Creature), typeof(decimal), typeof(bool)]);

    private static void RegisterDamageRunes(Harmony harmony)
    {
        var blood = AccessTools.DeclaredMethod(typeof(BloodPactRune), nameof(BloodPactRune.AfterDamageReceived));
        ExpectedPowerCalls.Add(blood, 1);
        harmony.Patch(blood, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
        RegisterState<BloodPactRune>();
        RuneMirrors.RegisterNativeBase<BloodPactRune>();
        AfterDamageReceivedMirrors.Registry.Register<BloodPactRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterDamageReceived(
                new ThrowingPlayerChoiceContext(), context.Target, context.Result, context.Props,
                context.Dealer, context.Source?.Preview)), typeof(BloodPactRune)));
        PatchPowerCallback<ShrinkRayRune>(harmony, nameof(ShrinkRayRune.AfterDamageGiven), 6, 1);
        RegisterNativeDamageHook<ShrinkRayRune>();
        var method = AccessTools.Method(typeof(DeathHarvestRune), nameof(DeathHarvestRune.AfterDamageGiven));
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("DeathHarvest callback shape changed.");
        harmony.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteHeal)));
        RegisterNativeDamageHook<DeathHarvestRune>();
        RegisterFlyingKick(harmony);
        RegisterHpChangeFamily(harmony);
    }

    private static void RegisterHpChangeFamily(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CombatManager), nameof(CombatManager.IsPartOfPlayerTurn)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(NativePlayerTurnQuery)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(PlateletRune), nameof(PlateletRune.AfterCurrentHpChanged)),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteSideStartBlock)));
        var bloodArmor = AccessTools.DeclaredMethod(typeof(BloodArmorRune), nameof(BloodArmorRune.AfterCurrentHpChanged));
        ExpectedPowerCalls.Add(bloodArmor, 1);
        harmony.Patch(bloodArmor, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
        RegisterNativeHpChange<PlateletRune>();
        RegisterNativeHpChange<BloodArmorRune>();
    }

    private static void RegisterNativeHpChange<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterCurrentHpChangedMirrors.Registry.Register<T>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator,
                model => model.AfterCurrentHpChanged(context.Creature, context.Delta)), typeof(T)));
    }

    private static bool NativePlayerTurnQuery(Player player, ref bool __result)
    {
        if (_simulator is null) return true;
        // Native query checks current side and extra-turn participants. Roots
        // are explicitly single-player; that player is the sole participant.
        if (_simulator.State.CombatState.Players.Count != 1)
            throw new PredictionUnsupportedException("Native player-turn query requires the reviewed single-player scope.");
        __result = _simulator.State.CombatState.CurrentSide == CombatSide.Player
            && _simulator.State.CombatState.Players.Contains(player);
        return false;
    }

    private static void RegisterNativeDamageHook<T>(bool resetBeforeTurn = false) where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>(resetBeforeTurn ? (rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeSideTurnStart(
                new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(T)) : null);
        AfterDamageGivenMirrors.Registry.Register<T>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterDamageGiven(
                new ThrowingPlayerChoiceContext(), context.Dealer, context.Result, context.Props,
                context.Target, context.Source?.Preview)), typeof(T)));
    }

    private static IEnumerable<CodeInstruction> RewriteHeal(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeHeal))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(HealNative));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed native heal commands changed: {count}.");
    }

    private static Task HealNative(Creature creature, decimal amount, bool playVfx)
    {
        if (_simulator is null) return CreatureCmd.Heal(creature, amount, playVfx);
        _simulator.Heal(creature, amount);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }
}
