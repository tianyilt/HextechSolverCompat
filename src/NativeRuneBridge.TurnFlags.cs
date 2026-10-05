using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeTurnFlags(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HomeguardRune), "AfterPlayerTurnStart"),
            Site(NativeDraw, nameof(Draw)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RepulsorRune), "AfterPlayerTurnStart"),
            SingleNativePowerSite<SlipperyPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SlowCookRune), "AfterPlayerTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeBranchMaxHp)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            SingleNativePowerSite<HextechBurnPower>());
        RegisterNativeTurnFlagRune<HomeguardRune>(true);
        RegisterNativeTurnFlagRune<RepulsorRune>(true);
        RegisterNativeTurnFlagRune<SlowCookRune>(false);
    }

    private static void RegisterNativeTurnFlagRune<T>(bool afterDamage) where T : HextechRelicBase
    {
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        AfterPlayerTurnStartMirrors.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterPlayerTurnStart(
                new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
        if (!afterDamage) return;
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(T), "AfterDamageReceived"));
        AfterDamageReceivedMirrors.Registry.Register<T>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterDamageReceived(
                new ThrowingPlayerChoiceContext(), context.Target, context.Result, context.Props,
                context.Dealer, context.Source?.MutablePreview)), typeof(T)));
    }
}
