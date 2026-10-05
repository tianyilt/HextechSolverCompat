using HarmonyLib;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly TormentorEnemyHex NativeEnemyTormentor = new();
    private static readonly FirebrandEnemyHex NativeEnemyFirebrand = new();
    private static readonly Func<HextechEnemyHexContext, Creature, Task> NativeEnemyTormentorCallback =
        AccessTools.DeclaredMethod(typeof(TormentorEnemyHex), "AfterEnemyDebuffReceived")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, Task>>(NativeEnemyTormentor);
    private static readonly Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task> NativeEnemyFirebrandCallback =
        AccessTools.DeclaredMethod(typeof(FirebrandEnemyHex), "AfterEnemyDamageGivenImmediate")
            .CreateDelegate<Func<HextechEnemyHexContext, Creature, DamageResult, Creature, CardModel?, Task>>(NativeEnemyFirebrand);
    private static void RegisterNativeBurnReactions(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FirebrandRune), "AfterDamageGiven"),
            SingleNativePowerSite<HextechBurnPower>());
        RegisterNativeDamageHook<FirebrandRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SpeedDemonRune), "AfterDamageGiven"),
            Site(NativeDraw, nameof(Draw)));
        RegisterNativeDamageHook<SpeedDemonRune>(resetBeforeTurn: true);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TormentorEnemyHex), "AfterEnemyDebuffReceived"),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking), 4),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            SingleNativePowerSite<HextechBurnPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FirebrandEnemyHex), "AfterEnemyDamageGivenImmediate"),
            SingleNativePowerSite<HextechBurnPower>());
        CompatibilityGuard.EnemyHexes.UnionWith([MonsterHexKind.Tormentor, MonsterHexKind.Firebrand]);
    }
    internal static void NativeEnemyTormentorChanged(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature target)
        => InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyTormentorCallback(context, target));
    internal static void NativeEnemyFirebrandDamage(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Creature dealer, DamageResult result, Creature target, CardModel? source)
        => InvokeNativeEnemyReaction(modifier, state, simulator,
            context => NativeEnemyFirebrandCallback(context, dealer, result, target, source));
    private static void InvokeNativeEnemyReaction(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, Func<HextechEnemyHexContext, Task> callback)
    {
        var previous = _simulator;
        var previousTurn = _nativeEnemyTurn;
        _simulator = simulator; _nativeEnemyTurn = new(modifier, state);
        try { RequireCompleted(callback(new HextechEnemyHexContext(modifier)), typeof(HextechMayhemModifier)); }
        finally { _simulator = previous; _nativeEnemyTurn = previousTurn; }
    }
}
