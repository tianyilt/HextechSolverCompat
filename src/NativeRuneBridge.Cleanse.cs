using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly HashSet<Type> VisualOnlyRemovalTypes =
        [typeof(DemonFormPower), typeof(EchoFormPower), typeof(ReaperFormPower), typeof(SerpentFormPower),
         typeof(VoidFormPower), typeof(ShrinkPower), typeof(SlumberPower)];
    private static readonly HashSet<Type> NativeRemovalTypes =
        [typeof(BurrowedPower), typeof(DampenPower), typeof(HexPower), typeof(RingingPower), typeof(TangledPower), typeof(VitalSparkPower)];

    private static void RegisterNativeCleanse(Harmony harmony)
    {
        var allCards = AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards));
        var clear = AccessTools.Method(typeof(CardCmd), nameof(CardCmd.ClearAffliction));
        foreach (Type type in new[] { typeof(HexPower), typeof(RingingPower), typeof(TangledPower), typeof(VitalSparkPower) })
        {
            var sites = new List<NativeCallSite> { Site(allCards, nameof(NativeRemovalAllCards)), Site(clear, nameof(ClearNativeAffliction)) };
            if (type == typeof(VitalSparkPower))
                sites.Add(Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2));
            PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, nameof(PowerModel.AfterRemoved)), sites.ToArray());
        }
        var refresh = AccessTools.Method(typeof(HextechVitalSparkCompatibilityHooks), "RefreshAfterNative");
        var getHexVital = AccessTools.GetDeclaredMethods(typeof(Creature)).Single(method => method.Name == "GetPower"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 0).MakeGenericMethod(typeof(HextechVitalSparkPower));
        PatchEventCallback(harmony, refresh,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            new(getHexVital, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalCapturedPower)).MakeGenericMethod(typeof(HextechVitalSparkPower)), 1));
        var removedPostfix = AccessTools.Method(AccessTools.Inner(typeof(HextechVitalSparkCompatibilityHooks), "RemovedPatch"), "Postfix");
        NativeCallbackContracts.Add(removedPostfix);
        NativeCallbackContracts.AddNativePostfix(AccessTools.DeclaredMethod(typeof(VitalSparkPower), nameof(PowerModel.AfterRemoved)),
            removedPostfix, "Natsuki.HextechRunes", Priority.Normal, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteEventCallSites)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BurrowedPower), nameof(PowerModel.AfterRemoved)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.LoseBlock),
                [typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(Creature)]), nameof(LoseNativeBlock)));
        var dampenData = AccessTools.GetDeclaredMethods(typeof(PowerModel)).Single(method => method.Name == "GetInternalData" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(DampenPower.Data));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DampenPower), nameof(PowerModel.AfterRemoved)),
            new(dampenData, AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeRemovalDampenData)), 1),
            Site(AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Upgrade), [typeof(CardModel), typeof(CardPreviewStyle)]), nameof(UpgradeNativeGeneratedCard)));
        foreach (Type type in VisualOnlyRemovalTypes.Append(typeof(SwordSagePower)).Append(typeof(SandpitPower)))
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, nameof(PowerModel.AfterRemoved)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(PowerModel), nameof(PowerModel.AfterRemoved)));
        var powers = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.Powers));
        var vfx = AccessTools.Method(typeof(HextechMikaelsBlessingVfx), "Play");
        var remove = AccessTools.Method(typeof(HextechPowerCmdCompat), "Remove", [typeof(PowerModel)]);
        RegisterNativePowerCard<MikaelsBlessingCard>(harmony,
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Runs.IPlayerCollection), "Players"), nameof(NativeFrozenTeamPlayers)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp)), nameof(NativeEnemyMaxHp)),
            Site(NativeHeal, nameof(HealNative)), Site(powers, nameof(NativeRemovalPowers)),
            Site(vfx, nameof(NativeCleanseVfx)), Site(remove, nameof(RemoveNativePower)));
        PatchNativeAlivePredicate<MikaelsBlessingCard>(harmony, nameof(Creature.IsDead), nameof(NativeBranchIsDead));
        RegisterNativePowerCard<FeelTheBurnCard>(harmony,
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2),
            Site(powers, nameof(NativeRemovalPowers)),
            Site(AccessTools.Method(typeof(HextechMonsterInteractionPolicy), "RemoveMonsterBuffSafely"), nameof(RemoveNativeMonsterBuff)),
            ManyNativePowerSite<HextechBurnPower>());
        PatchNativeAlivePredicate<FeelTheBurnCard>(harmony, nameof(Creature.IsAlive), nameof(NativeBranchIsAlive));
        foreach (Type type in new[] { typeof(MikaelsBlessingRune), typeof(FeelTheBurnRune) })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, "AfterObtained"));
        RuneMirrors.RegisterNativeBase<MikaelsBlessingRune>();
        RuneMirrors.RegisterNativeBase<FeelTheBurnRune>();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ExposeUpgradeRune), "AfterCardPlayed"),
            Site(powers, nameof(NativeRemovalPowers)),
            Site(AccessTools.Method(typeof(HextechMonsterInteractionPolicy), "RemoveMonsterBuffSafely"), nameof(RemoveNativeMonsterBuff)));
        RegisterAfterCardPlayed<ExposeUpgradeRune>();
        foreach (string method in new[] { "ShouldPreserveFromBuffRemoval", "IsStructuralMonsterBuff", "IsEnemyHostedPlayerRelationBuff", "RemoveMonsterBuffSafely" })
            NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechMonsterInteractionPolicy), method));
        NativeCallbackContracts.Add(vfx);
    }

    private static void PatchNativeAlivePredicate<T>(Harmony harmony, string property, string replacement)
    {
        var candidates = typeof(T).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Where(method => method.Name.StartsWith("<OnPlay>b__", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool) && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(Creature)).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Native cleanse alive predicate changed.");
        PatchEventCallback(harmony, candidates[0], Site(AccessTools.PropertyGetter(typeof(Creature), property), replacement));
    }

    private static T? NativeRemovalCapturedPower<T>(Creature creature) where T : PowerModel
        => _simulator is { } sim ? ((SimulatedCombatState)sim.State.CombatState).GetPower<T>(creature) : creature.GetPower<T>();
    private static IReadOnlyList<PowerModel> NativeRemovalPowers(Creature creature)
        => _simulator is { } sim
            ? ((SimulatedCombatState)sim.State.CombatState).EffectivePowers().Where(power => ReferenceEquals(power.Owner, creature)).ToArray()
            : creature.Powers;
    private static IEnumerable<CardModel> NativeRemovalAllCards(PlayerCombatState playerState)
    {
        if (_simulator is null) return playerState.AllCards;
        var player = _simulator.State.CombatState.Players.Single(player => ReferenceEquals(player.PlayerCombatState, playerState));
        return _simulator.State.GetPlayerCombatState(player).AllCards.Select(card => card.MutablePreview).ToArray();
    }
    private static void ClearNativeAffliction(CardModel card)
    {
        if (_simulator is null) { CardCmd.ClearAffliction(card); return; }
        (_simulator.State.FindCard(card) ?? throw new PredictionUnsupportedException("Removed power referenced an absent combat card.")).ClearAffliction();
    }
    private static DampenPower.Data NativeRemovalDampenData(PowerModel power)
    {
        if (_simulator is null) return power.GetInternalData<DampenPower.Data>();
        var combat = (SimulatedCombatState)_simulator.State.CombatState;
        var data = new DampenPower.Data();
        if (combat._dampenOriginalUpgrades is { } upgrades)
            foreach (var pair in upgrades.Where(pair => ReferenceEquals(pair.Key.Preview.Owner.Creature, power.Owner)))
                data.downgradedCardsToOldUpgradeLevels.Add(pair.Key.MutablePreview, pair.Value);
        return data;
    }
    private static void NativeCleanseVfx(Creature creature)
    { if (_simulator is null) HextechMikaelsBlessingVfx.Play(creature); }
    private static Task RemoveNativeMonsterBuff(PowerModel power)
        => _simulator is null ? HextechMonsterInteractionPolicy.RemoveMonsterBuffSafely(power) : RemoveNativePower(power);

    private static Task RemoveNativePowerWithLifecycle(PowerModel power)
    {
        var sim = _simulator ?? throw new InvalidOperationException("Missing native removal branch.");
        var combat = (SimulatedCombatState)sim.State.CombatState;
        sim.SynchronizePowerAmountPredictionStates();
        MethodInfo callback = AccessTools.Method(power.GetType(), nameof(PowerModel.AfterRemoved));
        Type declaring = callback.DeclaringType!;
        if (declaring != typeof(PowerModel) && !VisualOnlyRemovalTypes.Contains(declaring)
            && !NativeRemovalTypes.Contains(declaring) && declaring != typeof(SwordSagePower))
            throw new PredictionUnsupportedException($"Unreviewed native power removal: {power.GetType().Name}.");
        // SwordSage's SDK bookkeeping already preserves the captured bonus and
        // gameplay clones; initialize it before removal, then normalize once.
        if (power is SwordSagePower) combat.NormalizePowerCardState(sim);
        combat.SetPowerAmount(power, 0);
        if (NativeRemovalTypes.Contains(declaring)) RequireCompleted(power.AfterRemoved(power.Owner), power.GetType());
        if (power is DampenPower)
        {
            if (combat._dampenCasters is { } casters)
                foreach (var item in casters.Where(item => ReferenceEquals(item.Target, power.Owner)).ToArray()) casters.Remove(item);
            if (combat._dampenOriginalUpgrades is { } upgrades)
                foreach (var card in upgrades.Keys.Where(card => ReferenceEquals(card.Preview.Owner.Creature, power.Owner)).ToArray()) upgrades.Remove(card);
        }
        if (power is SwordSagePower) combat.NormalizePowerCardState(sim);
        PauseNativeChoice(sim);
        return Task.CompletedTask;
    }
}
