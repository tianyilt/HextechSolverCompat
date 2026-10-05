using HextechRunes;
using HarmonyLib;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterPersistentKeywordCards(Harmony harmony)
    {
        // These native keyword callbacks inspect only the immutable owner/type
        // and the supplied keyword set. The solver already dispatches the native
        // keyword hook with each branch's card preview and listener snapshot.
        RegisterState<RebootUpgradeRune>();
        RuneMirrors.RegisterNativeBase<RebootUpgradeRune>();
        RegisterState<RageUpgradeRune>();
        RuneMirrors.RegisterNativeBase<RageUpgradeRune>();
        RegisterState<CorrosiveWaveUpgradeRune>();
        RuneMirrors.RegisterNativeBase<CorrosiveWaveUpgradeRune>();
        RegisterState<OblivionUpgradeRune>();
        RuneMirrors.RegisterNativeBase<OblivionUpgradeRune>();
        RegisterState<ReflectUpgradeRune>();
        RuneMirrors.RegisterNativeBase<ReflectUpgradeRune>();
        PowerExpiryBridge.Register(harmony);
    }
}
