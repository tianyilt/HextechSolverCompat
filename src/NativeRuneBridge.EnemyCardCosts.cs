using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeEnemyCardCosts(Harmony harmony)
    {
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ArchmageEnemyHex), "RollTrigger"),
            Site(tracking, nameof(NativeCapturedTracking)), Site(combat, nameof(NativeBranchCombat)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ArchmageEnemyHex), "PickCard"),
            Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(combat, nameof(NativeBranchCombat)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ArchmageEnemyHex), "AfterCardPlayed"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechCombatProcTracker), "ConsumePlayerRuneProcInCombat"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(UpgradeEnemyHex), "AfterCardPlayed"),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Downgrade)), nameof(DowngradeNativePlayedCard)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(IGripEnemyHex), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(tracking, nameof(NativeCapturedTracking)),
            Site(AccessTools.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.LoseEnergy)), nameof(LoseNativePlayerEnergy)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(IGripEnemyHex), "TryConsumeFirstCard"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CorruptHeartEnemyHex), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(NativeDamageAmount, nameof(DamageNativeAmount)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(OmniDragonSoulEnemyHex), "BeforePlayerSideTurnStart"),
            ManyNativePowerSite<WeakPower>(), ManyNativePowerSite<FrailPower>(), ManyNativePowerSite<VulnerablePower>());
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.OmniDragonSoul);
    }

    private static void LoseNativePlayerEnergy(PlayerCombatState live, decimal amount)
    {
        if (_simulator is null) { live.LoseEnergy(amount); return; }
        var owner = _simulator.State.CombatState.Players.Single(player => ReferenceEquals(player.PlayerCombatState, live));
        _simulator.State.GetPlayerCombatState(owner).LoseEnergy(amount);
    }

    private static void DowngradeNativePlayedCard(CardModel card)
    {
        if (_simulator is null) { CardCmd.Downgrade(card); return; }
        if (_simulator.IsEnding) return;
        var predicted = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Native enemy downgrade target is absent in the branch.");
        // CardCmd's other write is a run map-history entry for deck cards only.
        // AfterCardPlayed owns a combat card; retain DowngradeInternal including
        // Hextech's audited self-upgrade postfix on the owned preview.
        predicted.MutablePreview.DowngradeInternal();
    }
}
