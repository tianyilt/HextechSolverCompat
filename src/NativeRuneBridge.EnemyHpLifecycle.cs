using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyHpKinds = [MonsterHexKind.TankEngine,
        MonsterHexKind.GoldenSpatula, MonsterHexKind.MadScientist, MonsterHexKind.UnmovableMountain];
    private static readonly Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal> NativeGoldenDamage =
        AccessTools.DeclaredMethod(typeof(GoldenSpatulaEnemyHex), "ModifyDamageMultiplicative")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(new GoldenSpatulaEnemyHex());

    private static void RegisterNativeEnemyHpLifecycle(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TankEngineEnemyHex), "BeforeEnemySideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking), 8),
            Site(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "CaptureMonsterMaxHpCoefficientBase"), nameof(CaptureNativeEnemyHpBase)),
            Site(AccessTools.DeclaredMethod(typeof(HextechMayhemModifier), "ReapplyMonsterMaxHpCoefficients"), nameof(ReapplyNativeEnemyHp)),
            Site(AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "UpdateEnemyScale"), nameof(NativeEnemyBodyScale)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(TankEngineEnemyHex), "RestoreTrackedValue"));
        PatchNativeEnemyTurn<UnmovableMountainEnemyHex>(harmony, "BeforeEnemySideTurnStart", (1, 0, 0, 1, 0));
        foreach (var type in new[] { typeof(GoldenSpatulaEnemyHex), typeof(MadScientistEnemyHex) })
        {
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "GetMaxHpBonusFraction"));
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "ApplyPersistentToEnemy"));
        }
        foreach (string method in new[] { "ModifyDamageMultiplicative", "ModifyBlockMultiplicative", "ModifyEnemyHealMultiplicative" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(GoldenSpatulaEnemyHex), method));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(UnmovableMountainEnemyHex), "ApplyOpeningCombatStartToEnemy"));
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyHpKinds);
    }

    private static int CaptureNativeEnemyHpBase(HextechMayhemModifier modifier, Creature creature, int? basis)
    {
        if (_simulator is null || _nativeEnemyTurn is not { } scope) return modifier.CaptureMonsterMaxHpCoefficientBase(creature, basis);
        return scope.State.Hp.CaptureNativeBase(scope.State, _simulator, creature, basis);
    }
    private static Task ReapplyNativeEnemyHp(HextechMayhemModifier modifier, Creature creature, int? basis)
    {
        if (_simulator is null || _nativeEnemyTurn is not { } scope) return modifier.ReapplyMonsterMaxHpCoefficients(creature, basis);
        scope.State.Hp.CaptureNativeBase(scope.State, _simulator, creature, basis);
        scope.State.Hp.Reapply(scope.State, _simulator, (SimulatedCombatState)_simulator.State.CombatState, creature);
        PauseNativeChoice(_simulator); return Task.CompletedTask;
    }
    private static void NativeEnemyBodyScale(ref HextechEnemyHexContext context, Creature creature)
    { if (_simulator is null) context.UpdateEnemyScale(creature); }

    internal static decimal NativeEnemyGoldenDamage(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? source)
    {
        decimal result = 1m;
        InvokeNativeEnemyReaction(modifier, state, simulator, context =>
        { result = NativeGoldenDamage(context, target, amount, props, dealer, source); return Task.CompletedTask; });
        return result;
    }
}
