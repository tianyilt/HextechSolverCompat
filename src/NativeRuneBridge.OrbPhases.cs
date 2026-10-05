using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeOrbPhases(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawYourSwordRune), "ShouldReplaceOrbEvoke"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            NativeRelicSite<DrawYourSwordRune>(nameof(NativeCapturedRelic)));
        PatchEventCallback(harmony, AccessTools.PropertyGetter(typeof(DrawYourSwordRune), "HasConflictingFocusConverter"),
            NativeRelicSite<DexterityStrengthToFocusRune>(nameof(NativeCapturedRelic)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawYourSwordRune), "ReplaceOrbEvoke"), SingleNativePowerSite<FocusPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawYourSwordRune), "ApplyConvertedPower"),
            SingleNativePowerSite<StrengthPower>(), SingleNativePowerSite<DexterityPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrawYourSwordRune), "RevertOriginalPower"), SingleNativePowerSite<FocusPower>());
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(DrawYourSwordRune), "ShouldConvert"));
        RegisterNativeReceivedRune<DrawYourSwordRune>();
        var evoke = AccessTools.DeclaredMethod(typeof(OrbMirrors), nameof(OrbMirrors.InvokeEvoke));
        var prefix = AccessTools.Method(typeof(NativeRuneBridge), nameof(ReplaceNativeOrbEvoke));
        harmony.Patch(evoke, prefix: new HarmonyMethod(prefix));
        NativeCallbackContracts.AddNativePrefix(evoke, prefix, "HextechSolverCompat", Priority.Normal);
        foreach (var original in HextechPlayerRuneHooks.FindOrbEvokeMethods())
            NativeCallbackContracts.AddNativePrefix(original, AccessTools.Method(typeof(HextechPlayerRuneHooks), "OrbEvokePrefix"),
                "Natsuki.HextechRunes", Priority.Low);

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MyriadManifestationsRune), "BeforeSideTurnEndEarly"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead), 2),
            Site(AccessTools.PropertyGetter(typeof(OrbQueue), nameof(OrbQueue.Orbs)), nameof(NativeOrbContents), 2),
            Site(AccessTools.DeclaredMethod(typeof(HextechOrbPassiveCompat), "TriggerPassive"), nameof(TriggerNativeOrbPassive)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(MyriadManifestationsRune), "CountOrbTypes"));
        RegisterState<MyriadManifestationsRune>(); RuneMirrors.RegisterNativeBase<MyriadManifestationsRune>();
        BeforeSideTurnEndMirrors.EarlyRegistry.Register<MyriadManifestationsRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeSideTurnEndEarly(new ThrowingPlayerChoiceContext(),
                context.Side, context.Participants)), typeof(MyriadManifestationsRune)));
        // This rune acts before the combat root exists. Normal battle search
        // captures its upgraded dragon-soul cards and their registered powers.
        RegisterStableState<OmniDragonSoulRune>(); RuneMirrors.RegisterNativeBase<OmniDragonSoulRune>();
        foreach (string callback in new[] { "BeforeCombatStart", "AddRandomUpgradedDragonSoulCardsToCombatHand", "RollDistinctDragonSoulCardKinds" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(OmniDragonSoulRune), callback));
    }

    private static bool ReplaceNativeOrbEvoke(CombatPredictionSimulator simulator, OrbModel orb, ref IReadOnlyList<Creature> __result)
    {
        if (orb.GetType().Assembly != typeof(OrbModel).Assembly || orb.Owner is not { } owner
            || ((SimulatedCombatState)simulator.State.CombatState).RelicsOf(owner).OfType<DrawYourSwordRune>().SingleOrDefault() is not { } rune)
            return true;
        if (!Invoke(rune, simulator, model => model.ShouldReplaceOrbEvoke(orb))) return true;
        var result = Invoke(rune, simulator, model => model.ReplaceOrbEvoke());
        if (!result.IsCompletedSuccessfully) throw new InvalidOperationException("Reviewed native evoke replacement suspended.");
        __result = result.Result.ToArray(); return false;
    }

    private static Task TriggerNativeOrbPassive(PlayerChoiceContext context, OrbModel orb)
    {
        if (_simulator is null) return HextechOrbPassiveCompat.TriggerPassive(context, orb);
        _simulator.TriggerOrbPassive(orb, null); PauseNativeChoice(_simulator); return Task.CompletedTask;
    }
}
