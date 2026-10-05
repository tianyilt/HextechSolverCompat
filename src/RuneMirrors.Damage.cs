using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class RuneMirrors
{
    private static void RegisterDamage()
    {
        RegisterBase<BigStrengthRune>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<BigStrengthRune>((r, c) =>
            IsOwnerDamage(r, c) ? r.DynamicVars["DamageMultiplier"].BaseValue : 1m);
        RegisterBase<HeavyHitterRune>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<HeavyHitterRune>((r, c) =>
            IsOwnerDamage(r, c) ? 1m + Math.Floor(c.State.GetCreature(r.Owner.Creature).MaxHp / 6m) / 100m : 1m);
        RegisterBase<ProteinShakeRune>();
        ModifyBlockMultiplicativeMirrors.Registry.Register<ProteinShakeRune>((r, c) =>
            c.Target == r.Owner.Creature ? ProteinMultiplier(r, c.State.GetCreature(c.Target).MaxHp) : 1m);
        RegisterBase<MoreTheMerrierRune>();
        ModifyBlockMultiplicativeMirrors.Registry.Register<MoreTheMerrierRune>((r, c) =>
            c.Target == r.Owner.Creature ? MoreMultiplier(r, (SimulatedCombatState)c.CombatState) : 1m);
        ModifyDamageMirrors.MultiplicativeRegistry.Register<MoreTheMerrierRune>((r, c) =>
            IsOwnerDamage(r, c) ? MoreMultiplier(r, (SimulatedCombatState)c.CombatState) : 1m);
        RegisterBase<GlassCannonRune>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<GlassCannonRune>((r, c) =>
            IsOwnerDamage(r, c) ? r.DynamicVars["DamageMultiplier"].BaseValue : 1m);
        RegisterBase<AdvanceToRetreatRune>();
        AfterDamageGivenMirrors.Registry.Register<AdvanceToRetreatRune>((r, c) =>
        {
            var combat = (SimulatedCombatState)c.CombatState;
            // Native tests TotalDamage, so a fully blocked hit still qualifies.
            // The attack card must belong to the holder; move damage has no card.
            if (!c.State.GetCreature(r.Owner.Creature).IsAlive || c.Target.Side != CombatSide.Enemy
                || c.Result.TotalDamage <= 0 || c.Source?.Preview.Owner != r.Owner
                || !IsAttack(c.Source.Preview, combat) || combat.GetAmount<VulnerablePower>(c.Target) <= 0) return;
            c.Simulator.GainBlock(r.Owner.Creature, r.DynamicVars.Block);
        });
    }

    private static decimal ProteinMultiplier(ProteinShakeRune rune, int maxHp) =>
        1m + Math.Floor(maxHp / rune.DynamicVars["MaxHpPerStep"].BaseValue) * rune.DynamicVars["SustainPercentPerStep"].BaseValue / 100m;
    private static decimal MoreMultiplier(MoreTheMerrierRune rune, SimulatedCombatState combat) =>
        1m + combat.RelicsOf(rune.Owner).Count * rune.DynamicVars["PercentPerRelic"].BaseValue / 100m;

    private static bool IsOwnerDamage(HextechRelicBase r, ModifyDamageMirrorContext c) =>
        (c.Target == null || c.Target.Side == CombatSide.Enemy) &&
        (c.Dealer == r.Owner.Creature || c.Dealer?.PetOwner == r.Owner ||
         (c.Dealer?.Side != CombatSide.Player && c.CardSource?.Preview.Owner == r.Owner));
}
