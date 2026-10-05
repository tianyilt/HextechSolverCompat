using HextechRunes;

namespace HextechSolverCompat;

internal static partial class RuneMirrors
{
    private static void RegisterOpeningEffects()
    {
        // These exact implementations only install vanilla powers before the
        // in-combat root. The root captures their amounts; the solver owns their
        // subsequent lifecycle. This is not a blanket exemption for opening runes.
        RegisterBase<OverlordBloodArmorRune>();
        RegisterBase<BadgeBrothersRune>();
        RegisterBase<SwordsmanshipRune>();
        RegisterBase<EasyDoesItRune>();
        RegisterBase<MonarchsGazeRune>();
        RegisterBase<SymphonyOfWarRune>();
        RegisterBase<UnmovableMountainRune>();
        RegisterBase<ForbiddenGrimoireRune>();
        RegisterBase<MysteryRune>();
    }
}
