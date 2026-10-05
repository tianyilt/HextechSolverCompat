using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using System.Reflection;
using System.Reflection.Emit;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterOwnerDebuffRunes(Harmony harmony)
    {
        foreach (string helper in new[] { "TryGetOwnerReceivedDebuff", "TryGetOwnerReceivedBuff" })
            PatchEventCallback(harmony, AccessTools.Method(typeof(HextechRelicBase), helper),
                Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechRelicBase), "TryGetOwnedEnemyDebuffTarget"));
        RegisterOwnerDebuffRune<SlapRune>(harmony);
        RegisterOwnerDebuffRune<AdamantRune>(harmony);
        RegisterOwnerDebuffRune<CourageOfColossusRune>(harmony);
        RegisterOwnerDebuffRune<BadTasteRune>(harmony, heals: true);
        RegisterDoomReactionFamily(harmony);
        harmony.Patch(AccessTools.Method(typeof(PowerLifecycleSupport), nameof(PowerLifecycleSupport.ResolvePowerAmountChanges)),
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(ResolveOwnedDebuffChanges)));
        var stockPowers = AccessTools.Method(typeof(CorePowerSupport), nameof(CorePowerSupport.ApplyCardPowers));
        harmony.Patch(stockPowers,
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(ResolveKnowThyPlaceCommands)));
        NativeCallbackContracts.Add(stockPowers, AccessTools.Method(typeof(NativeRuneBridge), nameof(ResolveKnowThyPlaceCommands)));
    }

    private static IEnumerable<CodeInstruction> ResolveKnowThyPlaceCommands(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var body = instructions.ToList();
        var panache = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.SynchronizePanacheState));
        var nextEnergy = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.AddEnergyNextTurn));
        var stockSupport = AccessTools.Method(typeof(CardOnPlaySupport), nameof(CardOnPlaySupport.Apply));
        var gainGold = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.GainPlayerGold));
        var recordGold = AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.RecordLongTermResource));
        var goldSites = body.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Calls(gainGold)).ToArray();
        if (goldSites.Length != 1 || !body.Skip(goldSites[0].index + 1).Take(4).Any(instruction => instruction.Calls(recordGold)))
            throw new InvalidOperationException("Pinned fatal gold reward boundary changed.");
        int goldRecordIndex = body.Select((instruction, index) => (instruction, index))
            .First(item => item.index > goldSites[0].index && item.instruction.Calls(recordGold)).index;
        body[goldRecordIndex].operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(AlreadyRecordedNativeGold));
        body[goldRecordIndex].opcode = OpCodes.Call;
        var maxHp = AccessTools.Method(typeof(SimCreatureState), nameof(SimCreatureState.SetMaxHp));
        var heal = AccessTools.Method(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.Heal));
        var maxHpSites = body.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Calls(maxHp)).ToArray();
        if (maxHpSites.Length != 1) throw new InvalidOperationException("Pinned Feed max HP boundary changed.");
        int feedHealIndex = body.Select((instruction, index) => (instruction, index))
            .First(item => item.index > maxHpSites[0].index && item.instruction.Calls(heal)).index;
        body[feedHealIndex].operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(AlreadyHealedNativeFeed));
        body[feedHealIndex].opcode = OpCodes.Call;
        var panacheSites = body.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Calls(panache)).ToArray();
        var supportSites = body.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Calls(stockSupport)).ToArray();
        var energySites = body.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Calls(nextEnergy)).ToArray();
        if (panacheSites.Length != 1 || supportSites.Length != 1 || energySites.Length != 2)
            throw new InvalidOperationException("Pinned card compensation boundaries changed.");
        int tail = energySites[^1].index + 1;
        if (body[tail].opcode != OpCodes.Ldarg_0 || !body[tail + 1].Calls(
                AccessTools.PropertyGetter(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.HasPendingChoice))))
            throw new InvalidOperationException("Pinned card compensation tail changed.");
        var sharedTail = generator.DefineLabel();
        body[tail].labels.Add(sharedTail);
        body[supportSites[0].index].operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyRemainingStockCardBody));
        int count = 0;
        var pausedReturn = generator.DefineLabel();
        foreach (var instruction in body)
        {
            if (instruction.Calls(maxHp))
            {
                var simulatorArgument = new CodeInstruction(OpCodes.Ldarg_0);
                simulatorArgument.labels.AddRange(instruction.labels); instruction.labels.Clear();
                simulatorArgument.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return simulatorArgument;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GrantNativeFeedMaxHp));
            }
            if (instruction.Calls(gainGold))
            {
                var simulatorArgument = new CodeInstruction(OpCodes.Ldarg_0);
                simulatorArgument.labels.AddRange(instruction.labels); instruction.labels.Clear();
                simulatorArgument.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return simulatorArgument;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(GrantNativeFatalGold));
            }
            yield return instruction;
            if (instruction.Calls(panache))
            {
                // Preserve death resolution, monologues, Panache and the full
                // common card tail. Only the vanilla OnPlay compensation is
                // replaced when this precise card has its captured native rune.
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(HasCompleteNativeCardBody));
                yield return new CodeInstruction(OpCodes.Brtrue, sharedTail);
            }
            if (instruction.operand is not MethodInfo method || method.DeclaringType != typeof(SimulatedCombatState)
                || method.Name != nameof(SimulatedCombatState.Apply) || !method.IsGenericMethod) continue;
            count++;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldarg_2);
            yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(AfterStockPowerCommand));
            var continued = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Brtrue, continued);
            // Leave also unwinds any stock foreach finally. The pause return
            // sits outside every original exception region; never emit ret
            // inside a protected body.
            yield return new CodeInstruction(OpCodes.Leave, pausedReturn);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(continued);
        }
        if (count != 56) throw new InvalidOperationException($"Pinned stock power application sites changed: {count}.");
        yield return new CodeInstruction(OpCodes.Ldc_I4_0).WithLabels(pausedReturn);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static void ApplyRemainingStockCardBody(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PredictedCard card, CardPlay play, Creature? target, ISet<uint> processedEnemyDeaths)
    {
        if (!HasCompleteNativeCardBody(combat, card))
            CardOnPlaySupport.Apply(simulator, combat, card, play, target, processedEnemyDeaths);
    }

    private static bool AfterStockPowerCommand(CombatPredictionSimulator simulator, SimulatedCombatState combat, PredictedCard card)
    {
        // The native skill awaits Weak before applying Vulnerable. Batching
        // both until card tail changes native power insertion/listener order.
        // Keep this bridge limited to the reviewed card and reaction families.
        if (card.Preview is not KnowThyPlace || !HasAuditedDebuffReactions(simulator, combat)) return true;
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (!simulator.HasPendingChoice) return true;
        simulator.RejectExecutionContinuation();
        return false;
    }

    private static void RegisterOwnerDebuffRune<T>(Harmony harmony, bool heals = false) where T : LimitedDebuffProcRelicBase
    {
        var callback = AccessTools.DeclaredMethod(typeof(T), "OnDebuffProc");
        if (typeof(T) == typeof(TormentorRune))
            PatchEventCallback(harmony, callback, SingleNativePowerSite<HextechBurnPower>());
        else if (typeof(T) == typeof(AdamantRune))
            PatchEventCallback(harmony, callback, Site(
                AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CreatureCmd), "GainBlock",
                    [typeof(Creature), typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar), typeof(CardPlay), typeof(bool)]),
                nameof(GainNativeVarBlock)));
        else
        {
            if (!heals) ExpectedPowerCalls.Add(callback, 1);
            harmony.Patch(callback, transpiler: new HarmonyMethod(typeof(NativeRuneBridge),
                heals ? nameof(RewriteHeal) : nameof(RewritePowerCommands)));
        }
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>((rune, context) => RequireCompleted(Invoke(rune, context.Simulator,
            model => model.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), context.Side, context.CombatState)), typeof(T)));
    }

    private static bool ResolveOwnedDebuffChanges(CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        if (!HasAuditedDebuffReactions(simulator, combat)) return true;
        // The pinned solver dispatches three vanilla power reactions here. Its
        // active listener list already preserves native creature/power/relic
        // ordering and substitutes branch-owned powers. Keep those reactions,
        // interleaving the audited relic callbacks at their native
        // positions. A global postfix would change the callback order.
        while (true)
        {
            var sources = PowerSourceBridge.CapturePending(combat);
            var cards = PowerSourceBridge.CapturePendingCards(combat);
            var changes = combat.DrainPowerAmountChanges();
            if (changes.Length == 0) return false;
            if (sources.Length != changes.Length || cards.Length != changes.Length)
                throw new PredictionUnsupportedException("Hextech power source drain changed length.");
            for (int changeIndex = 0; changeIndex < changes.Length; changeIndex++)
            {
                var change = changes[changeIndex];
                foreach (AbstractModel listener in combat.IterateHookListeners().ToArray())
                {
                    if (listener is PowerModel power && power.Amount <= 0 && power is not HextechTemporarySlowPower) continue;
                    switch (listener)
                    {
                        case AttributeConversionRelicBase conversion:
                            InvokeNativeAttributeChange(conversion, simulator, change, sources[changeIndex], cards[changeIndex]);
                            break;
                        case FuriousGlareRune or GrowingStrongerRune or SpinToWinRune or TwilightVeilRune:
                            InvokeNativeBuffChange((HextechRelicBase)listener, simulator, change, sources[changeIndex], cards[changeIndex]);
                            break;
                        case InstantDeathRune instant:
                            DispatchNativeInstantPower(instant, simulator, change);
                            break;
                        case SweepingBladeRune sweeping:
                            AfterNativeSweepingPower(sweeping, simulator, change, cards[changeIndex]);
                            break;
                        case HextechTemporarySlowPower slow:
                            InvokeNativePower(slow, simulator, model => model.AfterPowerAmountChanged(
                                new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, null));
                            break;
                        case HextechVitalSparkPower spark:
                            InvokeNativePower(spark, simulator, model => model.AfterPowerAmountChanged(
                                new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, cards[changeIndex]?.MutablePreview));
                            break;
                        case ShroudPower shroud when ReferenceEquals(change.Applier, shroud.Owner) && change.Power is DoomPower:
                            simulator.GainBlock(shroud.Owner, shroud.Amount, ValueProp.Unpowered);
                            break;
                        case SleightOfFleshPower sleight when change.Delta != 0
                            && change.Power.GetTypeForAmount(change.Delta) == PowerType.Debuff
                            && change.Power.Owner.IsEnemy && ReferenceEquals(change.Applier, sleight.Owner)
                            && change.Power is not ITemporaryPower
                            && !HextechCombatHooks.ShouldSuppressSleightOfFleshPowerDebuffResponse(true):
                            RequireCompleted(OriginalSleightGuardRunner(() =>
                            {
                                using (simulator.PushDamageSource(CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(SleightOfFleshPower))))
                                    simulator.Damage(change.Power.Owner, sleight.Amount, ValueProp.Unpowered, sleight.Owner);
                                return Task.CompletedTask;
                            }), typeof(SleightOfFleshPower));
                            FlushNativeInstantDoom(simulator);
                            break;
                        case ViciousPower vicious when change.Delta > 0 && change.Power is VulnerablePower
                            && ReferenceEquals(change.Applier, vicious.Owner) && vicious.Owner.Player is { } player:
                            simulator.Draw(player, vicious.Amount);
                            break;
                        case SlapRune slap:
                            InvokeOwnerDebuff(slap, simulator, change);
                            break;
                        case TormentorRune tormentor:
                            InvokeOwnerDebuff(tormentor, simulator, change);
                            break;
                        case AdamantRune adamant:
                            InvokeOwnerDebuff(adamant, simulator, change);
                            break;
                        case BadTasteRune badTaste:
                            InvokeOwnerDebuff(badTaste, simulator, change);
                            break;
                        case CourageOfColossusRune courage:
                            InvokeOwnerDebuff(courage, simulator, change);
                            break;
                        case TauntRune taunt:
                            InvokeOwnerDebuff(taunt, simulator, change);
                            break;
                        case OminousPactRune pact:
                            InvokeOwnerDebuff(pact, simulator, change);
                            break;
                        case HextechMayhemModifier modifier:
                            ModifierMirrors.AfterPowerChanged(modifier, simulator, change, sources[changeIndex], cards[changeIndex]);
                            break;
                    }
                    if (simulator.HasPendingChoice) return false;
                }
            }
        }
    }

    private static bool HasAuditedDebuffReactions(CombatPredictionSimulator simulator, SimulatedCombatState combat)
        => combat.EffectivePowers().Any(power => power is HextechTemporarySlowPower or HextechVitalSparkPower)
            || combat.Players.Any(player => combat.RelicsOf(player).Any(rune => rune is SweepingBladeRune))
            || combat.Players.Any(player => combat.RelicsOf(player).Any(r => r is TormentorRune or SlapRune or AdamantRune or BadTasteRune or CourageOfColossusRune or TauntRune or OminousPactRune or AttributeConversionRelicBase or FuriousGlareRune or GrowingStrongerRune or SpinToWinRune or SerpentsFangRune or InstantDeathRune or TwilightVeilRune))
            || combat.Modifiers.OfType<HextechMayhemModifier>().Any(modifier =>
                ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.Tormentor)
                || ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.BadTaste)
                || ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.CourageOfColossus)
                || ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.Slap)
                || ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.ServantMaster)
                || ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier).Has(MonsterHexKind.Compensation));

    private static void InvokeOwnerDebuff<T>(T rune, CombatPredictionSimulator simulator, SimulatedPowerAmountChange change)
        where T : HextechRelicBase
    {
        // The pinned queue has no card-source field. These reviewed callbacks
        // ignore that parameter; this bridge is not registered for other types.
        RequireCompleted(Invoke(rune, simulator, model => model.AfterPowerAmountChanged(
            new ThrowingPlayerChoiceContext(), change.Power, change.Delta, change.Applier, null)), typeof(T));
    }
}
