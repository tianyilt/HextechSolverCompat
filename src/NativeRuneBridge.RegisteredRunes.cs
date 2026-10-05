using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    // Registration is intentionally independent of selection defaults. Saves,
    // shops and awards can contain disabled catalogue entries. Keep executing
    // their original callbacks on captured branch state and redirect commands.
    private static void RegisterNativeRegisteredRunes(Harmony harmony)
    {
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var hp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
        var max = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EscapePlanRune), "AfterDamageReceived"),
            Site(hp, nameof(NativeEnemyCurrentHp)), Site(max, nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EscapePlanRune), "AfterPlayerTurnStart"),
            Site(max, nameof(NativeBranchMaxHp)), Site(NativeDecimalBlock, nameof(GainNativeBlock)), SingleNativePowerSite<ShrinkPower>());
        RegisterNativeTurnFlagRune<EscapePlanRune>(false);
        RegisterRegisteredDamageReceived<EscapePlanRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DawnbringersResolveRune), "AfterDamageReceived"),
            Site(hp, nameof(NativeEnemyCurrentHp)), Site(max, nameof(NativeBranchMaxHp), 2), SingleNativePowerSite<RegenPower>());
        RegisterState<DawnbringersResolveRune>(); RuneMirrors.RegisterNativeBase<DawnbringersResolveRune>();
        RegisterRegisteredDamageReceived<DawnbringersResolveRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PiggyBankRune), "GrantGold"), Site(NativeGainGold, nameof(GainNativeGold)));
        RegisterState<PiggyBankRune>(); RuneMirrors.RegisterNativeBase<PiggyBankRune>();
        RegisterRegisteredDamageReceived<PiggyBankRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PiggyBankRune), "AfterDamageReceived"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PiggyBankRune), "AfterObtained"));

        // Patch the leaf BEFORE compiling its caller. Otherwise the CLR can
        // inline its original live insertion into the patched state machine.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SoulCallingRune), "AddSoulToPile"),
            Site(AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardsToCombat"), nameof(AddNativeGeneratedCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SoulCallingRune), "BeforeHandDraw"), Site(dead, nameof(NativeBranchIsDead)));
        RegisterBeforeHandDrawRune<SoulCallingRune>();
        RegisterGeneratedBeforeHandDrawRune<NeowsGrudgeRune>(harmony);

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(KakaRune), "BlocksAttack"),
            Site(combat, nameof(NativeBranchCombat)), NativeRelicSite<KakaRune>(nameof(NativeResourceRelic)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(KakaRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechRelicBase), "GetPlayerActNumberForScaling"), nameof(NativeFrozenActNumber)),
            SingleNativePowerSite<RitualPower>());
        RegisterStableState<KakaRune>(); RuneMirrors.RegisterNativeBase<KakaRune>();
        RegisterRegisteredTurn<KakaRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(KakaRune), "ShouldPlay"));
        ShouldPlayMirrors.Registry.Register<KakaRune>((rune, context) => Invoke(rune, context.Simulator,
            model => model.ShouldPlay(context.Card.MutablePreview, context.AutoPlayType)));

        RegisterNativeQueries<AstralBodyRune>(NativeQueries.DamageMultiplier);
        // These acquisition/opening effects have already run before capture.
        // Generated cards and their complete play callbacks are registered by
        // GeneratedTokens; stock powers and transformed cards use the SDK.
        RegisterCapturedOpening<HardBonesRune>();
        RegisterCapturedOpening<GhostFormRune>();
        RegisterState<SuperBrainRune>(); RuneMirrors.RegisterNativeBase<SuperBrainRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SuperBrainRune), "AfterRoomEntered"));
        RegisterState<OkBoomerangRune>(); RuneMirrors.RegisterNativeBase<OkBoomerangRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(OkBoomerangRune), "AfterObtained"));
        RegisterState<PrimitiveMadnessRune>(); RuneMirrors.RegisterNativeBase<PrimitiveMadnessRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PrimitiveMadnessRune), "AfterObtained"));
        RegisterState<RegenerationSuppressionRune>(); RuneMirrors.RegisterNativeBase<RegenerationSuppressionRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(RegenerationSuppressionRune), "NotifyEnemyHealSuppressed"));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PorcupineRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Block)), nameof(NativeBranchBlock)), SingleNativePowerSite<ThornsPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PorcupineRune), "BeforeSideTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<ThornsPower>());
        RegisterState<PorcupineRune>();
        RuneMirrors.RegisterNativeBase<PorcupineRune>(
            beforeTurn: (rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
                model => model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(PorcupineRune)),
            beforeEnd: (rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
                model => model.BeforeTurnEnd(new ThrowingPlayerChoiceContext(), context.Side)), typeof(PorcupineRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(OmegaRune), "BeforeTurnEnd"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat), 3),
            Site(AccessTools.Method(typeof(HextechCombatVfx), "OmegaJudgment"), nameof(RegisteredOmegaVfx)),
            Site(AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait), [typeof(float), typeof(float), typeof(bool), typeof(CancellationToken)]), nameof(QuantumWait)),
            Site(AccessTools.GetDeclaredMethods(typeof(HextechGameApiCompat)).Single(method => method.Name == "Damage"
                && method.GetParameters().Length == 7 && method.GetParameters()[1].ParameterType == typeof(IEnumerable<Creature>)), nameof(NativeDiveDamage)));
        RegisterState<OmegaRune>(); RegisterNativeEndTurn<OmegaRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GetExcitedRune), "AfterPlayerTurnStartEarly"),
            Site(NativeGainEnergy, nameof(GainNativeEnergy)), Site(NativeDraw, nameof(Draw)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(GetExcitedRune), "AfterDeath"),
            Site(AccessTools.Method(typeof(HextechMonsterInteractionPolicy), "IsTrueCombatDeath", [typeof(Creature)]), nameof(FlyingKickTrueDeath)));
        RegisterNativeEarlyTurn<GetExcitedRune>(); RegisterRegisteredDeath<GetExcitedRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SwordFlightRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead)), Site(AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)), nameof(NativeBranchPileCards)),
            Site(NativeDraw, nameof(Draw)));
        RegisterNativeTurnState<SwordFlightRune>(); RegisterAfterCardPlayedCallback<SwordFlightRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SomethingForNothingRune), "AfterCardPlayed"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeDraw, nameof(Draw)),
            Site(AccessTools.Method(typeof(HextechCombatHooks), "GetEnergyCostForCurrentCardPlay"), nameof(NativeMissileEnergy)));
        RegisterNativeTurnState<SomethingForNothingRune>(); RegisterAfterCardPlayedCallback<SomethingForNothingRune>();
        foreach (var name in new[] { "IsZeroCostPlay", "ReduceCost" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(SomethingForNothingRune), name));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EarthAwakensRune), "ApplyRollingBoulderPower"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<RollingBoulderPower>());
        foreach (var name in new[] { "AfterPlayerTurnStart", "AfterPlayerTurnStartLate" })
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EarthAwakensRune), name), Site(dead, nameof(NativeBranchIsDead)));
        RegisterNativeTurnFlagRune<EarthAwakensRune>(false);
        AfterPlayerTurnStartMirrors.RegisterLate<EarthAwakensRune>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterPlayerTurnStartLate(new ThrowingPlayerChoiceContext(), context.Player)), typeof(EarthAwakensRune)));

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(FeyMagicRune), "AfterDamageGiven"),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Stun), [typeof(Creature), typeof(string)]), nameof(StunNativeCreature)));
        RegisterNativeDamageHook<FeyMagicRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DieForYouRune), "AfterPlayerTurnStart"),
            Site(dead, nameof(NativeBranchIsDead)),
            Site(AccessTools.Method(typeof(OstyCmd), nameof(OstyCmd.Summon), [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DieForYouRune), "AfterDeath"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat)), Site(max, nameof(NativeBranchMaxHp)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DieForYouRune), "AddPendingWishCard"),
            Site(combat, nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), nameof(NativeBranchProgress)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)), nameof(NativeBranchEnding)),
            Site(AccessTools.Method(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        RegisterNativeTurnFlagRune<DieForYouRune>(false); RegisterRegisteredDeath<DieForYouRune>();
        RegisterNativeRegisteredTransfers(harmony);
        RegisterNativePlayerVakuu(harmony);
        RegisterNativePlayerNature(harmony);
        RegisterNativeCardInspection(harmony);
    }

    private static void RegisterRegisteredTurn<T>() where T : HextechRelicBase
        => AfterPlayerTurnStartMirrors.Register<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));

    private static void RegisterRegisteredDamageReceived<T>() where T : HextechRelicBase
        => AfterDamageReceivedMirrors.Registry.Register<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterDamageReceived(new ThrowingPlayerChoiceContext(), context.Target, context.Result,
                context.Props, context.Dealer, context.Source?.MutablePreview)), typeof(T)));

    private static void RegisterRegisteredDeath<T>() where T : HextechRelicBase
        => AfterDeathMirrors.Registry.Register<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.AfterDeath(new ThrowingPlayerChoiceContext(), context.Creature, context.WasRemovalPrevented, 0f)), typeof(T)));

    private static void RegisteredOmegaVfx(IReadOnlyList<Creature> enemies)
    { if (_simulator is null) HextechCombatVfx.OmegaJudgment(enemies); }
}
