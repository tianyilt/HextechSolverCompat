using System.Text.Json;
using CombatSolver;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace HextechCompatLab;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        // Never install fixture behavior outside this explicit isolated runner.
        string? expectedData = System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_LAB_DATA");
        if (System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_LAB") != "1" ||
            string.IsNullOrEmpty(expectedData) || Path.GetFullPath(OS.GetUserDataDir()) != Path.GetFullPath(expectedData) ||
            !expectedData.EndsWith("/.local/headless-instances/hextech/user-data", StringComparison.Ordinal))
            throw new InvalidOperationException("HextechCompatLab must only be loaded in its isolated test profile.");
        const string probeText = "hextech-managed-path-isolated";
        using (var probe = Godot.FileAccess.Open("user://managed-path-probe.txt", Godot.FileAccess.ModeFlags.Write))
        {
            if (probe == null) throw new IOException("Managed user:// path probe could not open its file.");
            probe.StoreString(probeText);
        }
        if (File.ReadAllText(Path.Combine(expectedData, "managed-path-probe.txt")) != probeText ||
            ProjectSettings.GlobalizePath("user://managed-path-probe.txt") != Path.Combine(expectedData, "managed-path-probe.txt"))
            throw new IOException("Managed user:// I/O escaped the isolated workspace profile.");
        GD.Print($"HEXTECH_LAB_PATH_VERIFIED {expectedData}");
        var harmony = new Harmony("HextechCompatLab");
        using (var request = JsonDocument.Parse(File.ReadAllText(System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_FIXTURE")!)))
        {
            if (request.RootElement.TryGetProperty("hextechSteamTagOrder", out var steamOrder) && steamOrder.GetBoolean())
            {
                var target = AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Models.CardModel), "Tags");
                var method = AccessTools.DeclaredMethod(AccessTools.Inner(typeof(HextechPlayerRuneHooks), "CardTagsPatch"), "Postfix");
                new Harmony("Natsuki.HextechRunes").Unpatch(target, HarmonyPatchType.Postfix, "Natsuki.HextechRunes");
                new Harmony("Natsuki.HextechRunes").Patch(target, postfix: new HarmonyMethod(method));
                var sorted = PatchProcessor.GetSortedPatchMethods(target, Harmony.GetPatchInfo(target)!.Postfixes.ToArray());
                if (sorted.Count != 2 || sorted[1] != method) throw new Exception("Steam tag order was not reproduced.");
                GD.Print("HEXTECH_STEAM_TAG_ORDER_VERIFIED ritsu_first=true hextech_second=true");
            }
        }
        harmony.Patch(
            AccessTools.Method(typeof(HextechRuneSelectionCoordinator), "HandleActSelection",
                [typeof(RunState), typeof(HextechMayhemModifier)]),
            prefix: new HarmonyMethod(typeof(Entry), nameof(SelectFixtureHexes)));
        harmony.Patch(AccessTools.Method(typeof(HextechMayhemModifier), "BeforeCombatStart"),
            prefix: new HarmonyMethod(typeof(Entry), nameof(PrepareCombat)));
        FixtureAssertions.Install(harmony);
        harmony.Patch(AccessTools.Method(AccessTools.Inner(typeof(UnattendedTestRunner), "ScenarioBuilder"), "BeginGeneratedSetupChoices"),
            postfix: new HarmonyMethod(typeof(Entry), nameof(SetupChoices)));
    }

    private sealed class SetupChoiceScope(IDisposable inner, GeneratedScenarioCardSelector selector) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            if (selector.Choices.Count == 0) throw new Exception("Native opening choice was not exercised.");
            GD.Print($"HEXTECH_SETUP_CHOICES_VERIFIED choices={selector.Choices.Count} setup_scope_only=true native=true");
        }
    }

    private static void SetupChoices(ref IDisposable? __result)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_FIXTURE")!));
        if (!document.RootElement.TryGetProperty("hextechSetupChoices", out var flag) || !flag.GetBoolean()) return;
        if (__result is not null) throw new Exception("Fixture cannot replace an existing setup selector.");
        var selector = new GeneratedScenarioCardSelector(null);
        __result = new SetupChoiceScope(MegaCrit.Sts2.Core.Commands.CardSelectCmd.PushSelector(selector, localOnly: true), selector);
    }

    private static bool SelectFixtureHexes(RunState runState, HextechMayhemModifier modifier, ref Task __result)
    {
        PrepareCombat(modifier);
        __result = Task.CompletedTask;
        return false;
    }

    private static void PrepareCombat(HextechMayhemModifier __instance)
    {
        var modifier = __instance;
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("Fixture selection requires an active unattended request.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            System.Environment.GetEnvironmentVariable("HEXTECH_COMPAT_FIXTURE")
                ?? throw new InvalidOperationException("Fixture path missing.")));
        for (int act = 0; act < modifier.StageCount; act++)
        {
            modifier.ActState.SetMonsterHexes(act, []);
            modifier.ActState.SetRarity(act, HextechRarityTier.Silver);
            // Future acts are not resolved in a real opening run. Marking the
            // next act resolved makes upstream infer an endless-loop rewind
            // on room entry and clear genuine combat-scoped timer/counters.
            modifier.SetStageResolved(act, act <= modifier.GetCurrentStageIndex());
        }
        if (document.RootElement.TryGetProperty("hextechEnemyHexes", out JsonElement hexes))
            foreach (JsonElement value in hexes.EnumerateArray())
                modifier.DebugAddMonsterHex(Enum.Parse<MonsterHexKind>(value.GetString()!, ignoreCase: false));
        if (document.RootElement.TryGetProperty("hextechEnemyTierFloor", out JsonElement tier))
            modifier.SavedMonsterHexStrengthTierFloor = tier.GetInt32();
        GD.Print($"HEXTECH_FIXTURE selected=[{string.Join(',', modifier.GetActiveMonsterHexes())}]");
    }
}
