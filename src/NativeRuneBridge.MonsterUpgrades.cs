using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private delegate bool NativeLivingModifierQuery(Creature creature, MonsterHexKind kind, out HextechMayhemModifier? modifier);
    private static readonly NativeLivingModifierQuery OriginalLivingUpgradeModifier =
        AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "TryGetLivingEnemyHexModifier").CreateDelegate<NativeLivingModifierQuery>();
    private static readonly Func<Task, CeremonialBeastStrengthIntent?, ThievingHopperTheftIntent?, Task> OriginalCompleteMonsterUpgrade =
        AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "CompleteMonsterUpgradeMove")
            .CreateDelegate<Func<Task, CeremonialBeastStrengthIntent?, ThievingHopperTheftIntent?, Task>>();
    private static void RegisterNativeMonsterUpgrades(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "CompleteMonsterUpgradeMove"),
            Site(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), "TryGetLivingEnemyHexModifier"), nameof(NativeLivingUpgradeModifier), 2),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method => method.Name == "Apply"
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 5
                && method.GetParameters()[0].ParameterType == typeof(Creature)).MakeGenericMethod(typeof(StrengthPower)), nameof(ApplyNativeCeremonialStrength)));
        foreach (string name in new[] { "AddMonsterUpgradeIntents", "ComposeMonsterUpgradeIntents" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatHooks), name));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.CeremonialBeast);
        RegisterNativeThievingHopper(harmony);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task<StrengthPower?> ApplyNativeCeremonialStrength(Creature target, decimal amount, Creature? applier,
        MegaCrit.Sts2.Core.Models.CardModel? cardSource, bool silent)
        => ApplyPowerOne<StrengthPower>(target, amount, applier, cardSource, silent);
    private static bool NativeLivingUpgradeModifier(Creature creature, MonsterHexKind kind, out HextechMayhemModifier? modifier)
    {
        if (_simulator is not { } simulator) return OriginalLivingUpgradeModifier(creature, kind, out modifier);
        var scope = _nativeEnemyTurn ?? throw new PredictionUnsupportedException("Monster upgrade has no captured enemy scope.");
        modifier = scope.State.Has(kind) && creature.Side == MegaCrit.Sts2.Core.Combat.CombatSide.Enemy
            && ((SimulatedCombatState)simulator.State.CombatState).ContainsCreature(creature)
            && !simulator.State.GetCreature(creature).IsDead ? scope.Modifier : null;
        return modifier is not null;
    }
    internal static void CompleteNativeMonsterUpgrade(CombatPredictionSimulator simulator, SimulatedCombatState combat, ForecastMove move)
    {
        if (simulator.HasPendingChoice) return;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier is null) return;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        bool ceremonial = state.Has(MonsterHexKind.CeremonialBeast) && move.Move.Intents.Any(intent => intent is AttackIntent);
        var theft = state.Has(MonsterHexKind.ThievingHopper) ? move.Move.Intents.OfType<ThievingHopperTheftIntent>().FirstOrDefault() : null;
        if (!ceremonial && theft is null) return;
        int strength = state.StrengthTier <= 1 ? 1 : state.StrengthTier == 2 ? 2 : 3;
        var intent = ceremonial ? new CeremonialBeastStrengthIntent(move.Owner, strength) : null;
        // Native JeweledGauntlet repeats MoveState.PerformMove, including its
        // upgrade completion. Resolve this before the repeat is evaluated.
        InvokeNativeEnemyReaction(modifier, state, simulator, _ => OriginalCompleteMonsterUpgrade(Task.CompletedTask, intent, theft));
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
    }
}
