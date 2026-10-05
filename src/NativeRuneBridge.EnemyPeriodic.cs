using System.Reflection;
using CombatSolver;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativePeriodicKinds =
        [MonsterHexKind.DivineIntervention, MonsterHexKind.Cerberus, MonsterHexKind.LeafSlime,
         MonsterHexKind.SlimedBerserker, MonsterHexKind.Myte, MonsterHexKind.HauntedShip,
         MonsterHexKind.Omega, MonsterHexKind.LagavulinMatriarch, MonsterHexKind.FrostWraith, MonsterHexKind.Doomsday];
    internal static bool HasNativePeriodicHex(HextechMayhemModifier modifier)
        => modifier.HasActiveMonsterHex(MonsterHexKind.Tormentor) || NativePeriodicKinds.Any(modifier.HasActiveMonsterHex)
            || NativeHealthThresholdKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyDrawKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyPlayedKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyHitMemoryKinds.Any(modifier.HasActiveMonsterHex)
            || modifier.HasActiveMonsterHex(MonsterHexKind.BloodIdol)
            || modifier.HasActiveMonsterHex(MonsterHexKind.OmniDragonSoul)
            || NativeEnemyCardFlowKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyHpKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyHitEffectKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyPileKinds.Any(modifier.HasActiveMonsterHex)
            || NativeEnemyFlowLimitKinds.Any(modifier.HasActiveMonsterHex)
            || modifier.HasActiveMonsterHex(MonsterHexKind.NearDeathFeast)
            || modifier.HasActiveMonsterHex(MonsterHexKind.ServantMaster)
            || modifier.HasActiveMonsterHex(MonsterHexKind.Mystery)
            || modifier.HasActiveMonsterHex(MonsterHexKind.ShoulderVaku)
            || modifier.HasActiveMonsterHex(MonsterHexKind.ThievingHopper);
    private delegate HextechMayhemCombatTrackingState ContextTrackingGetter(ref HextechEnemyHexContext context);
    private static readonly ContextTrackingGetter OriginalContextTracking =
        AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking").CreateDelegate<ContextTrackingGetter>();

    private static void RegisterNativeEnemyPeriodicFamily(Harmony harmony)
    {
        var tracking = AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking");
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechEnemyHexContext), "TryConsumeOncePerRound"),
            Site(tracking, nameof(NativeCapturedTracking)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechEnemyHexContext), "TryConsumeRoundInterval"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRoundInterval), "IsDue"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechEnemyHexContext), "GetAlivePlayersByNetId"));
        var playerPredicate = typeof(HextechEnemyHexContext).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(nested => AccessTools.GetDeclaredMethods(nested))
            .Single(method => method.Name.StartsWith("<GetAlivePlayersByNetId>b__", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool) && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(Creature));
        PatchEventCallback(harmony, playerPredicate,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)));
        var enemies = AccessTools.Method(typeof(HextechEnemyHexContext), "GetAliveEnemies");
        var apply = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat))
            .Single(method => method.Name == "Apply" && method.IsGenericMethodDefinition
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                    new[] { typeof(IEnumerable<Creature>), typeof(decimal), typeof(Creature),
                        typeof(MegaCrit.Sts2.Core.Models.CardModel), typeof(bool) }));
        NativeCallSite Apply<T>() where T : MegaCrit.Sts2.Core.Models.PowerModel
            => new(apply.MakeGenericMethod(typeof(T)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerMany)).MakeGenericMethod(typeof(T)), 1);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DivineInterventionEnemyHex), "BeforePlayerSideTurnStart"),
            Site(enemies, nameof(NativeContextAliveEnemies)), Apply<IntangiblePower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CerberusEnemyHex), "BeforePlayerSideTurnStart"),
            Site(enemies, nameof(NativeContextAliveEnemies)), Apply<VigorPower>());
        var generation = AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat");
        foreach (var type in new[] { typeof(LeafSlimeEnemyHex), typeof(SlimedBerserkerEnemyHex), typeof(MyteEnemyHex), typeof(HauntedShipEnemyHex) })
        {
            var sites = new List<NativeCallSite> { Site(generation, nameof(AddNativeGeneratedCard)) };
            if (type == typeof(HauntedShipEnemyHex))
                sites.Add(Site(tracking, nameof(NativeCapturedTracking)));
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "BeforePlayerSideTurnStart"), sites.ToArray());
        }
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechCombatProcTracker), "ConsumeGlobalProcInCombat"));
        CompatibilityGuard.EnemyHexes.UnionWith(NativePeriodicKinds);
    }

    private static HextechMayhemCombatTrackingState NativeCapturedTracking(ref HextechEnemyHexContext context)
        => _simulator is not null && _nativeEnemyTurn is { } scope
            ? scope.State.NativeTurns?.Model ?? throw new InvalidOperationException("Native periodic counter state was not captured.")
            : OriginalContextTracking(ref context);

}
