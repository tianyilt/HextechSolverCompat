using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly System.Reflection.FieldInfo LiveInstantDoomQueue = AccessTools.Field(typeof(HextechCombatHooks), "PendingInstantDeathDoomKills");
    private static void RegisterNativeInstantDeath(Harmony harmony)
    {
        var amount = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPowerAmount" && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(InstantDeathRune), "KillIfDoomExceedsHp"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsAlive"), nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CurrentHp"), nameof(NativeEnemyCurrentHp)),
            new NativeCallSite(amount.MakeGenericMethod(typeof(DoomPower)), AccessTools.Method(typeof(NativeRuneBridge), "GetPowerAmount").MakeGenericMethod(typeof(DoomPower)), 1),
            Site(AccessTools.Method(typeof(HextechCombatHooks), "QueueInstantDeathDoomKill"), nameof(QueueNativeInstantDoom)),
            Site(AccessTools.Method(typeof(DoomPower), "DoomKill"), nameof(KillNativeInstantDoom)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CardOnPlaySupport), "ApplyOutbreak"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteOutbreakPowerBoundary)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CardOnPlaySupport), "ApplyBatch042"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteOutbreakCompensationGuard)));
        RegisterState<InstantDeathRune>();
        RuneMirrors.RegisterNativeBase<InstantDeathRune>();
        foreach (string name in new[] { "AfterPowerAmountChanged", "AfterCurrentHpChanged" }) NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(InstantDeathRune), name));
        AfterCurrentHpChangedMirrors.Registry.Register<InstantDeathRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCurrentHpChanged(context.Creature, context.Delta)), typeof(InstantDeathRune)));
    }
    private static IEnumerable<CodeInstruction> RewriteOutbreakCompensationGuard(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.DeclaredMethod(typeof(CardOnPlaySupport), "ApplyOutbreak");
        int sites = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(target))
            {
                var replacement = CodeInstruction.Call(typeof(NativeRuneBridge), nameof(RunNativeOutbreakCompensation));
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                yield return replacement;
                sites++;
            }
            else yield return instruction;
        }
        if (sites != 1) throw new InvalidOperationException("Pinned Outbreak compensation call changed.");
    }
    private static void RunNativeOutbreakCompensation(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        MegaCrit.Sts2.Core.Models.CardModel card, ISet<uint> processedEnemyDeaths)
    {
        RequireCompleted(() => OriginalOutbreakGuardRunner(() =>
        {
            CardOnPlaySupport.ApplyOutbreak(simulator, combat, card, processedEnemyDeaths);
            return Task.CompletedTask;
        }), typeof(MegaCrit.Sts2.Core.Models.Cards.Outbreak));
        FlushNativeInstantDoom(simulator);
    }
    private static IEnumerable<CodeInstruction> RewriteOutbreakPowerBoundary(IEnumerable<CodeInstruction> instructions)
    {
        int sites = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is System.Reflection.MethodInfo method
                && method.DeclaringType == typeof(SimulatedCombatState) && method.Name == "Apply"
                && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(PoisonPower) }))
            {
                // Apply returns the branch-owned power; retain the SDK's pop.
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(ResolveOutbreakPoisonBoundary));
                sites++;
            }
        }
        if (sites != 1) throw new InvalidOperationException("Pinned Outbreak poison application boundary changed.");
    }
    private static void ResolveOutbreakPoisonBoundary(CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        if (!HextechCombatHooks.IsResolvingOutbreakPowerPoisonResponse) return;
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        PauseNativeChoice(simulator);
    }
    internal static void AssertNativeInstantQueueQuiescent(HextechMayhemModifier modifier)
    {
        if (((List<Creature>)LiveInstantDoomQueue.GetValue(null)!).Any(creature => creature.CombatState?.RunState == modifier.ActiveRunState))
            throw new PredictionUnsupportedException("Native deferred Doom kills must finish before capturing a root.");
    }
    private static EnemyState NativeInstantQueueState(CombatPredictionSimulator simulator)
    {
        var modifier = simulator.State.CombatState.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault()
            ?? throw new PredictionUnsupportedException("InstantDeath requires the captured native Mayhem combat context.");
        return ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void QueueNativeInstantDoom(Creature creature)
    {
        if (_simulator is not { } simulator) { HextechCombatHooks.QueueInstantDeathDoomKill(creature); return; }
        var pending = NativeInstantQueueState(simulator).PendingInstantDoom;
        if (!pending.Contains(creature)) pending.Add(creature);
    }
    private static Task KillNativeInstantDoom(IReadOnlyList<Creature> creatures)
    {
        if (_simulator is not { } simulator) return DoomPower.DoomKill(creatures);
        ((SimulatedCombatState)simulator.State.CombatState).DoomKill(simulator, creatures);
        PauseNativeChoice(simulator);
        return Task.CompletedTask;
    }
    internal static void DispatchNativeInstantPower(InstantDeathRune rune, CombatPredictionSimulator simulator, SimulatedPowerAmountChange change)
        => RequireCompleted(Invoke(rune, simulator, model => model.AfterPowerAmountChanged(new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, null)), typeof(InstantDeathRune));
    internal static void FlushNativeInstantDoom(CombatPredictionSimulator simulator)
    {
        if (HextechCombatHooks.IsResolvingSleightOfFleshPowerDebuffResponse || HextechCombatHooks.IsResolvingOutbreakPowerPoisonResponse || simulator.HasPendingChoice) return;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return;
        var pending = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).PendingInstantDoom;
        while (pending.Count > 0)
        {
            var creature = pending[0]; pending.RemoveAt(0);
            if (simulator.State.GetCreature(creature).IsAlive && combat.GetAmount<DoomPower>(creature) > simulator.State.GetCreature(creature).CurrentHp)
            {
                combat.DoomKill(simulator, [creature]);
                if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); return; }
            }
        }
    }
}
