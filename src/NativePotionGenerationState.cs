using CombatSolver;
using CombatSolver.Engine.Common;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace HextechSolverCompat;

// Immutable inputs to the native stable picker. Slot contents remain in the
// solver's owned potion state; this object never owns mutable live potions.
internal sealed class NativePotionGenerationState
{
    private readonly string _seed;
    private readonly int _act;
    private readonly int _floor;
    private readonly string _playerKey;
    private readonly string _poolKey;
    internal readonly IReadOnlyList<PotionModel> Candidates;

    internal NativePotionGenerationState(Player player)
    {
        var run = (RunState)player.RunState;
        _seed = run.Rng.StringSeed;
        _act = run.CurrentActIndex;
        _floor = run.TotalFloor;
        _playerKey = HextechStableRandom.PlayerKey(player);
        var candidates = HextechGameApiCompat.GetPotionOptions(player).ToArray();
        if (candidates.Any(potion => potion.IsMutable || !ReferenceEquals(potion, potion.CanonicalInstance)))
            throw new PredictionUnsupportedException("Chemtech potion options contain a mutable model.");
        Candidates = Array.AsReadOnly(candidates);
        _poolKey = string.Join(",", candidates.Select(HextechStableRandom.PotionKey));
    }

    internal int Index(int count, IEnumerable<string?> salt)
    {
        List<string?> parts = [_seed, "|act:", _act.ToString(), "|floor:", _floor.ToString()];
        foreach (var part in salt) { parts.Add("|"); parts.Add(part ?? ""); }
        return HextechStableRandom.IndexFromRawParts(count, parts.ToArray());
    }

    internal void Write(ref ModelPredictionStateWriter writer)
    {
        writer.Add("nativePotionSeed", _seed);
        writer.Add("nativePotionAct", _act);
        writer.Add("nativePotionFloor", _floor);
        writer.Add("nativePotionPlayer", _playerKey);
        writer.Add("nativePotionPool", _poolKey);
    }
}
