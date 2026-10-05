using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics;
using CombatSolver;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    // Wall-clock timer events are external inputs, not deterministic future
    // turn effects. Preserve the native timer and freeze each searched root.
    // A completed native heal invalidates the route before another action can
    // start; the next playable native boundary supplies a fresh root.
    private sealed class NativeNatureEvents
    {
        internal int Epoch;
        internal bool ResumeRequested;
        internal bool DeployWhenReady;
        internal int Replans;
    }
    private static readonly ConditionalWeakTable<ICombatState, NativeNatureEvents> NativeNatureEventStates = new();
    private static readonly FieldInfo NativePlayerNatureTimer = AccessTools.Field(typeof(NatureIsHealingRune), "_timer");
    private static readonly FieldInfo NativePlayerNatureBusy = AccessTools.Field(typeof(NatureIsHealingRune), "_healing");
    private static void RegisterNativePlayerNature(Harmony harmony)
    {
        // Single-player Nature uses real time. Prediction never creates or
        // advances its timer; completed native events supply a fresh root.
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NatureIsHealingRune), "HealOwner"),
            Site(NativeHeal, nameof(HealNativeNatureTimer)));
        RegisterState<NatureIsHealingRune>(); RuneMirrors.RegisterNativeBase<NatureIsHealingRune>();
        RegisterRegisteredTurn<NatureIsHealingRune>();
        foreach (string name in new[] { "AfterPlayerTurnStart", "BeforeCombatStart", "AfterCombatEnd", "OnTimerTimeout", "ShouldHeal" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(NatureIsHealingRune), name));
        foreach (string name in new[] { "StartTimer", "StopTimer" })
        {
            var method = AccessTools.DeclaredMethod(typeof(NatureIsHealingRune), name);
            var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ObserveNativeNatureTimerBoundary));
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            NativeCallbackContracts.AddNativePrefix(method, prefix, "HextechSolverCompat", Priority.Normal);
        }
    }

    private static IEnumerable<Godot.Timer> ActiveNativeNatureTimers(ICombatState state)
    {
        if (state.Modifiers.OfType<HextechMayhemModifier>().Any(modifier => modifier.HasActiveMonsterHex(MonsterHexKind.NatureIsHealing))
            && NativeNatureEffect._timer is { } enemyTimer) yield return enemyTimer;
        foreach (var rune in state.Players.SelectMany(player => player.Relics).OfType<NatureIsHealingRune>())
            if (NativePlayerNatureTimer.GetValue(rune) is Godot.Timer playerTimer) yield return playerTimer;
    }
    private static readonly NatureIsHealingEnemyHex NativeNatureEffect = ((IReadOnlyList<HextechEnemyHexEffect>)
        AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!)
        .OfType<NatureIsHealingEnemyHex>().Single();
    private static void RegisterNativeNatureHealing(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(NatureIsHealingEnemyHex), "OnTimerTimeout"),
            Site(NativeHeal, nameof(HealNativeNatureTimer)));
        foreach (string name in new[] { "get_Kind", "ResetRunScopedState", "ApplyCombatStartToEnemy",
            "BeforeEnemySideTurnStart", "AfterCombatEnd", "StartTimer", "StopTimer", "TryGetAliveEnemies" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(NatureIsHealingEnemyHex), name));
        foreach (string name in new[] { "ApplyCombatStartToEnemy", "StartTimer", "StopTimer" })
        {
            var method = AccessTools.DeclaredMethod(typeof(NatureIsHealingEnemyHex), name);
            var prefix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ObserveNativeNatureTimerBoundary));
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            NativeCallbackContracts.AddNativePrefix(method, prefix, "HextechSolverCompat", Priority.Normal);
        }
        var monitor = AccessTools.DeclaredMethod(typeof(SolverController), "MonitorCombatPresence");
        var postfix = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ResumeAfterNativeNatureTimer));
        harmony.Patch(monitor, postfix: new HarmonyMethod(postfix));
        NativeCallbackContracts.AddNativePostfix(monitor, postfix, "HextechSolverCompat", Priority.Normal);
        var capture = AccessTools.DeclaredMethod(typeof(SolverController), "CaptureSearchPolicy");
        var constrain = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(ConstrainNativeNatureSearch));
        harmony.Patch(capture, postfix: new HarmonyMethod(constrain));
        NativeCallbackContracts.AddNativePostfix(capture, constrain, "HextechSolverCompat", Priority.Normal);
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.NatureIsHealing);
    }

    private static void ObserveNativeNatureTimerBoundary(MethodBase __originalMethod)
    {
        if (_simulator is not null) throw new InvalidOperationException("Prediction attempted to mutate the native Nature timer.");
        if (Environment.GetEnvironmentVariable("HEXTECH_COMPAT_FIXTURE") is not null)
            Godot.GD.Print($"HEXTECH_NATIVE_NATURE_BOUNDARY method={__originalMethod.Name} callers={string.Join(',', new StackTrace().GetFrames().Skip(1).Take(6).Select(frame => frame.GetMethod()?.DeclaringType?.Name + "." + frame.GetMethod()?.Name))}");
    }

    private static void ConstrainNativeNatureSearch(CombatState state, ref SearchPolicySnapshot __result)
    {
        if (_simulator is not null) return;
        var remaining = ActiveNativeNatureTimers(state).Where(timer => Godot.GodotObject.IsInstanceValid(timer)
            && !timer.IsStopped() && !timer.Paused).Select(timer => timer.TimeLeft).ToArray();
        if (remaining.Length == 0) return;
        // Reserve half the remaining real interval for root capture, rendering
        // and issuing native actions. The original request budget ledger shares
        // this allowance across Beam, scouts, refinements and potion audits.
        int limit = Math.Max(100, (int)Math.Floor(remaining.Min() * 500));
        int requested = __result.BudgetOverrideMilliseconds ?? __result.Profile.SoftTimeBudgetMilliseconds;
        int budget = Math.Min(requested, limit);
        __result = __result with
        {
            BudgetOverrideMilliseconds = budget,
            EarlyTurnExplorationBudgetMilliseconds = Math.Min(__result.EarlyTurnExplorationBudgetMilliseconds, budget)
        };
    }

    private static async Task HealNativeNatureTimer(Creature target, decimal amount, bool showAnim)
    {
        if (_simulator is not null)
            throw new InvalidOperationException("A real-time native timer must never run inside a prediction.");
        var state = target.CombatState;
        // A native card can damage the same enemy while the timer's heal
        // animation awaits. Net HP at the end is not proof no heal happened.
        bool canChange = target.IsAlive && amount > 0 && target.CurrentHp < target.MaxHp;
        await CreatureCmd.Heal(target, amount, showAnim);
        if (!canChange || state is null || !ReferenceEquals(target.CombatState, state)) return;
        var events = NativeNatureEventStates.GetOrCreateValue(state);
        events.Epoch++;
        var session = SolverController._combat;
        if (!ReferenceEquals(session.State, state)) return;
        bool resume = !SolverController._solverDisabled && !session.AutomaticSearchPaused && !session.RouteFrozen
            && (SolverController.IsSearching || SolverController.IsDeploying || session.LatestResult is not null || session.FullAutoEnabled);
        events.DeployWhenReady |= SolverController.IsDeploying || SolverController._search?.DeployWhenReady == true;
        events.ResumeRequested |= resume;
        SolverController.CancelSearch();
        SolverController.CancelDeployment();
        session.LatestResult = null;
        session.LatestStamp = null;
        session.ContinuationSource = null;
        session.PendingCompleteProjectionBaseline = null;
        session.PendingManualProjectionBaseline = null;
    }

    private static void ResumeAfterNativeNatureTimer()
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state is null || !NativeNatureEventStates.TryGetValue(state, out var events) || !events.ResumeRequested) return;
        var session = SolverController._combat;
        if (!ReferenceEquals(session.State, state) || !CombatManager.Instance.IsInProgress
            || SolverController._solverDisabled || session.AutomaticSearchPaused || session.RouteFrozen)
        { events.ResumeRequested = false; events.DeployWhenReady = false; return; }
        if (NativeNatureEffect._healing || state.Players.SelectMany(player => player.Relics).OfType<NatureIsHealingRune>()
                .Any(rune => (bool)NativePlayerNatureBusy.GetValue(rune)!)
            || SolverController.IsSearching || SolverController.IsDeploying || NGame.Instance is not { } host
            || state.Players.Count != 1 || state.Players[0].PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || RunManager.Instance.ActionExecutor.CurrentlyRunningAction is not null
            || !RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            return;
        bool deploy = events.DeployWhenReady;
        events.ResumeRequested = false; events.DeployWhenReady = false;
        events.Replans++;
        SolverController.RequestSearch(host, (CombatState)state, SearchReason.DeploymentDrift, deployWhenReady: deploy);
    }
}
