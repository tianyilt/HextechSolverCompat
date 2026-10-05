using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [ThreadStatic] private static Creature? _nativePotionTarget;
    private delegate bool NativeScaledMaxHpPrefix(Creature creature, ref decimal amount, out bool entered);
    private static readonly NativeScaledMaxHpPrefix OriginalScaledMaxHpPrefix =
        AccessTools.Method(AccessTools.Inner(typeof(HextechCombatHooks), "SetMaxHpPatch"), "Prefix")
            .CreateDelegate<NativeScaledMaxHpPrefix>();

    private static void RegisterNativeStackGrowth(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.Method(typeof(HextechMaxHpScaling), "ReapplyScale"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp), 2),
            Site(AccessTools.Method(typeof(CreatureCmdCompat), "SetMaxHp"), nameof(SetNativeScaledMaxHp)),
            Site(NativeHeal, nameof(HealNative)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechMaxHpScaling), "EnsureScaledBaseInitialized"));
        NativeCallbackContracts.Add(AccessTools.Method(AccessTools.Inner(typeof(HextechCombatHooks), "SetMaxHpPatch"), "Prefix"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeedUpgradeRune), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead), 2));
        PatchEventCallback(harmony, AccessTools.Method(typeof(FeedUpgradeRune), "MigrateLegacyStackCount"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp), 2));
        RegisterState<FeedUpgradeRune>(); RuneMirrors.RegisterNativeBase<FeedUpgradeRune>();
        RegisterAfterCardPlayedCallback<FeedUpgradeRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NineDragonPowerRune), "BeforeCombatStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            SingleNativePowerSite<RegenPower>());
        PatchEventCallback(harmony, AccessTools.Method(typeof(NineDragonPowerRune), "Grow"),
            Site(AccessTools.Method(typeof(HextechPlayerBodyScaleHelper), "Update"), nameof(NativeStackGrowthVisual)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NineDragonPowerRune), "AfterPotionUsed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(NineDragonPowerRune), "get_SustainMultiplier"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(NineDragonPowerRune), "get_MaxHpScale"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(FeedUpgradeRune), "get_MaxHpScale"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRelicBase), "IsPotionUseOwnedByOrTargetingOwner"));
        RegisterState<NineDragonPowerRune>(); RuneMirrors.RegisterNativeBase<NineDragonPowerRune>();

        var used = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.AfterPotionUsed));
        var enter = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativePotionTargetEnter));
        var exit = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativePotionTargetExit));
        harmony.Patch(used, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(exit));
        NativeCallbackContracts.AddScope(used, enter, exit);
        var dispatch = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.TriggerRelicsAfterPotionUsed));
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativePotionCallbacks));
        harmony.Patch(dispatch, transpiler: new HarmonyMethod(rewrite)); NativeCallbackContracts.Add(dispatch, rewrite);
    }
    private static void NativePotionTargetEnter(Creature? target, out Creature? __state)
    { __state = _nativePotionTarget; _nativePotionTarget = target; }
    private static void NativePotionTargetExit(Creature? __state) => _nativePotionTarget = __state;
    private static IEnumerable<CodeInstruction> RewriteNativePotionCallbacks(IEnumerable<CodeInstruction> instructions)
    {
        int sites = 0;
        var current = AccessTools.PropertyGetter(typeof(IEnumerator<RelicModel>), "Current");
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!instruction.Calls(current)) continue;
            sites++;
            yield return new CodeInstruction(OpCodes.Dup);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldarg_2);
            yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(DispatchNativePotionRune));
        }
        if (sites != 1) throw new InvalidOperationException($"Pinned potion listener iteration changed: {sites}.");
    }
    private static void DispatchNativePotionRune(RelicModel relic, SimulatedCombatState combat,
        CombatPredictionSimulator simulator, PotionModel potion)
    {
        if (relic is NineDragonPowerRune rune)
            RequireCompleted(Invoke(rune, simulator, native => native.AfterPotionUsed(potion, _nativePotionTarget)), typeof(NineDragonPowerRune));
    }
    private static void NativeStackGrowthVisual(Player player)
    { if (_simulator is null) HextechPlayerBodyScaleHelper.Update(player); }
    private static System.Threading.Tasks.Task SetNativeScaledMaxHp(Creature creature, decimal amount)
    {
        if (_simulator is not { } sim) return CreatureCmdCompat.SetMaxHp(creature, amount);
        bool entered = false;
        try
        {
            if (!OriginalScaledMaxHpPrefix(creature, ref amount, out entered))
                throw new PredictionUnsupportedException("Original scaled SetMaxHp unexpectedly suppressed the command.");
            sim.State.GetCreature(creature).SetMaxHp(Math.Max(1, (int)amount));
            return Task.CompletedTask;
        }
        finally { if (entered) NativeMaxHpGuard.Exit(); }
    }
}
