using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeRegisteredTransfers(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var amount = AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TwilightVeilRune), "AfterPowerAmountChanged"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat), 3),
            Site(AccessTools.Method(typeof(HextechPowerCmdCompat), "Apply", [typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool)]), nameof(ApplyRegisteredPowerModel)));
        RegisterNativeEarlyTurn<TwilightVeilRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ScapegoatRune), "IsDebuff"), Site(amount, nameof(RegisteredPowerAmount)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ScapegoatRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead), 3), Site(combat, nameof(NativeBranchCombat), 2),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)), nameof(NativeBranchEnding), 2),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Powers)), nameof(NativeRemovalPowers), 2),
            Site(amount, nameof(RegisteredPowerAmount)),
            Site(AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]), nameof(RemoveNativePower)),
            Site(AccessTools.GetDeclaredMethods(typeof(PowerCmd)).Single(method => method.Name == "Apply" && !method.IsGenericMethod
                && method.GetParameters().Length == 7 && method.GetParameters()[1].ParameterType == typeof(PowerModel)), nameof(ApplyRegisteredContextPowerModel)));
        RegisterStableState<ScapegoatRune>(); RuneMirrors.RegisterNativeBase<ScapegoatRune>(); RegisterRegisteredTurn<ScapegoatRune>();
        foreach (string name in new[] { "SnapshotDebuffs", "CreateEnemyTransfer", "ResetTransferCount" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ScapegoatRune), name));

        // The SDK batches player Poison at the preceding enemy-end boundary.
        // Native Poison runs after the player's turn-start relics, so cleansing
        // there must happen before its tick. Preserve the stock tick body and
        // move only this exposed batching case to its native side-power slot.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CombatBeamSolver), "AdvanceRound"),
            Site(AccessTools.Method(typeof(CorePowerSupport), nameof(CorePowerSupport.TriggerPoison)),
                nameof(TriggerPoisonAtRegisteredBoundary), 2));
    }

    private static bool HasRegisteredPoisonOrdering(SimulatedCombatState combat, Creature creature)
        => creature.Player is { } player && combat.RelicsOf(player).Any(rune => rune is ScapegoatRune);

    private static bool TriggerPoisonAtRegisteredBoundary(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, IEnumerable<Creature> creatures)
        => CorePowerSupport.TriggerPoison(simulator, combat,
            creatures.Where(creature => !HasRegisteredPoisonOrdering(combat, creature)));

    private static int RegisteredPowerAmount(PowerModel power)
        => _simulator is null ? power.Amount : _simulator.StateStore.GetPowerAmount(power).Amount;

    private static Task ApplyRegisteredPowerModel(PowerModel power, Creature target, decimal amount,
        Creature? applier, CardModel? source, bool silent)
        => _simulator is null ? HextechPowerCmdCompat.Apply(power, target, amount, applier, source, silent)
            : ApplyRegisteredContextPowerModel(new ThrowingPlayerChoiceContext(), power, target, amount, applier, source, silent);

    private static Task ApplyRegisteredContextPowerModel(PlayerChoiceContext context, PowerModel power, Creature target,
        decimal amount, Creature? applier, CardModel? source, bool silent)
    {
        if (_simulator is null) return PowerCmd.Apply(context, power, target, amount, applier, source, silent);
        // Twilight supplies a canonical mutable instance. Scapegoat supplies
        // one of the original exact, reviewed transfer types and clears its
        // player duration exemption. The public typed application pipeline
        // creates a fresh target instance, retaining Artifact, coefficients,
        // provenance and ordered power reactions rather than copying owners.
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        bool inserting = !combat.EffectivePowers().Any(existing => existing.GetType() == power.GetType()
            && ReferenceEquals(existing.Owner, target));
        var apply = AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(power.GetType());
        var task = (Task)apply.Invoke(null, [target, amount, applier, source, silent])!;
        RequireCompleted(task, power.GetType());
        // Scapegoat transfers a clone, rather than a canonical new power.
        // Deferred damage retains its start-of-turn snapshot and therefore
        // resolves during this same side-start. Stacking onto an existing
        // target power must retain that target instance's snapshot instead.
        if (inserting && power is HextechNextTurnDamagePower
            && combat.GetMutablePower<HextechNextTurnDamagePower>(target) is { } transferred)
            transferred.AmountOnTurnStart = power.AmountOnTurnStart;
        return Task.CompletedTask;
    }
}
