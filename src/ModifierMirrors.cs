using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal sealed class EnemyState(MonsterHexKind[] hexes, IReadOnlyDictionary<uint, int> delayedHealingBlock, int strengthTier,
    IReadOnlyDictionary<uint, int> courageProcs, IReadOnlyDictionary<uint, int> slapProcs,
    IReadOnlyDictionary<uint, int> bloodPactProcs, IReadOnlyDictionary<uint, int> clownProcs,
    EnemyHpState hp, IReadOnlyDictionary<ulong, int> playerAttacks,
    IReadOnlySet<ulong> exhaustFirst, IReadOnlySet<ulong> exhaustSecond) : IPredictionStateForkable, IPredictionForkBoundary
{
    private readonly MonsterHexKind[] _hexes = (MonsterHexKind[])hexes.Clone();
    internal readonly Dictionary<uint, int> DelayedHealingBlock = new(delayedHealingBlock);
    internal readonly int StrengthTier = strengthTier;
    internal readonly Dictionary<uint, int> CourageProcs = new(courageProcs);
    internal readonly Dictionary<uint, int> SlapProcs = new(slapProcs);
    internal readonly Dictionary<uint, int> BloodPactProcs = new(bloodPactProcs);
    internal readonly Dictionary<uint, int> ClownProcs = new(clownProcs);
    internal readonly EnemyHpState Hp = hp;
    internal readonly Dictionary<ulong, int> PlayerAttacks = new(playerAttacks);
    internal readonly HashSet<ulong> ExhaustFirst = new(exhaustFirst);
    internal readonly HashSet<ulong> ExhaustSecond = new(exhaustSecond);
    internal readonly List<Creature> PendingInstantDoom = [];
    internal CompensationEnemyHex? Compensation;
    internal readonly List<bool?> PendingPowerCardSources = [];
    internal readonly List<PredictedCard?> PendingPowerCards = [];
    internal EnemyStableSeed? StableSeed { get; private init; }
    internal MysterySeedState? TransformationSeed { get; private init; }
    internal NativePotionGenerationState PotionGeneration { get; private init; } = null!;
    internal EnemyTurnTrackingState? NativeTurns { get; private init; }
    internal readonly List<SimCreatureState> NearDeathCreatures = [];
    internal NativeSelfUpgradeState? SelfUpgrades;
    internal IReadOnlyDictionary<CardModel, int>? KeywordDeckMarkers;
    internal SimPlayerCombatState? KeywordPlayer;
    internal NativeGeneratedCardContext? GeneratedTags;
    internal NativeTheftState? Theft;
    internal IReadOnlyDictionary<Creature, int>? SoulBirthHps;
    private SimulatedCombatState? _combat;
    internal bool Has(MonsterHexKind hex) => Array.IndexOf(_hexes, hex) >= 0;
    internal static EnemyState Capture(HextechMayhemModifier source) =>
        new(source.GetActiveMonsterHexes().ToArray(), source.CombatTracking.DelayedEnemyHealingBlock,
            source.GetMonsterHexStrengthTier(MonsterHexKind.Judicator), source.CombatTracking.CourageProcsThisTurn,
            source.CombatTracking.SlapProcsThisTurn, source.CombatTracking.BloodPactProcsThisTurn,
            source.CombatTracking.ClownCollegeProcsThisTurn, EnemyHpState.Capture(source.CombatTracking),
            source.CombatTracking.PlayerAttackCardsPlayedThisTurn,
            source.CombatTracking.EightPennyGatePlayersTriggeredThisTurn,
            source.CombatTracking.EightPennyGatePlayersTriggeredSecondThisTurn)
        {
            PotionGeneration = new(source.ActiveRunState.Players.Single()),
            StableSeed = source.HasActiveMonsterHex(MonsterHexKind.JeweledGauntlet)
                || source.HasActiveMonsterHex(MonsterHexKind.Archmage) || source.HasActiveMonsterHex(MonsterHexKind.OmniDragonSoul)
                || source.HasActiveMonsterHex(MonsterHexKind.CorruptedBranch) || source.HasActiveMonsterHex(MonsterHexKind.SingularityAI)
                || source.HasActiveMonsterHex(MonsterHexKind.Mystery) || source.HasActiveMonsterHex(MonsterHexKind.ThievingHopper)
                ? EnemyStableSeed.Capture(source) : null,
            TransformationSeed = source.HasActiveMonsterHex(MonsterHexKind.Mystery)
                ? new MysterySeedState(source.ActiveRunState.Players.Single()) : null,
            NativeTurns = NativeRuneBridge.HasNativePeriodicHex(source) ? new(source.CombatTracking) : null,
            Theft = source.HasActiveMonsterHex(MonsterHexKind.ThievingHopper) ? NativeTheftState.Capture(source) : null,
        };
    internal void BindCombat(SimulatedCombatState combat)
    {
        _combat = combat;
        PowerSourceBridge.Bind(combat, this);
        NativeRuneBridge.BindNativePotionGeneration(combat, PotionGeneration);
        NativeTurns?.BindHp(Hp);
    }
    public void AssertForkable()
    {
        NativeRuneBridge.AssertNativeEnemyCompensationEmpty(Compensation);
        if (NativeTurns?.Model.HandlingMonsterTormentorBurn == true)
            throw new PredictionUnsupportedException("Cannot fork inside a native enemy Tormentor response.");
        if (NativeTurns?.Model.HandlingServantMasterIllusion == true)
            throw new PredictionUnsupportedException("Cannot fork inside a native Servant Master response.");
        if (PendingInstantDoom.Count != 0) throw new PredictionUnsupportedException("Cannot fork with unresolved deferred Doom kills.");
        if (PendingPowerCardSources.Count != 0 || PendingPowerCards.Count != 0)
            throw new PredictionUnsupportedException("Cannot fork with pending Hextech power source metadata.");
    }
    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        var copy = new EnemyState(_hexes, DelayedHealingBlock, StrengthTier, CourageProcs, SlapProcs, BloodPactProcs, ClownProcs,
            Hp.Fork(), PlayerAttacks, ExhaustFirst, ExhaustSecond) { StableSeed = StableSeed, TransformationSeed = TransformationSeed, PotionGeneration = PotionGeneration, NativeTurns = NativeTurns?.Fork(), SelfUpgrades = (NativeSelfUpgradeState?)SelfUpgrades?.Fork(context) };
        foreach (var creature in NearDeathCreatures) NativeRuneBridge.BindNativeNearEnemy(copy, context.RequireRemap(creature));
        copy.KeywordDeckMarkers = KeywordDeckMarkers;
        copy.KeywordPlayer = KeywordPlayer is null ? null : context.RequireRemap(KeywordPlayer);
        copy.GeneratedTags = (NativeGeneratedCardContext?)GeneratedTags?.Fork(context);
        copy.Theft = (NativeTheftState?)Theft?.Fork(context);
        copy.SoulBirthHps = SoulBirthHps;
        copy.Compensation = Compensation is null ? null : new CompensationEnemyHex();
        copy.PendingInstantDoom.AddRange(PendingInstantDoom);
        if (_combat is not null) copy.BindCombat(context.RequireRemap(_combat));
        return copy;
    }
    internal static void Write(EnemyState state, ref ModelPredictionStateWriter writer)
    {
        state.SelfUpgrades?.Write(ref writer);
        if (state.KeywordPlayer is not null) NativeRuneBridge.WriteNativePredictedKeywords(state.KeywordPlayer, ref writer);
        state.Theft?.Write(ref writer);
        if (state.Has(MonsterHexKind.SoulEater)) NativeRuneBridge.WriteSoulBirthHps(state.SoulBirthHps, ref writer);
        if (state.Has(MonsterHexKind.Compensation))
        {
            NativeRuneBridge.AssertNativeEnemyCompensationEmpty(state.Compensation);
            writer.Add("pendingEnemyCompensationCount", 0);
        }
        writer.Add("count", state._hexes.Length);
        writer.Add("strengthTier", state.StrengthTier);
        state.NativeTurns?.Write(ref writer);
        if (state.StableSeed is { } seed) EnemyStableSeed.Write(seed, ref writer);
        if (state.TransformationSeed is { } transformationSeed) MysterySeedState.Write(transformationSeed, ref writer);
        if (state.Has(MonsterHexKind.LightEmUp) || state.Has(MonsterHexKind.TwiceThrice))
        {
            writer.Add("playerAttackCount", state.PlayerAttacks.Count);
            foreach (var (id, count) in state.PlayerAttacks.OrderBy(pair => pair.Key)) writer.Add($"playerAttacks:{id}", count);
        }
        if (state.Has(MonsterHexKind.EightPennyGate))
        {
            writer.Add("exhaustFirstCount", state.ExhaustFirst.Count);
            foreach (ulong id in state.ExhaustFirst.Order()) writer.Add($"exhaustFirst:{id}", true);
            writer.Add("exhaustSecondCount", state.ExhaustSecond.Count);
            foreach (ulong id in state.ExhaustSecond.Order()) writer.Add($"exhaustSecond:{id}", true);
        }
        state.Hp.Write(ref writer);
        writer.Add("courageCount", state.CourageProcs.Count);
        foreach (var (id, count) in state.CourageProcs.OrderBy(p => p.Key)) writer.Add($"courage:{id}", count);
        writer.Add("slapCount", state.SlapProcs.Count);
        foreach (var (id, count) in state.SlapProcs.OrderBy(p => p.Key)) writer.Add($"slap:{id}", count);
        writer.Add("bloodPactCount", state.BloodPactProcs.Count);
        foreach (var (id, count) in state.BloodPactProcs.OrderBy(p => p.Key)) writer.Add($"bloodPact:{id}", count);
        writer.Add("clownCount", state.ClownProcs.Count);
        foreach (var (id, count) in state.ClownProcs.OrderBy(p => p.Key)) writer.Add($"clown:{id}", count);
        writer.Add("pendingInstantDoomCount", state.PendingInstantDoom.Count);
        for (int i = 0; i < state.PendingInstantDoom.Count; i++) writer.Add($"pendingInstantDoom:{i}", state.PendingInstantDoom[i].CombatId?.ToString());
        writer.Add("pendingSourceCount", state.PendingPowerCardSources.Count);
        for (int i = 0; i < state.PendingPowerCardSources.Count; i++)
        {
            writer.Add($"powerCardSource:{i}", state.PendingPowerCardSources[i] is not { } flag ? "unknown" : flag ? "card" : "none");
            writer.AddCard($"powerCardIdentity:{i}", state.PendingPowerCards[i]);
        }
        for (int i = 0; i < state._hexes.Length; i++) writer.Add($"hex{i}", state._hexes[i].ToString());
        writer.Add("delayedCount", state.DelayedHealingBlock.Count);
        foreach (var (id, amount) in state.DelayedHealingBlock.OrderBy(p => p.Key))
            writer.Add($"delayed:{id}", amount);
        state.PotionGeneration.Write(ref writer);
    }
}

