using HextechRunes;

namespace HextechSolverCompat;

internal static partial class RuneMirrors
{
    private static void RegisterAcquisitionOnly()
    {
        // These exact types only change the run loadout when obtained. The
        // resulting cards/relics and current creature stats are captured by the
        // normal solver snapshot; their acquisition must not run again while
        // predicting a combat. Source review includes references outside each
        // class, and the official method bodies are checked separately.
        RegisterBase<AttackDefenseUnityRune>();
        RegisterBase<DonationRune>();
        RegisterBase<OrobasBlessingRune>();
        RegisterBase<PortableSleepingBagRune>();
        RegisterBase<MirrorReflectionRune>();
        RegisterBase<GoldCardCustomerRune>();
        RegisterBase<MobileHomeRune>();
        RegisterBase<JinlianBoxRune>();
        RegisterBase<BarbarianWayRune>();
        RegisterBase<ExtremeSpeedRune>();
        // Its nested patch only replaces treasure-room rewards; it installs no
        // combat hook and must remain native outside combat.
        RegisterBase<PrismaticEggRune>();
        // These grant vanilla ancient relics once. Their resulting loadout is
        // captured normally; neither type has combat callbacks or external
        // gameplay patches referring to it.
        RegisterBase<NonupeipeGenerosityRune>();
        RegisterBase<VakuuMockeryRune>();
        // Reviewed run/reward/rest-site effects. They remain native outside the
        // current combat; do not replay room entry or reward generation in a
        // search branch. RedEnvelope's saved scalar is captured separately.
        RegisterBase<HattrickRune>();
        RegisterBase<CarefulSelectionRune>();
        RegisterBase<StokeRune>();
        RegisterBase<RedEnvelopeRune>();
        RegisterBase<EndlessRecoveryRune>();
        RegisterBase<CuttingEdgeAlchemistRune>();
        RegisterBase<WatchOutGrapefruitRune>();
    }
}
