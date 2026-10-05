using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeDuality(Harmony harmony)
    {
        var stat = AccessTools.DeclaredMethod(typeof(MasterOfDualityRune), "ApplyTemporaryStat");
        var strength = new NativeCallSite(stat.MakeGenericMethod(typeof(HextechTemporaryStrengthPower), typeof(HextechTemporaryStrengthLossPower)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeDualityStrength)), 1);
        var dexterity = new NativeCallSite(stat.MakeGenericMethod(typeof(HextechTemporaryDexterityPower), typeof(HextechTemporaryDexterityLossPower)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeDualityDexterity)), 1);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MasterOfDualityRune), "AfterCardPlayed"), strength, dexterity);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MasterOfDualityEnemyHex), "AfterCardPlayed"), strength, dexterity,
            Site(AccessTools.PropertyGetter(typeof(Creature), "IsAlive"), nameof(NativeBranchIsAlive)));
        RegisterAfterCardPlayed<MasterOfDualityRune>();
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.MasterOfDuality);
    }
    private static Task ApplyNativeDualityStrength(Creature creature, bool isGain, Creature? applier, CardModel? cardSource)
    {
        if (_simulator is null) return MasterOfDualityRune.ApplyTemporaryStat<HextechTemporaryStrengthPower, HextechTemporaryStrengthLossPower>(creature, isGain, applier, cardSource);
        ApplyOwnedDualityStat<HextechTemporaryStrengthPower, HextechTemporaryStrengthLossPower, StrengthPower>(creature, isGain, applier, cardSource, true);
        return Task.CompletedTask;
    }
    private static Task ApplyNativeDualityDexterity(Creature creature, bool isGain, Creature? applier, CardModel? cardSource)
    {
        if (_simulator is null) return MasterOfDualityRune.ApplyTemporaryStat<HextechTemporaryDexterityPower, HextechTemporaryDexterityLossPower>(creature, isGain, applier, cardSource);
        ApplyOwnedDualityStat<HextechTemporaryDexterityPower, HextechTemporaryDexterityLossPower, DexterityPower>(creature, isGain, applier, cardSource, false);
        return Task.CompletedTask;
    }
    private static void ApplyOwnedDualityStat<TGain, TLoss, TStat>(Creature creature, bool isGain, Creature? applier, CardModel? source, bool strength)
        where TGain : PowerModel where TLoss : PowerModel where TStat : PowerModel
    {
        var simulator = _simulator!; var combat = (SimulatedCombatState)simulator.State.CombatState;
        PowerModel? stale = isGain ? combat.GetPower<TGain>(creature) : combat.GetPower<TLoss>(creature);
        // The SDK encodes removed counter powers as zero. Reinitialization's
        // before-application stat command handles that same native fresh instance.
        if (stale is { Amount: <= 0 }) combat.SetPowerAmount(stale, 0);
        PowerModel? opposite = isGain ? combat.GetPower<TLoss>(creature) : combat.GetPower<TGain>(creature);
        bool artifactBlocksLoss = !isGain && combat.GetAmount<ArtifactPower>(creature) > 0;
        combat.BeginCardPowerApplication(source);
        try
        {
            if (opposite is { Amount: > 0 } && !artifactBlocksLoss)
            {
                opposite = combat.GetMutablePowerInstance(opposite);
                combat.SetPowerAmount(opposite, opposite.Amount - 1);
                combat.RecordPowerAmountChange(opposite, -1, applier);
                // Original TemporaryStat.OnAmountChanged applies the same delta
                // with its sign; cancelling a loss gives the point back.
                combat.Apply<TStat>(creature, isGain ? 1 : -1, applier);
            }
            else if (isGain)
            {
                if (strength) combat.ApplyTemporaryStrengthGain<TGain>(creature, 1, applier);
                else combat.ApplyTemporaryDexterity<TGain>(creature, 1, applier);
            }
            else combat.ApplyWithBeforeApplied<TLoss>(creature, 1, applier,
                value => combat.Apply<TStat>(creature, -value, applier),
                (offset, power) => { if (offset != power.Amount) combat.Apply<TStat>(creature, -offset, applier); });
        }
        finally { combat.CompleteCardPowerApplication(source); }
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat); PauseNativeChoice(simulator);
    }
}
