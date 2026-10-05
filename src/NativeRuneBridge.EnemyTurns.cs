using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private sealed record NativeEnemyTurnScope(HextechMayhemModifier Modifier, EnemyState State)
    { internal int Tier => State.StrengthTier; }
    [ThreadStatic] private static NativeEnemyTurnScope? _nativeEnemyTurn;
    private static readonly Dictionary<MethodBase, (int Blocks, int Heals, int Hp, int MaxHp, int Enemies)> EnemyTurnContracts = [];
    private static readonly List<MethodInfo> EnemyTurnCallbacks = [];
    private static readonly Dictionary<MethodBase, MethodInfo> EnemyTurnExpectedTranspilers = [];
    private delegate int NativeContextTierGetter(ref HextechEnemyHexContext context, MonsterHexKind kind);
    private static readonly NativeContextTierGetter OriginalContextTier = AccessTools.Method(typeof(HextechEnemyHexContext), "GetStrengthTier")
        .CreateDelegate<NativeContextTierGetter>();
    private sealed record NativeEnemyTurnHandler(MonsterHexKind Kind, Type Type,
        Func<HextechEnemyHexContext, ICombatState, IReadOnlyList<Creature>, Task> BeforePlayer,
        Func<HextechEnemyHexContext, ICombatState, IReadOnlyList<Creature>, IReadOnlyList<Creature>, Task> BeforeEnemy);
    private static NativeEnemyTurnHandler[] NativeEnemyTurnEffects = [];

    internal static void RegisterNativeEnemyTurnFamily(Harmony harmony)
    {
        RegisterOutsideBattleEnemyHexes();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(int), typeof(decimal)]));
        // Exact reviewed stateless handlers, in their original dispatcher order.
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var reviewed = effects.Where(effect => effect is SturdyEnemyHex or SonataEnemyHex or MiserableFateEnemyHex
            or DivineInterventionEnemyHex or CerberusEnemyHex or LeafSlimeEnemyHex or SlimedBerserkerEnemyHex
            or MyteEnemyHex or HauntedShipEnemyHex or OmegaEnemyHex or LagavulinMatriarchEnemyHex
            or FrostWraithEnemyHex or DoomsdayEnemyHex or BrutalityEnemyHex
            or EscapePlanEnemyHex or RepulsorEnemyHex or DawnbringersResolveEnemyHex or FeelTheBurnEnemyHex or MikaelsBlessingEnemyHex
            or NightstalkingEnemyHex or WarmogsSpiritEnemyHex or SwiftAndSafeEnemyHex or DizzySpinningEnemyHex
            or TanksShieldEnemyHex or AncientWineEnemyHex or MonarchsGazeEnemyHex or MirrorReflectionEnemyHex
            or BloodIdolEnemyHex or MountainSoulEnemyHex or SpeedDemonEnemyHex or DevilsDanceEnemyHex
            or QueenEnemyHex or HandOfBaronEnemyHex or OmniDragonSoulEnemyHex
            or TankEngineEnemyHex or GoldenSpatulaEnemyHex or MadScientistEnemyHex or UnmovableMountainEnemyHex or ShrinkRayEnemyHex or BloodArmorEnemyHex or FinalFormEnemyHex
            or FeyMagicEnemyHex or PorcupineEnemyHex or GoldrendEnemyHex or BackToBasicsEnemyHex
            or ShrinkEngineEnemyHex or SingularityAIEnemyHex or MysteryEnemyHex).ToArray();
        if (reviewed.Length != 48) throw new InvalidOperationException("Native enemy turn catalogue changed.");
        foreach (var effect in reviewed)
            if (AccessTools.GetDeclaredFields(effect.GetType()).Any(field => !field.IsStatic))
                throw new InvalidOperationException("Native enemy turn handler acquired instance state.");
        // Bind internal virtual callbacks once, without changing the shipped DLL.
        NativeEnemyTurnEffects = reviewed.Select(effect => new NativeEnemyTurnHandler(
            AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(), effect.GetType(),
            AccessTools.Method(effect.GetType(), "BeforePlayerSideTurnStart")
                .CreateDelegate<Func<HextechEnemyHexContext, ICombatState, IReadOnlyList<Creature>, Task>>(effect),
            AccessTools.Method(effect.GetType(), "BeforeEnemySideTurnStart")
                .CreateDelegate<Func<HextechEnemyHexContext, ICombatState, IReadOnlyList<Creature>, IReadOnlyList<Creature>, Task>>(effect))).ToArray();
        // TierValue's small nested getters may have been JIT-inlined before this
        // adapter initializes. Rewrite its actual query before recompiling the
        // outer async callbacks; a detour of the tiny modifier getter is insufficient.
        foreach (var query in AccessTools.GetDeclaredMethods(typeof(HextechEnemyHexContext)).Where(method => method.Name == "TierValue"))
        {
            EnemyTurnExpectedTranspilers.Add(query, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeContextTier)));
            harmony.Patch(query, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNativeContextTier)));
        }
        PatchNativeEnemyTurn<SturdyEnemyHex>(harmony, "BeforeEnemySideTurnStart", (0, 1, 1, 2, 0));
        PatchNativeEnemyTurn<SonataEnemyHex>(harmony, "BeforeEnemySideTurnStart", (0, 1, 0, 1, 0));
        PatchNativeEnemyTurn<SonataEnemyHex>(harmony, "BeforePlayerSideTurnStart", (1, 0, 0, 1, 1));
        PatchNativeEnemyTurn<MiserableFateEnemyHex>(harmony, "BeforePlayerSideTurnStart", (1, 0, 1, 1, 1));
        RegisterNativeEnemyPeriodicFamily(harmony);
        RegisterNativeEnemyDebuffTurns(harmony);
        RegisterNativeEnemyBrutality(harmony);
        RegisterNativeEnemyHealthThresholds(harmony);
        RegisterNativeEnemyDraw(harmony);
        RegisterNativeEnemyPlayed(harmony);
        RegisterNativeEnemyHpLifecycle(harmony);
        RegisterNativeEnemyHitEffects(harmony);
        RegisterNativeEnemyHitMemory(harmony);
        RegisterNativeEnemyPileBoundaries(harmony);
        RegisterNativeEnemyFlowLimits(harmony);
        ValidateNativeEnemyTurnContracts();
        harmony.Patch(AccessTools.Method(typeof(AdaptedCardOnPlayMirrors), "CaptureLiveStamp"),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AppendNativeEnemyTurnComposition)));
        CompatibilityGuard.EnemyHexes.UnionWith([MonsterHexKind.Sonata, MonsterHexKind.MiserableFate]);
    }

    private static void PatchNativeEnemyTurn<T>(Harmony harmony, string name, (int Blocks, int Heals, int Hp, int MaxHp, int Enemies) calls)
    {
        var callback = AccessTools.DeclaredMethod(typeof(T), name);
        EnemyTurnCallbacks.Add(callback);
        var machine = callback.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("Native enemy turn callback shape changed.");
        var target = AccessTools.Method(machine, "MoveNext");
        EnemyTurnContracts.Add(target, calls);
        EnemyTurnExpectedTranspilers.Add(target, AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteNativeEnemyTurnCommands)));
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNativeEnemyTurnCommands)));
    }

    internal static void ValidateNativeEnemyTurnContracts()
    {
        foreach (var target in EnemyTurnCallbacks.Cast<MethodBase>().Concat(EnemyTurnExpectedTranspilers.Keys))
        {
            var patches = Harmony.GetPatchInfo(target);
            bool machine = EnemyTurnExpectedTranspilers.TryGetValue(target, out var expected);
            if (patches is null)
            {
                if (machine) throw new PredictionUnsupportedException("Native enemy turn transpiler is missing.");
                continue;
            }
            if (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 || patches.Finalizers.Count != 0
                || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0
                || patches.Transpilers.Count != (machine ? 1 : 0)
                || machine && (patches.Transpilers[0].owner != "HextechSolverCompat"
                    || patches.Transpilers[0].PatchMethod != expected
                    || patches.Transpilers[0].priority != Priority.Normal || patches.Transpilers[0].before.Length != 0
                    || patches.Transpilers[0].after.Length != 0))
                throw new PredictionUnsupportedException($"Unreviewed native enemy turn composition: {target.DeclaringType?.Name}.{target.Name}.");
        }
    }

    private static void AppendNativeEnemyTurnComposition(ref string? __result)
    {
        if (__result is null) return;
        string signature = string.Join(';', EnemyTurnCallbacks.Cast<MethodInfo>().Concat(EnemyTurnExpectedTranspilers.Keys.Cast<MethodInfo>())
            .OrderBy(method => method.DeclaringType!.FullName + ":" + method.Name, StringComparer.Ordinal)
            .Select(method => AdaptedCardOnPlayMirrors.DescribeActual(method, Harmony.GetPatchInfo(method), includeIndex: true)));
        __result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(__result + ":native-enemy-turns-v1:" + signature)));
    }

    private static IEnumerable<CodeInstruction> RewriteNativeEnemyTurnCommands(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        (int Blocks, int Heals, int Hp, int MaxHp, int Enemies) found = (0, 0, 0, 0, 0);
        var hp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CurrentHp));
        var maxHp = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.MaxHp));
        var fraction = AccessTools.Method(typeof(HextechEnemyHexContext), "FractionOfMaxHp", [typeof(Creature), typeof(decimal)]);
        var enemies = AccessTools.Method(typeof(HextechEnemyHexContext), "GetAliveEnemies");
        foreach (var instruction in instructions)
        {
            string? replacement = null;
            if (instruction.Calls(NativeDecimalBlock)) { found.Blocks++; replacement = nameof(GainNativeBlock); }
            else if (instruction.Calls(NativeHeal)) { found.Heals++; replacement = nameof(HealNative); }
            // Rewriting the actual read avoids an already-inlined native getter.
            else if (instruction.Calls(hp)) { found.Hp++; replacement = nameof(NativeEnemyCurrentHp); }
            else if (instruction.Calls(maxHp)) { found.MaxHp++; replacement = nameof(NativeEnemyMaxHp); }
            else if (instruction.Calls(fraction)) { found.MaxHp++; replacement = nameof(NativeEnemyHpFraction); }
            else if (instruction.Calls(enemies)) { found.Enemies++; replacement = nameof(NativeContextAliveEnemies); }
            if (replacement is not null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), replacement);
            }
            yield return instruction;
        }
        if (found != EnemyTurnContracts[__originalMethod])
            throw new InvalidOperationException($"Native enemy turn commands changed: {__originalMethod} {found}.");
    }

    private static IEnumerable<CodeInstruction> RewriteNativeContextTier(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var query = AccessTools.Method(typeof(HextechEnemyHexContext), "GetStrengthTier");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(query))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeCapturedContextTier));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException($"Native context tier query changed: {count}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NativeCapturedContextTier(ref HextechEnemyHexContext context, MonsterHexKind kind)
        => _simulator is not null && _nativeEnemyTurn is { } scope ? scope.Tier : OriginalContextTier(ref context, kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NativeEnemyCurrentHp(Creature creature)
        => _simulator is { } sim ? sim.State.GetCreature(creature).CurrentHp : creature.CurrentHp;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NativeEnemyMaxHp(Creature creature)
        => _simulator is { } sim ? sim.State.GetCreature(creature).MaxHp : creature.MaxHp;

    private static int NativeEnemyHpFraction(Creature creature, decimal fraction)
        => HextechEnemyHexContext.FractionOfMaxHp(NativeEnemyMaxHp(creature), fraction);

    private static IReadOnlyList<Creature> NativeContextAliveEnemies(ref HextechEnemyHexContext context, ICombatState combat)
        => _simulator is { } sim
            ? sim.State.CombatState.Enemies.Where(enemy => sim.State.GetCreature(enemy).IsAlive).ToArray()
            : HextechCombatCreatureHelper.GetAliveEnemies(combat);

    internal static void DispatchNativeEnemyTurns(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, CombatSide side)
    {
        var previous = _simulator;
        var previousTurn = _nativeEnemyTurn;
        _simulator = simulator;
        _nativeEnemyTurn = new(modifier, state);
        try
        {
            var combat = simulator.State.CombatState;
            var context = new HextechEnemyHexContext(modifier);
            var players = combat.PlayerCreatures.Where(creature => simulator.State.GetCreature(creature).IsAlive).ToArray();
            var enemies = combat.Enemies.Where(creature => simulator.State.GetCreature(creature).IsAlive).ToArray();
            foreach (var effect in NativeEnemyTurnEffects)
            {
                if (!state.Has(effect.Kind)) continue;
                var previousSeed = _stableGeneration;
                try
                {
                    if (effect.Kind == MonsterHexKind.Mystery)
                        _stableGeneration = state.TransformationSeed
                            ?? throw new PredictionUnsupportedException("Mystery transformation pools were not captured.");
                    var task = side == CombatSide.Player
                        ? effect.BeforePlayer(context, combat, players)
                        : effect.BeforeEnemy(context, combat, players, enemies);
                    RequireCompleted(task, effect.Type);
                }
                finally { _stableGeneration = previousSeed; }
                if (simulator.HasPendingChoice) break;
            }
        }
        finally { _simulator = previous; _nativeEnemyTurn = previousTurn; }
    }
}
