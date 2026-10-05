using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly System.Reflection.MethodInfo NativeSetHp =
        AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.SetCurrentHp), [typeof(Creature), typeof(decimal)]);

    private static void RegisterNativeTurnResources(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BrutalityRune), "AfterPlayerTurnStartEarly"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp), 2),
            Site(NativeSetHp, nameof(SetNativeLivingHp)), Site(NativeDraw, nameof(Draw)));
        RegisterNativeEarlyTurn<BrutalityRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SonataRune), "AfterPlayerTurnStartEarly"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(NativeDraw, nameof(Draw)), Site(NativeHeal, nameof(HealNative)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
                [typeof(Creature), typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar), typeof(MegaCrit.Sts2.Core.Entities.Cards.CardPlay), typeof(bool)]),
                nameof(GainNativeVarBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(AccessTools.Inner(typeof(SonataRune), "<>c"), "<AfterPlayerTurnStartEarly>b__5_0"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        RegisterNativeEarlyTurn<SonataRune>();

        RegisterNativeLateEnergy<BrokenGoldenCrownRune>(harmony);
        RegisterNativeLateEnergy<BerserkRune>(harmony);
        var late = AccessTools.Method(typeof(TurnStartRelicSupport), nameof(TurnStartRelicSupport.TriggerAfterEnergyResetLate));
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeLateEnergy));
        harmony.Patch(late, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(late, rewrite);
    }

    private static bool NativeBranchIsAlive(Creature creature)
        => _simulator is { } simulator ? simulator.State.GetCreature(creature).IsAlive : creature.IsAlive;

    private static void RegisterNativeEarlyTurn<T>() where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterPlayerTurnStartMirrors.RegisterEarly<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStartEarly(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
    }

    private static void RegisterNativeLateEnergy<T>(Harmony harmony) where T : HextechRelicBase
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(T), "AfterEnergyResetLate"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
    }

    private static bool DispatchNativeLateEnergy(CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player)
    {
        var relics = combat.RelicsOf(player);
        if (!relics.Any(relic => relic is BrokenGoldenCrownRune or BerserkRune or EnergyForge)
            && !combat.EffectivePowers().Any(power => power is HextechDragonSoulPower && power.Amount > 0)) return false;
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is RelicModel { IsMelted: true } || listener is PowerModel { Amount: <= 0 }) continue;
            switch (listener)
            {
                case HextechDragonSoulPower power:
                    InvokeNativePower(power, simulator, model => model.AfterEnergyResetLate(player));
                    break;
                case BrokenGoldenCrownRune or BerserkRune or EnergyForge:
                    RequireCompleted(Invoke((HextechRelicBase)listener, simulator, model => model.AfterEnergyResetLate(player)), listener.GetType());
                    break;
                case BoundPhylactery relic when combat.GetPlayerTurnNumber(player) != 1:
                    // Preserve the pinned SDK's sole stock late relic at its
                    // original ordered position, including its summon hooks.
                    combat.SummonOsty(simulator, player, relic.DynamicVars.Summon.IntValue);
                    break;
            }
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); break; }
        }
        return true;
    }

    private static IEnumerable<CodeInstruction> RewriteNativeLateEnergy(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.ToList();
        if (code.Count(i => i.operand is System.Reflection.MethodInfo m && m.Name == "OfType"
                && m.IsGenericMethod && m.GetGenericArguments().SequenceEqual([typeof(BoundPhylactery)])) != 1
            || code.Count(i => i.Calls(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.SummonOsty)))) != 1)
            throw new InvalidOperationException("Pinned late energy stock relic catalogue changed.");
        var original = generator.DefineLabel();
        code[0].labels.Add(original);
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Ldarg_2);
        yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(DispatchNativeLateEnergy));
        yield return new CodeInstruction(OpCodes.Brfalse, original);
        yield return new CodeInstruction(OpCodes.Ret);
        foreach (var instruction in code) yield return instruction;
    }

    private static Task SetNativeLivingHp(Creature creature, decimal amount)
    {
        if (_simulator is null) return CreatureCmd.SetCurrentHp(creature, amount);
        var state = _simulator.State.GetCreature(creature);
        // These exact native callers only reduce living HP, retaining >=1.
        // Revival/death requires a separate lifecycle, never this shortcut.
        if (!state.IsAlive || amount < 1m || amount > state.CurrentHp)
            throw new PredictionUnsupportedException("Living-HP bridge received an unreviewed revival/gain/death request.");
        int previous = state.CurrentHp;
        state.CurrentHp = (int)Math.Min(amount, state.MaxHp);
        if (amount != previous) HookMirrors.AfterCurrentHpChanged(_simulator, creature, amount - previous);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }

    private static void RegisterNativeEnemyBrutality(Harmony harmony)
    {
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 5
            && method.GetParameters()[0].ParameterType == typeof(Creature));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BrutalityEnemyHex), "BeforeEnemySideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp), 2),
            Site(AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(Creature), typeof(decimal)]), nameof(NativeEnemyHpFraction)),
            Site(NativeSetHp, nameof(SetNativeLivingHp)),
            new NativeCallSite(apply.MakeGenericMethod(typeof(VigorPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(typeof(VigorPower)), 1));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.Brutality);
    }
}
