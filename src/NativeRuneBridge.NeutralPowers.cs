using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly FieldInfo[] SlowFields = typeof(HextechTemporarySlowPower)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();

    private static void RegisterNeutralTemporaryPowers(Harmony harmony)
    {
        if (SlowFields.Length != 3 || SlowFields.Any(field => field.FieldType != typeof(bool) && field.FieldType != typeof(int)))
            throw new InvalidOperationException("Temporary Slow scalar state changed.");
        foreach (var field in SlowFields)
            PowerHiddenStateMirrors.Register<HextechTemporarySlowPower>(field.Name,
                (_, power) => Convert.ToInt64(field.GetValue(power)));
        harmony.Patch(AccessTools.Method(typeof(ContinuationStamp), "AppendPowers"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AppendNeutralPowerState)));
        foreach (var (name, count) in new[] { ("BeforeApplied", 1), ("AfterPowerAmountChanged", 2), ("BeforeSideTurnStart", 1) })
        {
            var callback = typeof(HextechTemporarySlowPower).GetMethods().Single(method => method.Name == name
                && method.DeclaringType == typeof(HextechTemporarySlowPower));
            var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? throw new InvalidOperationException("Temporary Slow native async shape changed.");
            var moveNext = AccessTools.Method(machine, "MoveNext");
            ExpectedPowerCalls.Add(moveNext, count);
            harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
            if (name != "BeforeApplied")
                harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNeutralPowerRemoval)));
        }
        BeforeSideTurnStartMirrors.Register<HextechTemporarySlowPower>((power, context) =>
            InvokeNativePower(power, context.Simulator, model => model.BeforeSideTurnStart(
                new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)));
        BeforeSideTurnStartMirrors.Register<HextechPlayerSlowPower>((_, _) => { });
        BeforeSideTurnEndMirrors.Registry.RegisterIgnored<HextechPlayerSlowPower>();
        BeforeSideTurnEndMirrors.Registry.RegisterIgnored<HextechTemporarySlowPower>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<HextechPlayerSlowPower>((power, context) =>
            power.ModifyDamageMultiplicativeCompat(context.Target, context.Amount, context.Props,
                context.Dealer, context.CardSource?.Preview));
        PatchPowerCallback<CorrosionRune>(harmony, nameof(CorrosionRune.AfterDamageGiven), 6, 1);
        RegisterNativeDamageHook<CorrosionRune>();
        RegisterState<FrostWraithRune>();
        RuneMirrors.RegisterNativeBase<FrostWraithRune>();
        var slowCallback = AccessTools.DeclaredMethod(typeof(FrostWraithRune), "ApplySlow");
        var slowMachine = slowCallback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("FrostWraith slow command shape changed.");
        var slowMoveNext = AccessTools.Method(slowMachine, "MoveNext");
        ExpectedPowerCalls.Add(slowMoveNext, 1);
        harmony.Patch(slowMoveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
        AfterPlayerTurnStartMirrors.Register<FrostWraithRune>((rune, context) =>
            RequireCompleted(Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(FrostWraithRune)));
    }

    internal static void ApplySlowFromCard(CombatPredictionSimulator simulator, Creature target, int amount, CardModel card)
    {
        var previous = _simulator;
        _simulator = simulator;
        try { ApplyPowerOne<HextechTemporarySlowPower>(target, amount, target, card, true).GetAwaiter().GetResult(); }
        finally { _simulator = previous; }
    }

    private static void ApplyNativeTemporarySlow(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        Creature target, int amount, Creature? applier, CardModel? source)
    {
        var incoming = (HextechTemporarySlowPower)ModelDb.Power<HextechTemporarySlowPower>().ToMutable();
        incoming._owner = target;
        incoming._applier = applier;
        incoming._target = target;
        bool fresh = false;
        combat.BeginCardPowerApplication(source);
        try
        {
            // Preserve the solver's amount modifiers, Artifact decision,
            // clamping, ordered listeners and source/history queue. Only native
            // BeforeApplied and scalar initialization are supplied here.
            combat.ApplyWithBeforeApplied<HextechTemporarySlowPower>(target, amount, applier,
                applied =>
                {
                    fresh = true;
                    InvokeNativePower(incoming, simulator, model => model.BeforeApplied(target, applied, applier, source));
                }, (_, power) =>
                {
                    if (fresh) foreach (var field in SlowFields) field.SetValue(power, field.GetValue(incoming));
                });
        }
        finally { combat.CompleteCardPowerApplication(source); }
    }

    private static void InvokeNativePower<T>(T power, CombatPredictionSimulator simulator, Func<T, Task> callback)
        where T : PowerModel
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        // BeforeApplied's incoming model is not in the branch yet; other
        // callbacks must mutate its detached branch power, never a root alias.
        var mutable = power is HextechTemporarySlowPower or HextechInfernalDragonSoulPower && power.Amount != 0
            ? (T)combat.GetMutablePowerInstance(power) : power;
        var previous = _simulator;
        _simulator = simulator;
        try { RequireCompleted(callback(mutable), typeof(T)); }
        finally { _simulator = previous; }
    }

    private static IEnumerable<CodeInstruction> RewriteNeutralPowerRemoval(IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(HextechPowerCmdCompat)
                && method.Name == "Remove" && method.GetParameters().Length == 1)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(RemoveNativePower));
                count++;
            }
            else if (instruction.operand is MethodInfo modify && modify.DeclaringType == typeof(PowerCmd)
                && modify.Name == "ModifyAmount" && modify.GetParameters().Length == 6)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ModifyNativePower));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Reviewed neutral power removal changed in {__originalMethod}: {count}.");
    }

    private static Task RemoveNativePower(PowerModel power)
    {
        if (_simulator is null) return HextechPowerCmdCompat.Remove(power);
        return RemoveNativePowerWithLifecycle(power);
    }

    private static Task<int> ModifyNativePower(PlayerChoiceContext context, PowerModel power, decimal amount,
        Creature? applier, CardModel? source, bool silent)
    {
        if (_simulator is null) return PowerCmd.ModifyAmount(context, power, amount, applier, source, silent);
        if (amount != decimal.Truncate(amount) || amount < int.MinValue || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Neutral power modification must be integral.");
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        int before = power.Amount;
        combat.SetPowerAmount(power, (int)Math.Clamp(before + amount, -999999999m, 999999999m));
        int delta = combat.GetPower<HextechTemporarySlowPower>(power.Owner)!.Amount - before;
        combat.BeginCardPowerApplication(source);
        try { combat.RecordPowerAmountChange(power, delta, applier); }
        finally { combat.CompleteCardPowerApplication(source); }
        PowerLifecycleSupport.ResolvePowerAmountChanges(_simulator, combat);
        return Task.FromResult(delta);
    }

    private static void AppendNeutralPowerState(StringBuilder text, IEnumerable<PowerModel> powers)
    {
        foreach (var power in powers.Where(power => power.Amount != 0))
        {
            if (power is HextechInfernalDragonSoulPower)
            {
                text.Append(";hextech_infernal_power_state=").Append(power.Owner.CombatId)
                    .Append(':').Append((bool)InfernalTriggered.GetValue(power)! ? '1' : '0');
                continue;
            }
            if (power is not HextechTemporarySlowPower) continue;
            text.Append(";hextech_neutral_power_state=").Append(power.Owner.CombatId);
            foreach (var field in SlowFields) text.Append(':').Append(field.Name).Append('=').Append(field.GetValue(power));
        }
    }
}
