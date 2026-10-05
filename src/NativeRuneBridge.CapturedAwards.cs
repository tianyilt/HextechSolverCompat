using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeCapturedAwards()
    {
        // Exact source-reviewed types whose callbacks occur at native acquisition,
        // room entry, merchant/deck/reward creation or completed combat victory.
        // Those callbacks stay in the real game. Their resulting HP, powers,
        // cards, forges and granted runes enter the normal root snapshot and
        // must themselves pass the content guard; this does not license grants
        // of arbitrary unimplemented effects. No battle callback is suppressed.
        // Original/source metadata + bodies: 158/158 methods, including bases.
        RuneMirrors.RegisterNativeBase<WizardlyThinkingRune>();
        RuneMirrors.RegisterNativeBase<BlackCandleRune>();
        RuneMirrors.RegisterNativeBase<StatsRune>();
        RuneMirrors.RegisterNativeBase<StatsOnStatsRune>();
        RuneMirrors.RegisterNativeBase<StatsOnStatsOnStatsRune>();
        RuneMirrors.RegisterNativeBase<HundredRefinementsRune>();
        RuneMirrors.RegisterNativeBase<GoodLuckRune>();
        RuneMirrors.RegisterNativeBase<TransmuteGoldRune>();
        RuneMirrors.RegisterNativeBase<TransmutePrismaticRune>();
        RuneMirrors.RegisterNativeBase<TransmuteChaosRune>();
        RuneMirrors.RegisterNativeBase<CrossOrbRune>();
        RuneMirrors.RegisterNativeBase<TezcatarasMercyRune>();
        RuneMirrors.RegisterNativeBase<DiceManiacRune>();
        RuneMirrors.RegisterNativeBase<HailToTheKingRune>();
        RuneMirrors.RegisterNativeBase<TankEngineRune>();
        RuneMirrors.RegisterNativeBase<PandorasBoxRune>();
        RuneMirrors.RegisterNativeBase<UpgradeRune>();
    }
}