internal static class ModifierMirrors
{
    private sealed class DetachedConfiguration(EnemyState state, IReadOnlyList<Player> players, IReadOnlyList<Creature> creatures)
    {
        internal readonly int LoopReduction = state.Has(MonsterHexKind.Loop) && state.StrengthTier > 1 ? 1 : 0;
        internal readonly bool ReforgedHelmet = state.Has(MonsterHexKind.ReforgedHelmet);
        internal readonly bool DuffsVintage = state.Has(MonsterHexKind.DuffsVintage);
        private readonly FrozenSet<Player> _players = players.ToFrozenSet();
        private readonly FrozenSet<Creature> _creatures = creatures.ToFrozenSet();
        internal bool Owns(Player player) => _players.Contains(player);
        internal bool Owns(Creature creature) => _creatures.Contains(creature);
    }
    // Cloned modifier identities are shared read-only by solver branches. Only this
    // marker is shared; all mutable adapter state lives in PredictionStateStore.
    private static readonly ConditionalWeakTable<AbstractModel, DetachedConfiguration> Detached = new();

    internal static void Register(Harmony harmony)
    {
        ModelPredictionStateMirrors.RegisterModifier<HextechMayhemModifier, EnemyState>(
            "hextech-enemies-v14-native-growth", (simulator, live) =>
            {
                NativeRuneBridge.AssertNativeInstantQueueQuiescent(live);
                NativeRuneBridge.AssertLiveEnemyCompensationEmpty();
                var captured = EnemyState.Capture(live);
                captured.SelfUpgrades = NativeSelfUpgradeState.Capture(simulator, simulator.State.CombatState.Players.Single());
                captured.KeywordDeckMarkers = NativeRuneBridge.CaptureNativeKeywordDeckMarkers(simulator);
                captured.KeywordPlayer = simulator.State.GetPlayerCombatState(simulator.State.CombatState.Players.Single());
                captured.GeneratedTags = new NativeGeneratedCardContext(simulator, simulator.State.CombatState.Players.Single());
                foreach (var creature in simulator.State.CombatState.Enemies) NativeRuneBridge.BindNativeNearEnemy(captured, simulator.State.GetCreature(creature));
                if (captured.Has(MonsterHexKind.SoulEater)) captured.SoulBirthHps = NativeRuneBridge.CaptureNativeEnemySoulBirth(simulator);
                captured.BindCombat((SimulatedCombatState)simulator.State.CombatState);
                return captured;
            },
            (HextechMayhemModifier live, ref ModelPredictionStateWriter writer) =>
            {
                NativeSelfUpgradeState.WriteLive(live.ActiveRunState.Players.Single(), ref writer);
                NativeRuneBridge.WriteNativeLiveKeywords(live, ref writer);
                EnemyState.Write(EnemyState.Capture(live), ref writer);
            },
            EnemyState.Write);
        harmony.Patch(AccessTools.Method(typeof(ModelPredictionStateMirrors), "CaptureRootState"),
            postfix: new HarmonyMethod(typeof(ModifierMirrors), nameof(MarkDetached)));
        PowerSourceBridge.Register(harmony);
        EnemySpawnBridge.Register(harmony);
        FurCoatSnapshot.Register(harmony);
        EnemyMoveRepeatBridge.Register(harmony);
        NativeRuneBridge.RegisterNativeEnemyTurnFamily(harmony);

        // These native read-only hooks have no registry in the pinned solver. For the exact
        // supported enemy set they are identity/true; do not read the live run.
        Patch(harmony, nameof(HextechMayhemModifier.ModifyHandDraw), nameof(DrawIdentity));
        Patch(harmony, nameof(HextechMayhemModifier.ShouldFlush), nameof(AllowCapturedFlush));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ShouldEtherealTrigger)));
        Patch(harmony, nameof(HextechMayhemModifier.TryModifyPowerAmountReceived), nameof(PowerIdentity));

        BeforeSideTurnStartMirrors.Register<HextechMayhemModifier>(BeforeTurn);
        BeforeSideTurnEndMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            NativeRuneBridge.DispatchNativeEnemyPileEnd(modifier, state, context.Simulator, context.Side);
            NativeRuneBridge.ClearNativePorcupine(modifier, state, context.Simulator);
            state.NativeTurns?.Model.BackToBasicsCardsPlayedThisTurn.Clear();
            if (context.Side == CombatSide.Player) state.PlayerAttacks.Clear();
        });
        AfterPlayerTurnStartMirrors.RegisterLate<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyPileStart(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Player));
        BeforeCardPlayedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            NativeRuneBridge.DispatchNativeLivingFogBefore(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.CardPlay);
            NativeRuneBridge.RecordNativeStorm(context.Simulator, context.CardPlay);
        });
        AfterCardPlayedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            var play = context.CardPlay;
            var card = play.Card;
            if (state.Has(MonsterHexKind.AncientStatue) && card.Owner?.Creature.Side == CombatSide.Player
                && context.State.GetCreature(card.Owner.Creature).IsAlive)
                NativeRuneBridge.ApplySlowFromCard(context.Simulator, card.Owner.Creature,
                    AncientStatueEnemyHex.ResolveCardSlowGain(state.StrengthTier), card);
            if ((state.Has(MonsterHexKind.LightEmUp) || state.Has(MonsterHexKind.TwiceThrice))
                && play.IsFirstInSeries && !play.IsAutoPlay && card.Owner?.Creature.Side == CombatSide.Player
                && NativeRuneBridge.NativeAttackForEffects(card, context.Simulator))
                state.PlayerAttacks[card.Owner.NetId] = state.PlayerAttacks.GetValueOrDefault(card.Owner.NetId) + 1;
            NativeRuneBridge.DispatchNativeEnemyPlayed(modifier, state, context.Simulator, play);
        });
        AfterCardPlayedMirrors.LateRegistry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.ChannelNativeStorm(context.Simulator, context.CardPlay));
        AfterCardDrawnMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyDraw(modifier,
                ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator,
                context.Card.MutablePreview, context.FromHandDraw));
        AfterCardExhaustedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyExhaust(modifier,
                ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator,
                context.Card.MutablePreview, context.CausedByEthereal));
        AfterShuffleMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyShuffle(modifier,
                ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Player));
        AfterDamageGivenMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            // Preserve the native immediate dispatcher order, resolving each
            // power command before proceeding to the next enemy effect.
            if (context.Dealer?.Side != CombatSide.Enemy
                || context.Target.Side != CombatSide.Player || !context.State.GetCreature(context.Target).IsAlive
                || context.Result.UnblockedDamage <= 0) return;
            var combat = (SimulatedCombatState)context.CombatState;
            void Debuff<T>(int amount) where T : PowerModel
            {
                combat.ApplyPowerFromSource(typeof(T), context.Target, amount, context.Dealer, context.Source?.Preview);
                PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
            }
            if (state.Has(MonsterHexKind.ShrinkRay)) Debuff<ShrinkPower>((int)ShrinkRayEnemyHex.ShrinkStacks);
            if (state.Has(MonsterHexKind.Firebrand))
                NativeRuneBridge.NativeEnemyFirebrandDamage(modifier, state, context.Simulator,
                    context.Dealer, context.Result, context.Target, context.Source?.Preview);
            if (state.Has(MonsterHexKind.SerpentsFang))
                Debuff<PoisonPower>(state.StrengthTier <= 1 ? 2 : state.StrengthTier == 2 ? 3 : 4);
            if (state.Has(MonsterHexKind.OminousPact)) Debuff<DoomPower>(Math.Min(context.Result.UnblockedDamage, 999999999));
            if (state.Has(MonsterHexKind.Corrosion) && context.Target.Player is not null)
                Debuff<FrailPower>(CorrosionEnemyHex.FrailAmount);
            if (state.Has(MonsterHexKind.DeathHarvest) && context.Target.Player is not null
                && context.State.GetCreature(context.Dealer).IsAlive)
            {
                decimal percent = state.StrengthTier <= 1 ? .5m : state.StrengthTier == 2 ? .75m : 1m;
                context.Simulator.Heal(context.Dealer, Math.Max(1, (int)Math.Floor(context.Result.UnblockedDamage * percent)));
            }
            NativeRuneBridge.DispatchNativeEnemyGoldHit(modifier, state, context.Simulator, context.Dealer, context.Result, context.Target, context.Source?.MutablePreview);
            NativeRuneBridge.DispatchNativePhrogHit(modifier, state, context.Simulator, context.Dealer, context.Result, context.Target, context.Source?.MutablePreview);
            // Player-hit callbacks follow every immediate callback. Buffer has
            // no special enemy scaling override and retains its self-applier.
            NativeRuneBridge.DispatchNativeEnemyPlayerHit(modifier, state, context.Simulator, context.Dealer, context.Target);
        });
        AfterDamageReceivedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            if (context.Target.Side != CombatSide.Enemy) return;
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            var combat = (SimulatedCombatState)context.CombatState;
            NativeRuneBridge.DispatchNativeEnemyCompensationAfter(modifier, state, context.Simulator,
                context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            if (context.Simulator.HasPendingChoice) return;
            NativeRuneBridge.DispatchNativeEnemyHundredHit(modifier, state, context.Simulator,
                context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            if (context.Simulator.HasPendingChoice || context.Result.UnblockedDamage <= 0) return;
            bool alive = context.State.GetCreature(context.Target).IsAlive;
            if (alive && state.Has(MonsterHexKind.BloodPact)
                && HextechCombatProcTracker.TryConsumeLimitedProc(state.BloodPactProcs, context.Target, 2))
            {
                combat.BeginCardPowerApplication(null);
                try { combat.ApplyTemporaryStrengthGain<HextechBloodPactTemporaryStrengthPower>(context.Target,
                    (int)BloodPactEnemyHex.TemporaryStrengthStacks, context.Target); }
                finally { combat.CompleteCardPowerApplication(null); }
            }
            if (alive && state.Has(MonsterHexKind.ClownCollege)
                && HextechCombatProcTracker.TryConsumeLimitedProc(state.ClownProcs, context.Target, 1))
            {
                // Slippery's earlier power listener consumes its old stack in
                // StateStore. Settle that consumption before adding the new
                // native stack, otherwise card-tail normalization overwrites it.
                context.Simulator.SynchronizePowerAmountPredictionStates();
                int current = combat.GetAmount<SlipperyPower>(context.Target);
                int offset = (int)Math.Min(ClownCollegeEnemyHex.SlipperyStacks,
                    Math.Max(0m, int.MaxValue - (decimal)current));
                if (offset > 0) combat.ApplyPowerFromSource(typeof(SlipperyPower), context.Target, offset, null, null);
            }
            NativeRuneBridge.DispatchNativePorcupineHit(modifier, state, context.Simulator, context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            NativeRuneBridge.RecordNativeMountainHit(modifier, state, context.Simulator,
                context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            NativeRuneBridge.DispatchNativeThievingHit(modifier, state, context.Simulator,
                context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            NativeRuneBridge.DispatchNativeEnemyHealthThresholds(modifier, state, context.Simulator,
                context.Target, context.Result, context.Dealer, context.Source?.MutablePreview);
            PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
        });
        AfterCurrentHpChangedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            NativeRuneBridge.DispatchNativeEnemyHpLoss(modifier, state, context.Simulator, context.Creature, context.Delta);
            NativeRuneBridge.DispatchNativeNearEnemyHp(modifier, state, context.Simulator, context.Creature, context.Delta);
        });
        AfterBlockGainedMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyBlock(modifier,
                ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator,
                context.Creature, context.Amount, context.Props, context.Source?.MutablePreview));
        BeforeDeathMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyDeath(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Creature, true));
        AfterDeathMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
            NativeRuneBridge.DispatchNativeEnemyDeath(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Creature, false, context.WasRemovalPrevented));
        ModifyDamageMirrors.MultiplicativeRegistry.Register<HextechMayhemModifier>(DamageMultiplier);
        ModifyBlockMultiplicativeMirrors.Registry.Register<HextechMayhemModifier>((m, c) =>
            c.Target.Side == CombatSide.Enemy ? SustainMultiplier(ModelPredictionStateMirrors.Get<EnemyState>(c.Simulator, m),
                (SimulatedCombatState)c.CombatState, c.State.GetCreature(c.Target).MaxHp) : 1m);
        ModifyHpLostMirrors.AfterOstyRegistry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            decimal amount = NativeRuneBridge.DispatchNativeEnemyCompensationHp(modifier, state, context.Simulator,
                context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.MutablePreview);
            return state.Has(MonsterHexKind.TungstenRod) && context.Target.Side == CombatSide.Enemy
                && context.State.GetCreature(context.Target).IsAlive && amount > 0m
                ? TungstenRodEnemyHex.ReduceHpLoss(amount, state.StrengthTier) : amount;
        });
        ShouldDrawMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) => NativeRuneBridge.NativeEnemyShouldDraw(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Player, context.FromHandDraw));
        ShouldPlayMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) => NativeRuneBridge.NativeEnemyMayPlay(modifier, ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier), context.Simulator, context.Card.MutablePreview, context.AutoPlayType));
        ModifyEnergyCostInCombatMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var card = context.Card.Preview;
            if (card.Owner?.Creature.Side != CombatSide.Player || card.EnergyCost.CostsX)
                return context.Cost;
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            decimal cost = context.Cost;
            if (state.Has(MonsterHexKind.BlueCandleMedkit) && card.Type is CardType.Status or CardType.Curse)
                cost += BlueCandleMedkitEnemyHex.GetIncreaseSurvivingLocalModifiers(card.EnergyCost, BlueCandleMedkitEnemyHex.CostIncrease);
            if (context.Cost <= 0m || !NativeRuneBridge.NativeAttackForEffects(card, context.Simulator)
                || context.Card.GetPile(context.State)?.Type != PileType.Hand)
                return cost;
            int next = state.PlayerAttacks.GetValueOrDefault(card.Owner.NetId) + 1;
            decimal multiplier = 1m;
            if (state.Has(MonsterHexKind.LightEmUp) && next % 4 == 0) multiplier *= 2m;
            if (state.Has(MonsterHexKind.TwiceThrice) && next % 3 == 0) multiplier *= 2m;
            return cost * multiplier;
        });
        ModifyEnergyCostInCombatMirrors.LateRegistry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            return state.Has(MonsterHexKind.Enlightenment) && context.Card.Preview.Owner?.Creature.Side == CombatSide.Player
                && !context.Card.Preview.EnergyCost.CostsX ? Math.Max(1m, context.Cost) : context.Cost;
        });
        ModifyCardPlayResultLocationMirrors.Registry.Register<HextechMayhemModifier>((modifier, context) =>
        {
            var location = context.Location;
            var state = ModelPredictionStateMirrors.Get<EnemyState>(context.Simulator, modifier);
            if (state.Has(MonsterHexKind.SomethingForNothing) && context.Card.Preview.Owner?.Creature.Side == CombatSide.Player
                && SomethingForNothingRune.IsZeroCostPlay(context.Resources.EnergyValue))
                location.pileType = PileType.Exhaust;
            var card = context.Card.Preview;
            if (state.Has(MonsterHexKind.EightPennyGate) && !context.IsAutoPlay && card.Type != CardType.Power
                && card.Owner?.Creature.Side == CombatSide.Player)
            {
                int limit = state.StrengthTier <= 1 ? 0 : state.StrengthTier == 2 ? 1 : 2;
                if (limit > 0 && (state.ExhaustFirst.Add(card.Owner.NetId)
                    || limit > 1 && state.ExhaustSecond.Add(card.Owner.NetId)))
                    location.pileType = PileType.Exhaust;
            }
            return location;
        });
    }

    private static void Patch(Harmony harmony, string method, string prefix) => harmony.Patch(
        AccessTools.Method(typeof(HextechMayhemModifier), method), prefix: new HarmonyMethod(typeof(ModifierMirrors), prefix));

    internal static void AfterPowerChanged(HextechMayhemModifier modifier, CombatPredictionSimulator simulator,
        SimulatedPowerAmountChange change, bool? hasCardSource, PredictedCard? cardSource)
    {
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        NativeRuneBridge.DispatchNativeServantPower(modifier, state, simulator, change, cardSource);
        var power = change.Power;
        var target = power.Owner;
        if (target.Side != CombatSide.Enemy
            || !simulator.State.GetCreature(target).IsAlive || change.Delta == 0 || power is ITemporaryPower
            || power.GetTypeForAmount(change.Delta) != PowerType.Debuff) return;
        if (change.Delta < 0)
        {
            if (!power.AllowNegative || power.GetTypeForAmount(-change.Delta) != PowerType.Buff
                || change.Applier == target) return;
            if (change.Applier is null)
            {
                if (hasCardSource is null)
                    throw new PredictionUnsupportedException("Enemy debuff reaction has no captured card-source metadata.");
                if (!hasCardSource.Value) return;
            }
        }
        // Native OrderedEffects dispatches Slap, Courage, then BadTaste. Counters
        // are consumed even if applying Plating is clamped to zero.
        if (state.Has(MonsterHexKind.Slap)
            && HextechCombatProcTracker.TryConsumeLimitedProc(state.SlapProcs, target, 3))
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            combat.BeginCardPowerApplication(null);
            try { combat.ApplyTemporaryStrengthGain<HextechSlapTemporaryStrengthPower>(target, 1, target); }
            finally { combat.CompleteCardPowerApplication(null); }
        }
        if (state.Has(MonsterHexKind.Tormentor))
            NativeRuneBridge.NativeEnemyTormentorChanged(modifier, state, simulator, target);
        if (state.Has(MonsterHexKind.CourageOfColossus)
            && HextechCombatProcTracker.TryConsumeLimitedProc(state.CourageProcs, target, 2))
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            int plating = CourageOfColossusEnemyHex.ResolvePlating(simulator.State.GetCreature(target).MaxHp, state.StrengthTier);
            int current = combat.EffectivePowers().OfType<PlatingPower>().SingleOrDefault(p => p.Owner == target)?.Amount ?? 0;
            int offset = (int)Math.Min(plating, Math.Max(0m, int.MaxValue - (decimal)current));
            // Hextech's scaling wrapper clears enemy self-appliers and gives
            // Plating its unscaled, int-clamped final offset in single player.
            if (offset > 0) combat.ApplyPowerFromSource(typeof(PlatingPower), target, offset, null, null);
        }
        if (state.Has(MonsterHexKind.BadTaste))
        {
            int heal = BadTasteEnemyHex.HealAmountFor(simulator.State.GetCreature(target).MaxHp);
            if (heal > 0) simulator.Heal(target, heal);
        }
    }
    private static void MarkDetached(CombatPredictionSimulator simulator, AbstractModel clone)
    {
        if (clone is HextechMayhemModifier)
            Detached.GetValue(clone, _ => new DetachedConfiguration(
                ModelPredictionStateMirrors.Get<EnemyState>(simulator, clone), simulator.State.CombatState.Players,
                simulator.State.CombatState.Creatures));
    }
    private static bool AllowCapturedFlush(HextechMayhemModifier __instance, Player player, ref bool __result)
    {
        if (!Detached.TryGetValue(__instance, out var config)) return true;
        __result = !config.DuffsVintage || !config.Owns(player);
        return false;
    }
    private static bool DrawIdentity(HextechMayhemModifier __instance, Player player, decimal count, ref decimal __result)
    {
        if (!Detached.TryGetValue(__instance, out var config)) return true;
        __result = config.Owns(player) && config.LoopReduction > 0 ? Math.Max(0m, count - config.LoopReduction) : count;
        return false;
    }
    private static bool PowerIdentity(HextechMayhemModifier __instance, PowerModel canonicalPower, Creature target,
        decimal amount, ref decimal modifiedAmount, ref bool __result)
    {
        if (!Detached.TryGetValue(__instance, out var config)) return true;
        modifiedAmount = config.ReforgedHelmet && config.Owns(target) && target.Side == CombatSide.Enemy
            && canonicalPower is StrengthPower && amount < 0m ? 0m : amount;
        __result = modifiedAmount != amount;
        return false;
    }
    private static decimal DamageMultiplier(HextechMayhemModifier modifier, ModifyDamageMirrorContext c)
    {
        if (c.Dealer?.Side != CombatSide.Enemy) return 1m;
        EnemyState state = ModelPredictionStateMirrors.Get<EnemyState>(c.Simulator, modifier);
        decimal multiplier = state.Has(MonsterHexKind.BigStrength) ? 1.2m : 1m;
        if (state.Has(MonsterHexKind.GoldenSpatula))
            multiplier *= NativeRuneBridge.NativeEnemyGoldenDamage(modifier, state, c.Simulator,
                c.Target, c.Amount, c.Props, c.Dealer, c.CardSource?.MutablePreview);
        if (state.Has(MonsterHexKind.Goldrend))
            multiplier *= NativeRuneBridge.NativeEnemyGoldrendDamage(modifier, state, c.Simulator, c.Target, c.Amount, c.Props, c.Dealer, c.CardSource?.MutablePreview);
        if (state.Has(MonsterHexKind.HeavyHitter))
            multiplier *= 1m + Math.Min(30m, Math.Max(0m, Math.Floor(c.State.GetCreature(c.Dealer).MaxHp / 15m))) * 0.01m;
        if (state.Has(MonsterHexKind.VitalitySurge))
            multiplier *= VitalityMultiplier(c.State.GetCreature(c.Dealer).MaxHp);
        if (state.Has(MonsterHexKind.MoreTheMerrier))
            multiplier *= RelicCountMultiplier((SimulatedCombatState)c.CombatState);
        multiplier *= AttributeMultiplier(state);
        if (state.Has(MonsterHexKind.AstralBody)) multiplier *= .9m;
        if (state.Has(MonsterHexKind.GlassCannon))
            multiplier *= 1m + (state.StrengthTier <= 1 ? .3m : state.StrengthTier == 2 ? .4m : .5m);
        if (state.Has(MonsterHexKind.HandOfBaron))
            multiplier *= NativeRuneBridge.NativeEnemyBaronDamage(modifier, state, c.Simulator,
                c.Target, c.Amount, c.Props, c.Dealer, c.CardSource?.MutablePreview);
        if (state.Has(MonsterHexKind.GiantSlayer) && c.Target?.Player is not null)
            multiplier *= 1m + GiantSlayerEnemyHex.GetBonus(c.State.GetCreature(c.Target).MaxHp);
        if (state.Has(MonsterHexKind.Judicator) && c.Target?.Player is not null)
        {
            var target = c.State.GetCreature(c.Target);
            if (target.CurrentHp * 2m < target.MaxHp)
                multiplier *= 1m + (state.StrengthTier <= 1 ? .10m : state.StrengthTier == 2 ? .20m : .30m);
        }
        return multiplier;
    }
    private static decimal VitalityMultiplier(int maxHp) => 1m + Math.Min(30m, Math.Max(0m, Math.Floor(maxHp / 20m))) * .01m;
    private static decimal RelicCountMultiplier(SimulatedCombatState combat) =>
        1m + combat.Players.Sum(player => combat.RelicsOf(player).Count) * .01m;
    private static decimal AttributeMultiplier(EnemyState state)
    {
        // The native getter ignores kind; the captured act/floor tier is shared
        // by these coefficients. Persistent HP changes use branch-owned tracking.
        decimal multiplier = state.Has(MonsterHexKind.Goliath)
            ? 1m + (state.StrengthTier <= 1 ? .15m : state.StrengthTier == 2 ? .20m : .25m) : 1m;
        foreach (var kind in new[] { MonsterHexKind.Stats, MonsterHexKind.StatsOnStats, MonsterHexKind.StatsOnStatsOnStats })
            if (state.Has(kind)) multiplier *= 1m + EnemyAttributeBoostValues.GetBonusFraction(kind, state.StrengthTier);
        return multiplier;
    }
    private static decimal SustainMultiplier(EnemyState state, SimulatedCombatState combat, int maxHp)
    {
        decimal multiplier = state.Has(MonsterHexKind.FirstAidKit) ? 1.25m : 1m;
        if (state.Has(MonsterHexKind.GoldenSpatula)) multiplier *= .5m;
        if (state.Has(MonsterHexKind.VitalitySurge)) multiplier *= VitalityMultiplier(maxHp);
        if (state.Has(MonsterHexKind.ProteinShake)) multiplier *= 1m + Math.Max(0m, Math.Floor(maxHp / 5m)) * .01m;
        if (state.Has(MonsterHexKind.MoreTheMerrier)) multiplier *= RelicCountMultiplier(combat);
        multiplier *= AttributeMultiplier(state);
        return multiplier;
    }
    internal static bool ModifyEnemyHeal(CombatPredictionSimulator simulator, SimulatedCombatState combat, Creature target, ref decimal amount)
    {
        // Native Hextech exempts revival heals from both scaling and suppression.
        var creature = simulator.State.GetCreature(target);
        if (creature.IsDead && amount > 0m) return true;
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().SingleOrDefault();
        if (modifier == null) return true;
        var state = ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
        amount *= SustainMultiplier(state, combat, creature.MaxHp);
        // Native healing scales first, then applies GlassCannon's 70% cap.
        if (state.Has(MonsterHexKind.GlassCannon))
            amount = Math.Min(amount, Math.Max(0m, Math.Floor(creature.MaxHp * GlassCannonEnemyHex.HealCapPercent) - creature.CurrentHp));
        // This global Hextech patch also affects the vanilla Skulking Colony,
        // even when no enemy hex is selected. Delay belongs to each branch.
        if ((target.Monster is SkulkingColony || combat.Players.Any(player =>
                combat.RelicsOf(player).Any(relic => relic is RegenerationSuppressionRune)))
            && target.CombatId is uint id && amount >= 1m)
        {
            state.DelayedHealingBlock[id] = state.DelayedHealingBlock.GetValueOrDefault(id) + (int)Math.Floor(amount);
            return false;
        }
        return amount > 0m;
    }
    private static void BeforeTurn(HextechMayhemModifier modifier, BeforeSideTurnStartMirrorContext c)
    {
        EnemyState state = ModelPredictionStateMirrors.Get<EnemyState>(c.Simulator, modifier);
        NativeRuneBridge.DispatchNativeDeadCleanup(modifier, state, c.Simulator, c.Side);
        NativeRuneBridge.ClearNativePorcupine(modifier, state, c.Simulator);
        state.NativeTurns?.Model.BloodArmorHpLossThisPlayerTurn.Clear();
        state.NativeTurns?.Model.BackToBasicsCardsPlayedThisTurn.Clear();
        if (c.Side is CombatSide.Player or CombatSide.Enemy) state.PlayerAttacks.Clear();
        if (c.Side == CombatSide.Player)
        {
            state.NativeTurns?.Model.PlayerRuneProcsThisTurn.Clear();
            state.NativeTurns?.Model.InspectExtraDrawsPreventedThisTurn.Clear();
            if (state.NativeTurns is { } turns) { turns.Model.PlayersAwaitingPlayPhase.Clear(); foreach (var player in c.CombatState.Players) turns.Model.PlayersAwaitingPlayPhase.Add(player.NetId); }
            state.ExhaustFirst.Clear();
            state.ExhaustSecond.Clear();
            state.CourageProcs.Clear();
            state.SlapProcs.Clear();
            state.BloodPactProcs.Clear();
            state.ClownProcs.Clear();
            state.NativeTurns?.Model.FinalFormTriggeredThisTurn.Clear();
            state.NativeTurns?.Model.TormentorProcsThisTurn.Clear();
            state.NativeTurns?.Model.DevilsDanceTriggeredThisTurn.Clear();
            state.NativeTurns?.Model.GripPlayersTriggeredThisTurn.Clear();
            state.NativeTurns?.Model.MindOverMatterPlayersTriggeredThisTurn.Clear();
            // Native BeforePlayerSideTurnStart reapplies persistent effects
            // before delayed healing blocks. The HP providers have once-per-
            // creature markers; GlassCannon's cap runs on every preparation,
            // including the player turn following a TestSubject revival.
            foreach (var enemy in c.CombatState.Enemies)
                state.Hp.Apply(state, c.Simulator, (SimulatedCombatState)c.CombatState, enemy);
            foreach (var (id, amount) in state.DelayedHealingBlock.ToArray())
            {
                state.DelayedHealingBlock.Remove(id);
                var enemy = c.CombatState.GetCreature(id);
                if (enemy != null && c.State.GetCreature(enemy).IsAlive && amount > 0)
                    c.Simulator.GainBlock(enemy, amount, ValueProp.Unpowered);
            }
            NativeRuneBridge.DispatchNativeEnemyTurns(modifier, state, c.Simulator, c.Side);
            return;
        }
        if (c.Side == CombatSide.Enemy)
            NativeRuneBridge.DispatchNativeEnemyTurns(modifier, state, c.Simulator, c.Side);
    }
}
