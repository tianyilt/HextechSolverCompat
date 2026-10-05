using System.Reflection;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using HextechRunes;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    [Flags]
    private enum NativeQueries { Energy = 1, LateEnergy = 2, Stars = 4, DamageMultiplier = 8, DamageAdditive = 16 }

    private static void RegisterNativeQueryFamilies()
    {
        // One implementation per hook, with the original rune supplying its
        // predicates and arithmetic. Entries require source/call-graph review;
        // matching a method name alone does not establish eligibility.
        RegisterNativeQueries<SwordIntentRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterNativeQueries<TrickLicenseRune>(NativeQueries.Energy | NativeQueries.Stars);
        RegisterNativeQueries<EnlightenmentRune>(NativeQueries.LateEnergy);
        RegisterNativeQueries<BrandUpgradeRune>(NativeQueries.DamageMultiplier, afterCardPlayed: true);
    }

    private static void RegisterNativeQueries<T>(NativeQueries queries, bool afterCardPlayed = false)
        where T : HextechRelicBase
    {
        RequireQueryFamilyContract<T>(queries, afterCardPlayed);
        RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        RegisterNativeQueryCallbacks<T>(queries);
        if (afterCardPlayed) RegisterAfterCardPlayedCallback<T>();
    }

    private static void RegisterNativeQueryCallbacks<T>(NativeQueries queries) where T : HextechRelicBase
    {
        if (queries.HasFlag(NativeQueries.Energy))
            ModifyEnergyCostInCombatMirrors.Registry.Register<T>((rune, context) =>
                Invoke(rune, context.Simulator, model =>
                {
                    model.TryModifyEnergyCostInCombat(context.Card.Preview, context.Cost, out decimal cost);
                    return cost;
                }));
        if (queries.HasFlag(NativeQueries.LateEnergy))
            ModifyEnergyCostInCombatMirrors.LateRegistry.Register<T>((rune, context) =>
                Invoke(rune, context.Simulator, model =>
                {
                    model.TryModifyEnergyCostInCombatLate(context.Card.Preview, context.Cost, out decimal cost);
                    return cost;
                }));
        if (queries.HasFlag(NativeQueries.Stars))
            ModifyStarCostMirrors.Registry.Register<T>((rune, context) =>
                Invoke(rune, context.Simulator, model =>
                {
                    model.TryModifyStarCost(context.Card.Preview, context.Cost, out decimal cost);
                    return cost;
                }));
        if (queries.HasFlag(NativeQueries.DamageMultiplier))
            ModifyDamageMirrors.MultiplicativeRegistry.Register<T>((rune, context) =>
                Invoke(rune, context.Simulator, model => model.ModifyDamageMultiplicativeCompat(
                    context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview)));
        if (queries.HasFlag(NativeQueries.DamageAdditive))
            ModifyDamageMirrors.AdditiveRegistry.Register<T>((rune, context) =>
                Invoke(rune, context.Simulator, model => model.ModifyDamageAdditiveCompat(
                    context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview)));
    }

    private static void RequireQueryFamilyContract<T>(NativeQueries queries, bool afterCardPlayed)
        where T : HextechRelicBase
    {
        HashSet<string> allowed = ["IsAvailableForPlayer", "IsAvailableForCharacter", "AfterObtained",
            "GetSelectionFooterText", "MeetsCardAvailabilityRequirement"];
        if (queries.HasFlag(NativeQueries.Energy)) allowed.Add("TryModifyEnergyCostInCombat");
        if (queries.HasFlag(NativeQueries.LateEnergy)) allowed.Add("TryModifyEnergyCostInCombatLate");
        if (queries.HasFlag(NativeQueries.Stars)) allowed.Add("TryModifyStarCost");
        if (queries.HasFlag(NativeQueries.DamageMultiplier)) allowed.Add("ModifyDamageMultiplicativeCompat");
        if (queries.HasFlag(NativeQueries.DamageAdditive)) allowed.Add("ModifyDamageAdditiveCompat");
        if (afterCardPlayed) allowed.Add("AfterCardPlayed");
        for (Type? type = typeof(T); type is not null && type != typeof(HextechRelicBase); type = type.BaseType)
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (method.IsVirtual && !method.IsSpecialName && method.GetBaseDefinition() != method
                    && !allowed.Contains(method.Name))
                    throw new InvalidOperationException($"{typeof(T).Name}.{method.Name} is outside its reviewed native query family.");
    }
}
