using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MethodInfo NativeGainGold = AccessTools.Method(typeof(PlayerCmd), nameof(PlayerCmd.GainGold),
        [typeof(decimal), typeof(Player), typeof(bool)]);
    private static readonly Func<HextechEnemyHexContext, Player, Task> NativeEnemyBloodIdolGold =
        AccessTools.DeclaredMethod(typeof(BloodIdolEnemyHex), "AfterGoldGained")
            .CreateDelegate<Func<HextechEnemyHexContext, Player, Task>>(new BloodIdolEnemyHex());

    private static void RegisterNativeGold(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BloodIdolRune), "AfterGoldGained"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(NativeHeal, nameof(HealNative)));
        RegisterState<BloodIdolRune>(); RuneMirrors.RegisterNativeBase<BloodIdolRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SacrificeRune), "AfterPlayerTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2),
            Site(NativeGainGold, nameof(GainNativeGold)));
        var alivePredicate = typeof(SacrificeRune).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.Contains("AfterPlayerTurnStart")
                && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(Creature));
        PatchEventCallback(harmony, alivePredicate,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        RegisterState<SacrificeRune>(); RuneMirrors.RegisterNativeBase<SacrificeRune>();
        RegisterNativeSustainCallbacks<SacrificeRune>();
        AfterPlayerTurnStartMirrors.Register<SacrificeRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(SacrificeRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GoldrendRune), "AfterDamageGiven"),
            Site(AccessTools.Method(typeof(HextechCombatVfx), "CoinBurst"), nameof(NativeCoinBurst)),
            Site(NativeGainGold, nameof(GainNativeGold)));
        RegisterNativeDamageHook<GoldrendRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BurningInterestRune), "AfterDamageGiven"),
            SingleNativePowerSite<HextechBurnPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BurningInterestRune), "AfterDamageReceived"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp)),
            Site(NativeGainGold, nameof(GainNativeGold)));
        RegisterNativeDamageHook<BurningInterestRune>();
        AfterDamageReceivedMirrors.Registry.Register<BurningInterestRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterDamageReceived(new ThrowingPlayerChoiceContext(),
                context.Target, context.Result, context.Props, context.Dealer, context.Source?.Preview)), typeof(BurningInterestRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CollectorRune), "AfterDamageGiven"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp)),
            Site(AccessTools.Method(typeof(CollectorRune), "IsCreditableDeath"), nameof(FlyingKickCreditable), 2),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Kill), [typeof(Creature), typeof(bool)]), nameof(KillNative)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CollectorRune), "RecordExecution"),
            Site(AccessTools.Method(typeof(CollectorRune), "IsCreditableDeath"), nameof(FlyingKickCreditable)),
            Site(NativeGainGold, nameof(GainNativeGold)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(CollectorRune), "IsBelowExecuteThreshold"));
        RegisterNativeDamageHook<CollectorRune>();

        var singleDamage = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
            [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardModel), typeof(MegaCrit.Sts2.Core.Entities.Cards.CardPlay)]);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BloodIdolEnemyHex), "AfterGoldGained"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), nameof(NativeBranchProgress)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)), nameof(NativeBranchEnding)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp)), nameof(NativeEnemyCurrentHp), 2),
            Site(NativeSetHp, nameof(SetNativeLivingHp)), Site(singleDamage, nameof(NativeGoldSingleDamage)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(BloodIdolEnemyHex), "NonCombatHpAfterGold"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "AfterGoldGained"));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.BloodIdol);
        foreach (var type in new[] {typeof(BowlerHat), typeof(Ectoplasm)})
        {
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "ModifyGoldGained"));
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterModifyingGoldGained"),
                Site(AccessTools.Method(typeof(RelicModel), nameof(RelicModel.Flash), Type.EmptyTypes), nameof(NativeGoldFlash)));
        }
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DragonFruit), "AfterGoldGained"),
            Site(AccessTools.Method(typeof(RelicModel), nameof(RelicModel.Flash), Type.EmptyTypes), nameof(NativeGoldFlash)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp), [typeof(Creature), typeof(decimal)]), nameof(GainNativeMaxHp)));
        RegisterNativeMaxHp(harmony);
    }

    private static Task<IEnumerable<DamageResult>> NativeGoldSingleDamage(PlayerChoiceContext context, Creature target,
        decimal amount, ValueProp props, CardModel? source, MegaCrit.Sts2.Core.Entities.Cards.CardPlay? play)
        => _simulator is null ? CreatureCmd.Damage(context, target, amount, props, source, play)
            : DamageNativeAmount(context, target, amount, props, null, source, play);
    private static void NativeCoinBurst(Creature target) { if (_simulator is null) HextechCombatVfx.CoinBurst(target); }
    private static void NativeGoldFlash(RelicModel relic) { if (_simulator is null) relic.Flash(); }

    private static Task GainNativeGold(decimal amount, Player player, bool wasStolenBack)
    {
        if (_simulator is not { } simulator) return PlayerCmd.GainGold(amount, player, wasStolenBack);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var listeners = ((ICombatPredictionHookListenerSource)combat).RunHookListeners.ToArray();
        List<AbstractModel> changed = [];
        foreach (var model in listeners)
        {
            var method = AccessTools.Method(model.GetType(), nameof(AbstractModel.ModifyGoldGained));
            if (method.DeclaringType == typeof(AbstractModel)) continue;
            if (model is not BowlerHat and not Ectoplasm)
                throw new PredictionUnsupportedException($"Unreviewed gold modifier: {model.GetType().Name}.");
            decimal previous = amount;
            amount = model.ModifyGoldGained(player, amount);
            if ((int)previous != (int)amount) changed.Add(model);
        }
        foreach (var model in changed) RequireCompleted(model.AfterModifyingGoldGained(player, amount), model.GetType());
        // Original command emits AfterGoldGained only when the final decimal
        // is positive, including a positive fractional amount that casts to 0.
        if (amount <= 0m) return Task.CompletedTask;
        combat.GainPlayerGold(player, (int)amount);
        foreach (var model in ((ICombatPredictionHookListenerSource)combat).RunHookListeners.ToArray())
        {
            var method = AccessTools.Method(model.GetType(), nameof(AbstractModel.AfterGoldGained));
            if (method.DeclaringType == typeof(AbstractModel)) continue;
            if (model is BloodIdolRune rune)
                RequireCompleted(Invoke(rune, simulator, native => native.AfterGoldGained(player)), typeof(BloodIdolRune));
            else if (model is HextechMayhemModifier modifier)
            {
                var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
                if (state.Has(MonsterHexKind.BloodIdol))
                    InvokeNativeEnemyReaction(modifier, state, simulator, context => NativeEnemyBloodIdolGold(context, player));
            }
            else if (model is DragonFruit) RequireCompleted(model.AfterGoldGained(player), model.GetType());
            else throw new PredictionUnsupportedException($"Unreviewed gold event: {model.GetType().Name}.");
        }
        PauseNativeChoice(simulator);
        return Task.CompletedTask;
    }

    private static void GrantNativeFatalGold(SimulatedCombatState combat, Player player, int amount, CombatPredictionSimulator simulator)
    {
        var previous = _simulator;
        _simulator = simulator;
        try
        {
            int before = combat.GetPlayerGold(player);
            RequireCompleted(GainNativeGold(amount, player, false), typeof(PlayerCmd));
            combat.RecordLongTermResource(combat.GetPlayerGold(player) - before);
        }
        finally { _simulator = previous; }
    }
    private static void AlreadyRecordedNativeGold(SimulatedCombatState combat, int value) { }
}
