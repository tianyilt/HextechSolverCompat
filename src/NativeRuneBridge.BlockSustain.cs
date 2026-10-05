using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeBlockSustain(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PowerShieldRune), "AfterBlockGained"),
            Site(AccessTools.DeclaredMethod(typeof(HextechRelicBase), "GetPlayerActNumberForScaling"), nameof(NativeFrozenActNumber)),
            SingleNativePowerSite<HextechPowerShieldTemporaryStrengthPower>());
        RegisterStableState<PowerShieldRune>();
        RuneMirrors.RegisterNativeBase<PowerShieldRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.BeforeSideTurnStart(
                new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(PowerShieldRune)));
        AfterBlockGainedMirrors.Registry.Register<PowerShieldRune>((rune, context) => RequireCompleted(
            Invoke(rune, context.Simulator, model => model.AfterBlockGained(
                context.Creature, context.Amount, context.Props, context.Source?.MutablePreview)), typeof(PowerShieldRune)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PowerShieldRune), "ResetTurnScopedState"));
        CompatibilityGuard.Powers.Add(typeof(HextechPowerShieldTemporaryStrengthPower));
        RegisterState<AllForYouRune>(); RuneMirrors.RegisterNativeBase<AllForYouRune>();
        RegisterNativeSustainCallbacks<AllForYouRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(AllForYouRune), "ModifyBlockMultiplicative"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AllForYouRune), "CountAliveHolders"),
            Site(AccessTools.PropertyGetter(typeof(IPlayerCollection), nameof(IPlayerCollection.Players)), nameof(NativeFrozenTeamPlayers)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            NativeRelicSite<AllForYouRune>(nameof(NativeResourceRelic)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(AllForYouRune), "GetTeamHealingMultiplier"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(AllForYouRune), "Pow"));
        RegisterState<OurHealingRune>(); RuneMirrors.RegisterNativeBase<OurHealingRune>();
        // Capture validation already enforces one player. The native share-heal
        // callback then has an empty teammate list and no gameplay writes.
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(OurHealingRune), "ShareHolderHeal"));
    }

    private static int NativeFrozenActNumber(HextechRelicBase rune)
        => _simulator is null ? HextechPlayerContextHelper.GetActNumberForScaling(rune.Owner)
            : (_stableGeneration ?? throw new PredictionUnsupportedException("Native act query has no frozen run metadata.")).ScalingAct;
    private static IReadOnlyList<Player> NativeFrozenTeamPlayers(IPlayerCollection run)
        => _simulator?.State.CombatState.Players ?? run.Players;
    internal static decimal NativeTeamHealing(AllForYouRune rune, CombatPredictionSimulator simulator, Player player)
        => Invoke(rune, simulator, _ => AllForYouRune.GetTeamHealingMultiplier(player));
}
