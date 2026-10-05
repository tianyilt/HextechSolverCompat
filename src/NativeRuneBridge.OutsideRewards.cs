using HarmonyLib;
using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeOutsideRewards()
    {
        // Official/source 251/251 plus reward-safety hooks 76/76. Reward/event/shop
        // transactions stay in the native game; the direct command gates explicitly
        // exclude active combat. No reward queue is read or mutated by prediction.
        var callbacks = AccessTools.GetDeclaredMethods(typeof(DoubleVisionRune))
            .Where(method => method.IsVirtual && method.GetBaseDefinition().DeclaringType != method.DeclaringType)
            .Select(method => method.Name).Order().ToArray();
        if (!callbacks.SequenceEqual(new[] { "AfterCombatEnd", "AfterRewardTaken", "AfterRoomEntered" }))
            throw new InvalidOperationException("DoubleVision outside-combat callback contract changed: " + string.Join(",", callbacks));
        RuneMirrors.RegisterNativeBase<DoubleVisionRune>();
        foreach (string name in callbacks.Concat(new[] { "BeginDirectCommandReward", "ShouldDuplicateDirectDeckCard" }))
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DoubleVisionRune), name));

        // Source 18/18 review included Hail. It only installs stock powers at real
        // Elite/Boss entry; their ordinary power lifecycle is captured by the root.
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HailToTheKingEnemyHex), "ApplyCombatStartToEnemy"));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.HailToTheKing);
    }
}
