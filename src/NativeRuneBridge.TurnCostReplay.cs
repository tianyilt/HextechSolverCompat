using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Action<FanTheHammerRune> ClearNativeFanCard =
        AccessTools.Method(typeof(FanTheHammerRune), "ClearDamageReducedCard").CreateDelegate<Action<FanTheHammerRune>>();

    private static void RegisterNativeTurnCostReplay(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        var max = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
        var hp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var pile = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile));
        PatchEventCallback(harmony, AccessTools.Method(typeof(CerberusRune), "ShouldPlayAttackForFree"),
            Site(pile, nameof(NativeBranchCardPile)));
        PatchEventCallback(harmony, AccessTools.Method(typeof(LubricantRune), "ShouldPowerCardBeFree"),
            Site(pile, nameof(NativeBranchCardPile)));
        RegisterNativeTurnState<CerberusRune>(); RegisterNativeTurnState<LubricantRune>();
        RegisterNativeQueryCallbacks<CerberusRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterNativeQueryCallbacks<LubricantRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterAfterCardPlayedCallback<CerberusRune>(); RegisterAfterCardPlayedCallback<LubricantRune>();
        RegisterNativeTurnState<TriPrismRune>();
        RegisterNativeQueryCallbacks<TriPrismRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterNativeReplayHooks<TriPrismRune>();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(TriPrismRune), "ShouldTrigger"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechColorlessCardHelper), "IsColorlessCard"));

        var missileLambda = typeof(MagicMissileRune).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.Contains("AfterCardPlayed")
                && method.ReturnType == typeof(decimal) && method.GetParameters().Length == 1);
        PatchEventCallback(harmony, missileLambda, Site(max, nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MagicMissileRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechMissileVolley), "PlayVfxAsync"), nameof(NativeMissileVfx)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(MagicMissileRune), "CalculateMissileDamage"));
        RegisterNativeTurnState<MagicMissileRune>(); RegisterAfterCardPlayedCallback<MagicMissileRune>();

        PatchEventCallback(harmony, AccessTools.Method(typeof(SpinToWinRune), "ConvertDelayedResource"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Amount)), nameof(NativeDelayedAmount), 2),
            Site(NativeDraw, nameof(Draw)), Site(AccessTools.Method(typeof(PlayerCmd), "GainEnergy"), nameof(GainNativeEnergy)),
            Site(AccessTools.Method(typeof(PlayerCmd), "GainStars"), nameof(GainNativeStars)),
            Site(AccessTools.Method(typeof(OstyCmd), "Summon", [typeof(PlayerChoiceContext), typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)),
            Site(AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]), nameof(RemoveNativePower)));
        RegisterState<SpinToWinRune>(); RuneMirrors.RegisterNativeBase<SpinToWinRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SpinToWinRune), "AfterPowerAmountChanged"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(SpinToWinRune), "IsConvertiblePower"));

        // Preserve native teammate ordering, while its roster and HP queries
        // belong to the captured branch. Single-player naturally has no target.
        foreach (var method in typeof(BlossomBladeRune).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                     .SelectMany(AccessTools.GetDeclaredMethods).Where(method => method.Name.Contains("FindLowestHpRatioTeammate")))
        {
            if (method.ReturnType == typeof(bool)) PatchEventCallback(harmony, method,
                Site(alive, nameof(NativeBranchIsAlive)), Site(max, nameof(NativeBranchMaxHp)));
            else if (method.ReturnType == typeof(decimal)) PatchEventCallback(harmony, method,
                Site(hp, nameof(NativeEnemyCurrentHp)), Site(max, nameof(NativeBranchMaxHp)));
            else NativeCallbackContracts.Add(method);
        }
        PatchEventCallback(harmony, AccessTools.Method(typeof(BlossomBladeRune), "FindLowestHpRatioTeammate"),
            Site(AccessTools.PropertyGetter(typeof(IPlayerCollection), "Players"), nameof(NativeFrozenTeamPlayers)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BlossomBladeRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding)),
            Site(max, nameof(NativeBranchMaxHp)), Site(NativeHeal, nameof(HealNative)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(BlossomBladeRune), "GetHealAmount"));
        RegisterState<BlossomBladeRune>(); RuneMirrors.RegisterNativeBase<BlossomBladeRune>();
        RegisterAfterCardPlayedCallback<BlossomBladeRune>();

        RegisterNativeTurnState<FanTheHammerRune>();
        RegisterNativeReplayHooks<FanTheHammerRune>();
        RegisterNativeQueryCallbacks<FanTheHammerRune>(NativeQueries.DamageMultiplier);
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(FanTheHammerRune))) NativeCallbackContracts.Add(method);
        var tail = AccessTools.Method(typeof(CombatPredictionSimulator), "ContinueCardResultExecution");
        var cleanup = AccessTools.Method(typeof(NativeRuneBridge), nameof(FinishNativeFanCard));
        harmony.Patch(tail, postfix: new HarmonyMethod(cleanup));
        NativeCallbackContracts.AddNativePostfix(tail, cleanup, "HextechSolverCompat", Priority.Normal);

        RegisterStableState<JeweledGauntletRune>(); RuneMirrors.RegisterNativeBase<JeweledGauntletRune>();
        ModifyCardPlayCountMirrors.Registry.Register<JeweledGauntletRune>((rune, context) =>
            Invoke(rune, context.Simulator, model => model.ModifyCardPlayCount(
                context.Card.MutablePreview, context.Target, context.PlayCount)));
        BeforeCardPlayedMirrors.Registry.Register<JeweledGauntletRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeCardPlayed(context.CardPlay)), typeof(JeweledGauntletRune)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(JeweledGauntletRune))) NativeCallbackContracts.Add(method);
    }

    private static void RegisterNativeTurnState<T>() where T : TurnScopedRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(T)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), "ResetTurnScopedState"));
    }

    private static int NativeDelayedAmount(PowerModel power)
        => _simulator is null ? power.Amount
            : NativeRemovalPowers(power.Owner).SingleOrDefault(candidate => candidate.GetType() == power.GetType())?.Amount ?? 0;

    private static void FinishNativeFanCard(CombatPredictionSimulator __instance, PredictedCard card, bool __result)
    {
        if (!__result) return; // A pending choice retains the original tracked card.
        var combat = (SimulatedCombatState)__instance.State.CombatState;
        foreach (var rune in combat.RelicsOf(card.Preview.Owner).OfType<FanTheHammerRune>())
            Invoke(rune, __instance, model =>
            {
                if (ReferenceEquals(NativeRuneState.FanCard.GetValue(model), card.MutablePreview)) ClearNativeFanCard(model);
                return true;
            });
    }
}
