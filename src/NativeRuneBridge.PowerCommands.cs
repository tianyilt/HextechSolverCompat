using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Dictionary<MethodBase, int> ExpectedPowerCalls = new();

    private static void RegisterPowerTurnRunes(Harmony harmony)
    {
        RegisterPowerTurnRune<QueenRune>(harmony, 3);
        RegisterPowerTurnRune<HandOfBaronRune>(harmony, 1);
        RegisterPowerTurnRune<ProtectiveVeilRune>(harmony, 1);
        RegisterPlayerPowerTurnRune<MiseryRune>(harmony, 4);
        RegisterPlayerPowerTurnRune<DoomsdayRune>(harmony, 1);
        PatchPowerCallback<KillerHunterRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed), 2, 2);
        RegisterAfterCardPlayed<KillerHunterRune>();
        TemporaryDexterityBridge.Register(harmony);
        RegisterNativePowerGivenQueries(harmony);
        ModifyDamageMirrors.MultiplicativeRegistry.Register<HandOfBaronRune>((relic, context) =>
            Invoke(relic, context.Simulator, model => model.ModifyDamageMultiplicativeCompat(
                context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview)));
    }

    private static void RegisterPlayerPowerTurnRune<T>(Harmony harmony, int expectedCalls) where T : HextechRelicBase
    {
        PatchPowerCallback<T>(harmony, nameof(HextechRelicBase.AfterPlayerTurnStart), 2, expectedCalls);
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterPlayerTurnStartMirrors.Register<T>((relic, context) =>
        {
            var task = Invoke(relic, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player));
            RequireCompleted(task, typeof(T));
        });
    }

    private static void RequireCompleted(Task task, Type rune)
    {
        if (!task.IsCompleted)
            throw new PredictionUnsupportedException($"{rune.Name} native callback unexpectedly suspended.");
        try { task.GetAwaiter().GetResult(); }
        catch (NativeCallbackChoicePause pause) { AcceptNativeChoicePause(pause); }
    }

    private static void RequireCompleted(Func<Task> callback, Type rune)
    {
        try { RequireCompleted(callback(), rune); }
        catch (NativeCallbackChoicePause pause) { AcceptNativeChoicePause(pause); }
        catch (TargetInvocationException exception) when (exception.InnerException is NativeCallbackChoicePause)
        { AcceptNativeChoicePause((NativeCallbackChoicePause)exception.InnerException); }
    }

    private static void PatchPowerCallback<T>(Harmony harmony, string methodName, int parameterCount, int expectedCalls) where T : HextechRelicBase
    {
        var callback = typeof(T).GetMethods().Single(m => m.Name == methodName
            && m.GetParameters().Length == parameterCount && m.DeclaringType == typeof(T));
        var stateMachine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeof(T).Name} async callback shape changed.");
        var moveNext = AccessTools.Method(stateMachine, "MoveNext");
        ExpectedPowerCalls.Add(moveNext, expectedCalls);
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
    }

    private static void RegisterPowerTurnRune<T>(Harmony harmony, int expectedCalls) where T : HextechRelicBase
    {
        // The reviewed methods use only these command overloads and ignore their
        // returned power objects. Patch the generated state machine, since the
        // public async method itself contains no gameplay command instructions.
        PatchPowerCallback<T>(harmony, nameof(HextechRelicBase.BeforeSideTurnStart), 3, expectedCalls);
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>((relic, context) =>
        {
            var task = Invoke(relic, context.Simulator, model => model.BeforeSideTurnStart(
                new ThrowingPlayerChoiceContext(), context.Side, context.CombatState));
            RequireCompleted(task, typeof(T));
        });
    }

    private static IEnumerable<CodeInstruction> RewritePowerCommands(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(HextechPowerCmdCompat)
                && method.Name == nameof(HextechPowerCmdCompat.Apply) && method.IsGenericMethod)
            {
                var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
                if (parameters.Length != 5 || parameters[1] != typeof(decimal) || parameters[2] != typeof(Creature)
                    || parameters[3] != typeof(CardModel) || parameters[4] != typeof(bool))
                    throw new InvalidOperationException($"Unreviewed Hextech power command: {method}.");
                string name = parameters[0] == typeof(Creature) ? nameof(ApplyPowerOne)
                    : parameters[0] == typeof(IEnumerable<Creature>) ? nameof(ApplyPowerMany)
                    : throw new InvalidOperationException($"Unreviewed Hextech power targets: {parameters[0]}.");
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), name).MakeGenericMethod(method.GetGenericArguments());
                count++;
            }
            yield return instruction;
        }
        if (count != ExpectedPowerCalls[__originalMethod])
            throw new InvalidOperationException($"Power commands changed in {__originalMethod}: found {count}, expected {ExpectedPowerCalls[__originalMethod]}.");
    }

    private static Task<T?> ApplyPowerOne<T>(Creature target, decimal amount, Creature? applier, CardModel? cardSource, bool silent) where T : PowerModel
    {
        if (_simulator is null) return HextechPowerCmdCompat.Apply<T>(target, amount, applier, cardSource, silent);
        if (amount != decimal.Truncate(amount) || amount < int.MinValue || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Native power bridge requires the reviewed integral power amounts.");
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        if (typeof(T) == typeof(HextechTemporarySlowPower))
            ApplyNativeTemporarySlow(_simulator, combat, target, (int)amount, applier, cardSource);
        else if (typeof(T) == typeof(HextechPowerShieldTemporaryStrengthPower))
            combat.ApplyTemporaryStrengthGain<HextechPowerShieldTemporaryStrengthPower>(target, (int)amount, applier);
        else if (typeof(T) == typeof(HextechTemporaryStrengthLossPower))
            ApplyInvisibleTemporaryLoss<HextechTemporaryStrengthLossPower, StrengthPower>(combat, target, (int)amount, applier, cardSource);
        else if (typeof(T) == typeof(HextechTemporaryDexterityLossPower))
            ApplyInvisibleTemporaryLoss<HextechTemporaryDexterityLossPower, DexterityPower>(combat, target, (int)amount, applier, cardSource);
        else combat.ApplyPowerFromSource(typeof(T), target, (int)amount, applier, cardSource);
        PowerLifecycleSupport.ResolvePowerAmountChanges(_simulator, combat);
        PauseNativeChoice(_simulator);
        return Task.FromResult(combat.GetPower<T>(target));
    }

    private static void ApplyInvisibleTemporaryLoss<T, TStat>(SimulatedCombatState combat, Creature target,
        int amount, Creature? applier, CardModel? source) where T : PowerModel where TStat : PowerModel
    {
        if (amount == 0 || !combat.CanReceivePredictedPowers(target)) return;
        combat.BeginCardPowerApplication(source);
        try
        {
            var incoming = combat.CreatePowerForApplication<T>(target, null, applier);
            if (incoming.IsVisible || incoming.InstanceType == PowerInstanceType.Instanced)
                throw new PredictionUnsupportedException("Reviewed invisible temporary stat wrapper contract changed.");
            // Hextech 0.9.7's artifact compatibility prefix blocks the hidden
            // loss wrapper as a whole. Use the SDK application boundary so the
            // consumed Artifact cannot leave a counter that restores free stats.
            combat.ApplyWithBeforeApplied<T>(target, amount, applier,
                applied => combat.Apply<TStat>(target, -applied, applier),
                (offset, power) =>
                {
                    if (offset != power.Amount) combat.Apply<TStat>(target, -offset, applier);
                });
        }
        finally { combat.CompleteCardPowerApplication(source); }
    }

    private static Task<IReadOnlyList<T>> ApplyPowerMany<T>(IEnumerable<Creature> targets, decimal amount, Creature? applier, CardModel? cardSource, bool silent) where T : PowerModel
    {
        if (_simulator is null) return HextechPowerCmdCompat.Apply<T>(targets, amount, applier, cardSource, silent);
        List<T> applied = [];
        foreach (Creature target in targets)
        {
            T? power = ApplyPowerOne<T>(target, amount, applier, cardSource, silent).GetAwaiter().GetResult();
            if (power is not null) applied.Add(power);
        }
        return Task.FromResult<IReadOnlyList<T>>(applied);
    }
}
