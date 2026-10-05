using HarmonyLib;
using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterOutsideBattleEnemyHexes()
    {
        // Pinned official/source comparison: 132/132, including potion-odds
        // and reward-safety patches. These six have no battle transition hooks.
        // Native rewards, maps, potion odds and wax melting remain in the game;
        // a fresh battle root captures their resulting inventory and active IDs.
        var reviewed = new Dictionary<Type, string[]>
        {
            [typeof(CuttingEdgeAlchemistEnemyHex)] = [],
            [typeof(JinlianBoxEnemyHex)] = ["TryModifyCardRewardOptions", "TryModifyCardRewardOptionsLate"],
            [typeof(HastyScribbleEnemyHex)] = ["ModifyGeneratedMapLate"],
            [typeof(PandorasBoxEnemyHex)] = ["ModifyCardRewardCreationOptions"],
            [typeof(TezcatarasMercyEnemyHex)] = ["AfterCombatVictory", "TryModifyRewards"],
            [typeof(ForbiddenGrimoireEnemyHex)] = [],
        };
        foreach (var (type, allowed) in reviewed)
        {
            var overrides = AccessTools.GetDeclaredMethods(type)
                .Where(method => method.IsVirtual && method.GetBaseDefinition().DeclaringType != type
                    && method.Name != "get_Kind").ToArray();
            if (!overrides.Select(method => method.Name).Order().SequenceEqual(allowed.Order()))
                throw new InvalidOperationException($"Reviewed outside-battle enemy callbacks changed: {type.Name}.");
            foreach (var method in overrides) NativeCallbackContracts.Add(method);
        }
        CompatibilityGuard.EnemyHexes.UnionWith([
            MonsterHexKind.CuttingEdgeAlchemist, MonsterHexKind.JinlianBox, MonsterHexKind.HastyScribble,
            MonsterHexKind.PandorasBox, MonsterHexKind.TezcatarasMercy, MonsterHexKind.ForbiddenGrimoire]);
    }
}
