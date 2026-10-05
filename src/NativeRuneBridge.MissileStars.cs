using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeMissileStars(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GalacticGiftRune), "AfterStarsSpent"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.PlayerCmd), "GainStars"), nameof(GainNativeStars)));
        RegisterState<GalacticGiftRune>(); RuneMirrors.RegisterNativeBase<GalacticGiftRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechMissileVolley), "ResolveVolleyDamageInLockstepAsync"),
            Site(dead, nameof(NativeBranchIsDead)), Site(alive, nameof(NativeBranchIsAlive)),
            Site(combat, nameof(NativeBranchCombat), 2), Site(NativeDamageAmount, nameof(DamageNativeAmount)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechMissileVolley), "DamageFromEnergyCost"));
        // This shared predicate has one native liveness read; retaining its
        // sorting and targeting rules avoids inventing an attack interpretation.
        var predicate = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(HextechRuneTargeting), "<>c"))
            .Single(method => method.Name.StartsWith("<ResolveCardPlayEnemyTargets>b__", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool));
        PatchEventCallback(harmony, predicate, new NativeCallSite(alive,
            AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeBranchIsAlive)), 1));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechRuneTargeting), "ResolveCardPlayEnemyTargets"));
        foreach (var type in new[] { typeof(LightEmUpRune), typeof(TwinFlamesRune) })
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterCardPlayed"),
                Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
                Site(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "GetEnergyCostForCurrentCardPlay"), nameof(NativeMissileEnergy)),
                Site(AccessTools.DeclaredMethod(typeof(HextechMissileVolley), "PlayVfxAsync"), nameof(NativeMissileVfx)));
        RegisterState<LightEmUpRune>(); RuneMirrors.RegisterNativeBase<LightEmUpRune>();
        RegisterAfterCardPlayedCallback<LightEmUpRune>();
        RegisterStableState<TwinFlamesRune>(); RuneMirrors.RegisterNativeBase<TwinFlamesRune>();
        RegisterAfterCardPlayedCallback<TwinFlamesRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(LightEmUpRune), "AdvanceAttackProgress"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(TwinFlamesRune), "ShouldLaunchMissiles"));
    }

    private static decimal NativeMissileEnergy(CardModel card)
        => _simulator is null ? HextechCombatHooks.GetEnergyCostForCurrentCardPlay(card)
            : FindNativePlayCost(card) ?? throw new InvalidOperationException("Native missile callback lost its captured play resource value.");

    private static Task NativeMissileVfx(Creature source, IReadOnlyList<Creature> targets,
        int missiles, Func<Creature, Creature, int, Task<bool>> play)
        => _simulator is null ? HextechMissileVolley.PlayVfxAsync(source, targets, missiles, play) : Task.CompletedTask;
}
