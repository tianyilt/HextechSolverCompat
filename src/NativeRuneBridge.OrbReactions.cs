using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Orb;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeOrbReactions(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(CoreOverloadRune), "AfterOrbEvoked"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            SingleNativePowerSite<FocusPower>());
        RegisterState<CoreOverloadRune>(); RuneMirrors.RegisterNativeBase<CoreOverloadRune>();
        AfterOrbEvokedMirrors.Registry.Register<CoreOverloadRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterOrbEvoked(
                new ThrowingPlayerChoiceContext(), context.Orb, context.Targets)), typeof(CoreOverloadRune)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MadScientistRune), "AfterOrbChanneled"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.AddSlots)), nameof(AddNativeOrbSlots)));
        RegisterNativeChannelReaction<MadScientistRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(OrbSymbiosisRune), "AfterOrbChanneled"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(NativeOrbChannel, nameof(ChannelOrb)));
        RegisterNativeChannelReaction<OrbSymbiosisRune>();
        var target = AccessTools.Method(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.AddOrbSlots));
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeUncappedOrbSlots));
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(target, prefix, "HextechSolverCompat", Priority.Normal);
        NativeCallbackContracts.AddNativePrefix(AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.AddSlots)),
            AccessTools.Method(AccessTools.Inner(typeof(MadScientistRune), "MadScientistPatch"), "Prefix"),
            "Natsuki.HextechRunes", Priority.Low);
        NativeCallbackContracts.AddNativePrefix(AccessTools.Method(typeof(PowerLifecycleSupport), "AfterStarsSpent"),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(DispatchNativeStarsSpent)), "HextechSolverCompat", Priority.Normal);
    }

    private static void RegisterNativeChannelReaction<T>() where T : HextechRelicBase
    {
        RegisterState<T>(); RuneMirrors.RegisterNativeBase<T>();
        AfterOrbChanneledMirrors.Registry.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterOrbChanneled(
                new ThrowingPlayerChoiceContext(), context.Player, context.Orb)), typeof(T)));
    }

    private static bool NativeUncappedOrbSlots(CombatPredictionSimulator __instance, Player player, int amount)
    {
        var combat = (SimulatedCombatState)__instance.State.CombatState;
        if (!combat.RelicsOf(player).OfType<MadScientistRune>().Any()) return true;
        if (!__instance.IsOverOrEnding && amount > 0)
            __instance.State.GetPlayerCombatState(player).OrbQueue.AddCapacity(amount);
        return false;
    }
}
