using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class ConditionalCardPatches
{
    internal static void Register(Harmony harmony)
    {
        RegisterRitsuHandSizeContract(harmony);
        RegisterNativeOutbreak();
        // These exact pinned prefixes return true without changing state when
        // their owner lacks the corresponding rune. They are installed globally,
        // so even a normal Silent starting deck would otherwise be rejected.
        RegisterConditional<Survivor, SurvivorUpgradeRune>("SurvivorPatch", "hextech-inactive-upgrade-v1");
        RegisterConditional<BodySlam, BodySlamUpgradeRune>("BodySlamPatch", "hextech-body-slam-upgrade-v2");
        RegisterConditional<SovereignBlade, BigKnifeRune>("SovereignBladeOnPlayPatch", "hextech-inactive-big-knife-v1");
        // Generation can reach these types even when none occur in the deck.
        // Freeze every reviewed conditional composition at root capture, rather
        // than discovering a globally patched card after a worker generates it.
        RegisterConditional<Neurosurge, NeurosurgeUpgradeRune>("OnPlayPatch", "hextech-native-neurosurge-v2");
        RegisterConditional<Hang, HangUpgradeRune>("HangAllDamagePatch", "hextech-native-hang-v2");
        RegisterConditional<HiddenGem, HiddenGemUpgradeRune>("HiddenGemPatch", "hextech-inactive-hidden-gem-v1");
        RegisterConditional<Voltaic, VoltaicUpgradeRune>("VoltaicPatch", "hextech-inactive-voltaic-v1");
        RegisterConditional<CrashLanding, CrashLandingUpgradeRune>("CrashLandingPatch", "hextech-native-crash-landing-v2");
        RegisterConditional<GrandFinale, GrandFinaleUpgradeRune>("GrandFinalePatch", "hextech-native-grand-finale-v2");
        RegisterConditional<Compact, CompactUpgradeRune>("CompactPatch", "hextech-inactive-compact-v1");
        RegisterConditional<FlakCannon, FlakCannonUpgradeRune>("FlakCannonPlayPatch", "hextech-inactive-flak-cannon-v2");
        RegisterConditional<Jackpot, JackpotUpgradeRune>("JackpotPatch", "hextech-inactive-jackpot-v1");
        RegisterConditional<WroughtInWar, WroughtInWarUpgradeRune>("WroughtInWarPatch", "hextech-native-wrought-in-war-v2");
        RegisterConditional<DecisionsDecisions, DecisionsDecisionsUpgradeRune>("DecisionsDecisionsOnPlayPatch", "hextech-inactive-decisions-v1");
        RegisterConditional<Nightmare, NightmareUpgradeRune>("ImmediateNightmarePatch", "hextech-inactive-nightmare-v1", prefixAndPostfix: true);
        RegisterConditional<BladeOfInk, InkshadowRune>("BladeOfInkPatch", "hextech-inactive-inkshadow-v1");
    }

    private static void RegisterNativeOutbreak()
    {
        var type = AccessTools.Inner(typeof(HextechCombatHooks), "OutbreakPatch");
        var patches = new[]
        {
            new AdaptedOnPlayPatch(HarmonyPatchType.Prefix, AccessTools.Method(type, "Prefix"), "Natsuki.HextechRunes", Priority.Normal, [], []),
            new AdaptedOnPlayPatch(HarmonyPatchType.Postfix, AccessTools.Method(type, "Postfix"), "Natsuki.HextechRunes", Priority.Normal, [], []),
            new AdaptedOnPlayPatch(HarmonyPatchType.Finalizer, AccessTools.Method(type, "Finalizer"), "Natsuki.HextechRunes", Priority.Normal, [], []),
        };
        AdaptedCardOnPlayMirrors.Register<Outbreak>("hextech-native-outbreak-response-guard-v1",
            AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(Outbreak))!, patches,
            (_, context) => NativeRuneBridge.RunNativeOutbreakBody(context));
    }

    private static void RegisterConditional<TCard, TRune>(string nestedPatch, string schema, bool prefixAndPostfix = false)
        where TCard : CardModel where TRune : HextechRelicBase
    {
        var patchType = AccessTools.Inner(typeof(TRune), nestedPatch)
            ?? throw new InvalidOperationException($"Missing reviewed patch {typeof(TRune).Name}.{nestedPatch}");
        var prefix = AccessTools.Method(patchType, "Prefix");
        var target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(TCard))!;
        AdaptedOnPlayPatch[] patches = prefixAndPostfix
            ? [new(HarmonyPatchType.Prefix, prefix, "Natsuki.HextechRunes", Priority.Normal, [], []),
               new(HarmonyPatchType.Postfix, AccessTools.Method(patchType, "Postfix"), "Natsuki.HextechRunes", Priority.Normal, [], [])]
            : [new(HarmonyPatchType.Prefix, prefix, "Natsuki.HextechRunes", Priority.Low, [], [])];
        AdaptedCardOnPlayMirrors.Register<TCard>(schema, target,
            patches,
            (card, context) =>
            {
                var combat = (SimulatedCombatState)context.CombatState;
                if (combat.RelicsOf(card.Owner).OfType<TRune>().SingleOrDefault() is TRune rune)
                {
                    if (rune is BodySlamUpgradeRune bodySlam)
                    {
                        NativeRuneBridge.PlayBodySlam(bodySlam, context);
                        CardOnPlayMirrors.ApplyRemainingCardSpec(context.Simulator, context.Card, context.CardPlay.Target);
                        return;
                    }
                    // These helpers replace the complete native OnPlay body.
                    // Running the vanilla compensation afterward would also
                    // apply the original Hang debuff/Forge/etc a second time.
                    if (NativeRuneBridge.TryPlayConditionalRune(rune, context)) return;
                    throw new PredictionUnsupportedException($"{typeof(TRune).Name} has active upgraded OnPlay semantics that are not implemented yet.");
                }
                // Follow the ordinary OnPlay path, including its continuation
                // after a selection. Calling Invoke would recursively re-enter
                // this adapted composition; calling native OnPlay would mutate
                // live state. This invokes only the solver's vanilla registry.
                CardOnPlayMirrors.Registry.Invoke(card, context);
                if (context.Simulator.HasPendingChoice)
                    context.Simulator.AppendExecutionContinuation(new CardOnPlayMirrors.CardSpecExecutionFrame(context.Card, context.CardPlay.Target));
                else
                    CardOnPlayMirrors.ApplyRemainingCardSpec(context.Simulator, context.Card, context.CardPlay.Target);
            });
    }
}
