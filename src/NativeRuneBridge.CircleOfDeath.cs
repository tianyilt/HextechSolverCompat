using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Func<int, int, decimal> NativeActualHeal = AccessTools.DeclaredMethod(
        AccessTools.TypeByName("HextechRunes.HextechCombatHooks"), "CalculateActualHealAmount")
        .CreateDelegate<Func<int, int, decimal>>();

    private static void RegisterNativeCircleOfDeath(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CircleOfDeathRune), "HandleSustainGained"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 3),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), nameof(NativeBranchProgress)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatVfx), "DeathRingLash"), nameof(SkipNativeCircleVfx)),
            Site(NativeDamageAmount, nameof(DamageNativeAmount)));
        var alive = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(CircleOfDeathRune), "<>c"))
            .Single(method => method.Name.StartsWith("<HandleSustainGained>b__", StringComparison.Ordinal));
        PatchEventCallback(harmony, alive,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(CircleOfDeathRune), "AfterBlockGained"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(AccessTools.TypeByName("HextechRunes.HextechCombatHooks"), "CalculateActualHealAmount"));
        RegisterStableState<CircleOfDeathRune>();
        RuneMirrors.RegisterNativeBase<CircleOfDeathRune>();
        AfterBlockGainedMirrors.Registry.Register<CircleOfDeathRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterBlockGained(
                context.Creature, context.Amount, context.Props, context.Source?.MutablePreview)), typeof(CircleOfDeathRune)));
    }

    private static void SkipNativeCircleVfx(Creature source, Creature target)
    {
        if (_simulator is null) HextechCombatVfx.DeathRingLash(source, target);
    }

    internal static void NativeCircleHealed(CombatPredictionSimulator simulator, Creature creature, int hpBefore)
    {
        if (simulator.State.CombatState is not SimulatedCombatState combat
            || creature.Player is not { } player || creature != player.Creature) return;
        var rune = combat.RelicsOf(player).OfType<CircleOfDeathRune>().FirstOrDefault();
        if (rune is null) return;
        var restored = NativeActualHeal(hpBefore, simulator.State.GetCreature(creature).CurrentHp);
        if (restored <= 0m) return;
        RequireCompleted(Invoke(rune, simulator, model => model.HandleSustainGained(restored)), typeof(CircleOfDeathRune));
    }
}
