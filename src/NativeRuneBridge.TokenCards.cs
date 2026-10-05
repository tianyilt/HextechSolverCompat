using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterTokenCards(Harmony harmony)
    {
        var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(ReprogramCard))
            ?? throw new InvalidOperationException("Reprogram OnPlay missing.");
        var stateMachine = onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("Reprogram async callback shape changed.");
        var moveNext = AccessTools.Method(stateMachine, "MoveNext");
        ExpectedPowerCalls.Add(moveNext, 3);
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
        RegisterNativeTokenCard<ReprogramCard>(onPlay);
        RuneMirrors.RegisterNativeBase<ReprogramRune>();
        RegisterSearingAttack(harmony);
        RegisterWhiteHole(harmony);
        RegisterBladeWaltz(harmony);
        RegisterQuantumComputing(harmony);
    }

    private static void RegisterBladeWaltz(Harmony harmony)
    {
        var onPlay = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(BladeWaltzCard))
            ?? throw new InvalidOperationException("BladeWaltz OnPlay missing.");
        var machine = onPlay.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("BladeWaltz async callback shape changed.");
        var moveNext = AccessTools.Method(machine, "MoveNext");
        ExpectedPowerCalls.Add(moveNext, 1);
        harmony.Patch(moveNext,
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewritePowerCommands)));
        harmony.Patch(moveNext,
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteNativeAttack)));
        RegisterNativeTokenCard<BladeWaltzCard>(onPlay);
        RuneMirrors.RegisterNativeBase<BladeWaltzRune>();
    }

    private static void RegisterNativeTokenCard<T>(MethodInfo onPlay) where T : CardModel
    {
        CardOnPlayMirrors.Registry.Register<T>((card, context) =>
        {
            var previous = _simulator;
            _simulator = context.Simulator;
            try
            {
                RequireCompleted(() => (Task)onPlay.Invoke(card, [new ThrowingPlayerChoiceContext(), context.CardPlay])!, typeof(T));
            }
            finally { _simulator = previous; }
        });
        CompatibilityGuard.Cards.Add(typeof(T));
    }
}
