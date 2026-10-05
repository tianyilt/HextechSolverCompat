using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeDrawExhaustPeriod(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DizzySpinningRune), "AfterShuffle"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        RegisterState<DizzySpinningRune>(); RuneMirrors.RegisterNativeBase<DizzySpinningRune>();
        // SDK evaluates this pure modifier on a detached root relic. Its only
        // inputs are the captured DynamicVars and the player identity.
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DizzySpinningRune), "ModifyHandDraw"));
        AfterShuffleMirrors.Registry.Register<DizzySpinningRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterShuffle(new ThrowingPlayerChoiceContext(), context.Player)), typeof(DizzySpinningRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawThresholdRuneBase), "AfterCardDrawn"),
            Site(dead, nameof(NativeBranchIsDead)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NightstalkingRune), "ApplyDrawThresholdReward"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<IntangiblePower>());
        RegisterNativeDrawThreshold<NightstalkingRune>();
        // Default-disabled catalogue entries can still be obtained from rewards
        // and retained in saves. Reuse the actual threshold callback, including
        // its captured progress, rather than merely allowing the relic ID.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(WarmogsSpiritRune), "ApplyDrawThresholdReward"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<PlatingPower>());
        RegisterNativeDrawThreshold<WarmogsSpiritRune>();
        RegisterNativeDeathWarrant(harmony);

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RekindleRune), "AfterCardExhausted"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        RegisterState<RekindleRune>(); RuneMirrors.RegisterNativeBase<RekindleRune>();
        AfterCardExhaustedMirrors.Registry.Register<RekindleRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardExhausted(new ThrowingPlayerChoiceContext(),
                context.Card.MutablePreview, context.CausedByEthereal)), typeof(RekindleRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DivineInterventionRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatVfx), "DivinePulse"), nameof(SkipNativeDivinePulse)),
            ManyNativePowerSite<IntangiblePower>());
        var predicate = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(DivineInterventionRune), "<>c"))
            .Single(method => method.Name.StartsWith("<AfterPlayerTurnStart>b__", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool));
        PatchEventCallback(harmony, predicate,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        RegisterState<DivineInterventionRune>(); RuneMirrors.RegisterNativeBase<DivineInterventionRune>();
        AfterPlayerTurnStartMirrors.Register<DivineInterventionRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(DivineInterventionRune)));
    }

    private static void SkipNativeDivinePulse(IReadOnlyList<Creature> creatures)
    { if (_simulator is null) HextechCombatVfx.DivinePulse(creatures); }

    private static void RegisterNativeDrawThreshold<T>() where T : DrawThresholdRuneBase
    {
        RegisterState<T>(); RuneMirrors.RegisterNativeBase<T>();
        AfterCardDrawnMirrors.Registry.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardDrawn(new ThrowingPlayerChoiceContext(),
                context.Card.MutablePreview, context.FromHandDraw)), typeof(T)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DrawThresholdRuneBase), "AfterCardPlayedLate"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DrawThresholdRuneBase), "AfterPlayerTurnStartLate"));
        AfterCardPlayedMirrors.LateRegistry.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterCardPlayedLate(new ThrowingPlayerChoiceContext(), context.CardPlay)), typeof(T)));
        AfterPlayerTurnStartMirrors.RegisterLate<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStartLate(new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
    }

    private static void RegisterNativeDeathWarrant(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DeathWarrantRune), "ApplyDrawThresholdReward"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)));
        var predicates = AccessTools.GetDeclaredMethods(AccessTools.Inner(typeof(DeathWarrantRune), "<>c"));
        PatchEventCallback(harmony, predicates.Single(m => m.Name == "<ApplyDrawThresholdReward>b__9_0"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        var get = typeof(Creature).GetMethods().Single(m => m.Name == nameof(Creature.GetPower) && m.IsGenericMethodDefinition);
        PatchEventCallback(harmony, predicates.Single(m => m.Name == "<ApplyDrawThresholdReward>b__9_1"),
            new NativeCallSite(get.MakeGenericMethod(typeof(PoisonPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(PoisonPower)), 1));
        PatchEventCallback(harmony, predicates.Single(m => m.Name == "<ApplyDrawThresholdReward>b__9_2"),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount)), nameof(NativeDeathWarrantPoisonAmount)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DeathWarrantRune), "TriggerPoisonCompat"),
            Site(AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.AfterSideTurnStart)), nameof(NativeDeathWarrantPoisonTick)));
        RegisterNativeDrawThreshold<DeathWarrantRune>();
    }

    private static int NativeDeathWarrantPoisonAmount(PowerModel power)
        => _simulator is null ? power.Amount : ((SimulatedCombatState)_simulator.State.CombatState).GetAmount<PoisonPower>(power.Owner);

    private static Task NativeDeathWarrantPoisonTick(AbstractModel model, CombatSide side,
        IReadOnlyList<Creature> creatures, ICombatState combat)
    {
        if (_simulator is null) return model.AfterSideTurnStart(side, creatures, combat);
        if (model is not PoisonPower poison || side != poison.Owner.Side
            || creatures.Count != 1 || !ReferenceEquals(creatures[0], poison.Owner)
            || !ReferenceEquals(combat, _simulator.State.CombatState))
            throw new PredictionUnsupportedException("Native DeathWarrant poison tick left its audited ownership contract.");
        CorePowerSupport.TriggerPoison(_simulator, (SimulatedCombatState)combat, creatures);
        PauseNativeChoice(_simulator);
        return Task.CompletedTask;
    }
}
