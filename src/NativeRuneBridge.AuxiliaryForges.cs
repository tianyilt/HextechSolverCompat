using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;
internal static partial class NativeRuneBridge
{
    private static void RegisterNativeAuxiliaryForges(Harmony harmony)
    {
        RegisterNativeAuxiliaryForge<StrengthForge>();
        RegisterNativeAuxiliaryForge<DexterityForge>();
        // Still registered for old saves, though absent from the current shop catalogue.
        RegisterNativeAuxiliaryForge<SilverPlatingForge>();
        RegisterNativeAuxiliaryForge<UpgradeForge>();
        RegisterNativeAuxiliaryForge<LifeForge>();
        RegisterNativeAuxiliaryForge<SilverHpForge>();
        RegisterNativeAuxiliaryForge<SilverAttackForge>();
        RegisterNativeAuxiliaryForge<SilverProtectionForge>();
        RegisterNativeAuxiliaryForge<PocketForge>();
        RegisterNativeAuxiliaryForge<PreparedForge>();
        RegisterNativeAuxiliaryForge<SwiftForge>();
        RegisterNativeAuxiliaryForge<FireworksForge>();
        RegisterNativeAuxiliaryForge<VigorForge>();
        RegisterNativeAuxiliaryForge<BlockForge>();
        RegisterNativeAuxiliaryForge<NecrobinderForge>();
        RegisterNativeAuxiliaryForge<SilverStarsForge>();
        RegisterNativeAuxiliaryForge<SilverOrbForge>();
        RegisterNativeAuxiliaryForge<ForgingForge>();
        RegisterNativeAuxiliaryForge<ScissorsForge>();
        RegisterNativeAuxiliaryForge<SteadyForge>();
        RegisterNativeAuxiliaryForge<SilverRecoveryForge>();
        RegisterNativeAuxiliaryForge<ConstitutionForge>();
        RegisterNativeAuxiliaryForge<DisasterForge>();
        RegisterNativeAuxiliaryForge<GoldLifeForge>();
        RegisterNativeAuxiliaryForge<GoldHpForge>();
        RegisterNativeAuxiliaryForge<GoldAttackForge>();
        RegisterNativeAuxiliaryForge<GoldProtectionForge>();
        RegisterNativeAuxiliaryForge<GoldFocusForge>();
        RegisterNativeAuxiliaryForge<DrawForge>();
        RegisterNativeAuxiliaryForge<RecoveryForge>();
        RegisterNativeAuxiliaryForge<HourglassForge>();
        RegisterNativeAuxiliaryForge<GoldUpgradeForge>();
        RegisterNativeAuxiliaryForge<GlamForge>();
        RegisterNativeAuxiliaryForge<SoulsPowerForge>();
        RegisterNativeAuxiliaryForge<MomentumForge>();
        RegisterNativeAuxiliaryForge<SummonForge>();
        RegisterNativeAuxiliaryForge<FleshForge>();
        RegisterNativeAuxiliaryForge<EmbersForge>();
        RegisterNativeAuxiliaryForge<StarsForge>();
        RegisterNativeAuxiliaryForge<OrbSlotForge>();
        RegisterNativeAuxiliaryForge<PlatingForge>();
        RegisterNativeAuxiliaryForge<ThornsForge>();
        RegisterNativeAuxiliaryForge<ArtifactForge>();
        RegisterNativeAuxiliaryForge<VenomForge>();
        RegisterNativeAuxiliaryForge<ShrinkForge>();
        RegisterNativeAuxiliaryForge<GoldScissorsForge>();
        RegisterNativeAuxiliaryForge<PrismaticLifeForge>();
        RegisterNativeAuxiliaryForge<AttackForge>();
        RegisterNativeAuxiliaryForge<ProtectionForge>();
        RegisterNativeAuxiliaryForge<EnergyForge>();
        RegisterNativeAuxiliaryForge<RitualForge>();
        RegisterNativeAuxiliaryForge<RegenForge>();
        RegisterNativeAuxiliaryForge<BufferForge>();
        RegisterNativeAuxiliaryForge<SlipperyForge>();
        RegisterNativeAuxiliaryForge<PrismaticArtifactForge>();
        RegisterNativeAuxiliaryForge<FocusForge>();
        RegisterNativeAuxiliaryForge<FortuneForge>();
        RegisterNativeAuxiliaryForge<SpiralForge>();
        RegisterNativeAuxiliaryForge<VoidForge>();
        var dead = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead));
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        PatchEventCallback(harmony, AccessTools.PropertyGetter(typeof(HextechRelicBase), "IsOwnersFirstTurn"),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber)), nameof(NativeBranchTurnNumber)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(VigorForge), "AfterPlayerTurnStartEarly"),
            Site(dead, nameof(NativeBranchIsDead)), SingleNativePowerSite<VigorPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BlockForge), "AfterPlayerTurnStartEarly"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(RecoveryForge), "AfterPlayerTurnStartEarly"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeHeal, nameof(HealNative)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HourglassForge), "AfterPlayerTurnStartEarly"),
            Site(dead, nameof(NativeBranchIsDead)), Site(combat, nameof(NativeBranchCombat), 2),
            Site(NativeDamageAmount, nameof(DamageNativeAmount)));
        PatchEventCallback(harmony, NativeLambda(typeof(HourglassForge), "AfterPlayerTurnStartEarly"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(StarsForge), "AfterPlayerTurnStartEarly"),
            Site(dead, nameof(NativeBranchIsDead)), Site(AccessTools.Method(typeof(PlayerCmd), "GainStars"), nameof(GainNativeStars)));
        RegisterNativeForgeEarly<VigorForge>(); RegisterNativeForgeEarly<BlockForge>();
        RegisterNativeForgeEarly<RecoveryForge>(); RegisterNativeForgeEarly<HourglassForge>(); RegisterNativeForgeEarly<StarsForge>();
        foreach (var type in new[] { typeof(NecrobinderForge), typeof(SummonForge) })
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterPlayerTurnStart"),
                Site(dead, nameof(NativeBranchIsDead)),
                Site(AccessTools.Method(typeof(OstyCmd), "Summon",
                    [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        RegisterNativeStartCallback<NecrobinderForge>(); RegisterNativeStartCallback<SummonForge>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SilverStarsForge), "AfterSideTurnStart"),
            Site(AccessTools.Method(typeof(PlayerCmd), "GainStars"), nameof(GainNativeStars)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(SilverOrbForge), "AfterSideTurnStart"),
            Site(NativeOrbChannel, nameof(ChannelOrb)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(OrbSlotForge), "AfterSideTurnStart"),
            Site(AccessTools.Method(typeof(OrbCmd), "AddSlots"), nameof(AddNativeOrbSlots)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ForgingForge), "AfterSideTurnStart"),
            Site(AccessTools.Method(typeof(ForgeCmd), "Forge", [typeof(decimal), typeof(Player), typeof(AbstractModel)]), nameof(ForgeNativeBlade)));
        RegisterNativeSideStartCallback<SilverStarsForge>(); RegisterNativeSideStartCallback<SilverOrbForge>();
        RegisterNativeSideStartCallback<OrbSlotForge>(); RegisterNativeSideStartCallback<ForgingForge>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(EnergyForge), "AfterEnergyResetLate"),
            Site(dead, nameof(NativeBranchIsDead)), Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        RegisterNativeQueryCallbacks<SilverAttackForge>(NativeQueries.DamageMultiplier);
        RegisterNativeQueryCallbacks<GoldAttackForge>(NativeQueries.DamageMultiplier);
        RegisterNativeQueryCallbacks<AttackForge>(NativeQueries.DamageMultiplier);
        RegisterNativeSustainCallbacks<SilverProtectionForge>(); RegisterNativeSustainCallbacks<GoldProtectionForge>();
        RegisterNativeSustainCallbacks<ProtectionForge>();
        var grouped = AccessTools.DeclaredMethod(typeof(HextechForgeCoefficientHelper), "GetGroupedMultiplier");
        foreach (var item in new[] {
            (Method: AccessTools.DeclaredMethod(typeof(HextechForgeCoefficientHelper), "GetDamageMultiplier"), Type: typeof(IHextechDamageCoefficientForge)),
            (Method: AccessTools.GetDeclaredMethods(typeof(HextechForgeCoefficientHelper)).Single(m => m.Name == "GetSustainMultiplier" && m.GetParameters().Length == 2), Type: typeof(IHextechSustainCoefficientForge)) })
            PatchEventCallback(harmony, item.Method, new NativeCallSite(grouped.MakeGenericMethod(item.Type),
                AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(NativeForgeGroupedMultiplier)).MakeGenericMethod(item.Type), 1));
        PatchEventCallback(harmony, AccessTools.GetDeclaredMethods(typeof(HextechForgeCoefficientHelper))
            .Single(m => m.Name == "GetSustainMultiplier" && m.GetParameters().Length == 1),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Relics)), nameof(NativeForgeRelics)));
        NativeCallbackContracts.Add(grouped);
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechForgeCoefficientHelper), "CombineBonusFractions"));
        foreach (var type in new[] { typeof(DrawForge), typeof(PreparedForge) })
            RegisterNativeAuxiliaryProjection(harmony, AccessTools.DeclaredMethod(type, "ModifyHandDraw"));
    }
    private static void RegisterNativeAuxiliaryForge<T>() where T : HextechForgeBase
    {
        // Explicit pinned catalogue plus the old-save SilverPlatingForge. Pickup/entry effects remain native
        // and enter the captured root; only in-combat callbacks are bridged.
        if (typeof(T) == typeof(SilverOrbForge)) RegisterStableState<T>();
        else RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(T)).Where(method => method.Name is
            "BeforeCombatStart" or "AfterObtained" or "AfterCombatEnd" or "ModifyHandDraw"))
            NativeCallbackContracts.Add(method);
    }
    private static void RegisterNativeForgeEarly<T>() where T : HextechForgeBase
        => AfterPlayerTurnStartMirrors.RegisterEarly<T>((relic, context) => RequireCompleted(
            Invoke(relic, context.Simulator, model => model.AfterPlayerTurnStartEarly(new ThrowingPlayerChoiceContext(), context.Player)), typeof(T)));
    private static IReadOnlyList<RelicModel> NativeForgeRelics(Player player)
        => _simulator is not { } sim ? player.Relics
            : ((SimulatedCombatState)sim.State.CombatState).RelicsOf(player).Select(relic =>
                relic is HextechForgeBase ? (RelicModel)ModelPredictionStateMirrors.Get<NativeRuneState>(sim, relic).Model : relic).ToArray();
    private static decimal NativeForgeGroupedMultiplier<TForge>(Player player, TForge source, Func<TForge, decimal> getBonusFraction)
        where TForge : class
    {
        // Original first-provider guard and aggregation; only collection and
        // identities are replaced with the detached branch models.
        var forges = NativeForgeRelics(player).OfType<TForge>().ToList();
        if (forges.Count == 0 || !ReferenceEquals(forges[0], source)) return 1m;
        return HextechForgeCoefficientHelper.CombineBonusFractions(forges.Select(getBonusFraction));
    }
    internal static decimal NativeForgeHealing(CombatPredictionSimulator simulator, Player player)
    {
        var previous = _simulator; _simulator = simulator;
        try { return HextechForgeCoefficientHelper.GetSustainMultiplier(player); }
        finally { _simulator = previous; }
    }
    private static void RegisterNativeAuxiliaryProjection(Harmony harmony, MethodInfo target)
    {
        var enter = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(EnterNativeAuxiliaryProjection));
        var leave = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(LeaveNativeAuxiliaryProjection));
        harmony.Patch(target, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
        NativeCallbackContracts.AddScope(target, enter, leave, EventCallSites.ContainsKey(target)
            ? AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)) : null);
    }
    private static void EnterNativeAuxiliaryProjection(out CombatPredictionSimulator? __state)
    {
        __state = _simulator;
        if (_simulator is null && RuneProjectionMirrors.CurrentBranch is { } combat)
            _simulator = CombatSimulators.TryGetValue(combat, out var simulator) ? simulator
                : throw new PredictionUnsupportedException("Auxiliary projection has no captured simulator.");
    }
    private static void LeaveNativeAuxiliaryProjection(CombatPredictionSimulator? __state) => _simulator = __state;
}
