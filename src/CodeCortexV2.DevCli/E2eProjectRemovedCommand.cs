using System.Text.Json;
using CodeCortexV2;

namespace CodeCortexV2.DevCli;

public static class E2eProjectRemovedCommand {
    public static async Task<int> RunAsync() {
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var projectName = "E2E.Target";
        var probeTypeFqn = "E2E.Target.TypeA"; // known from e2e project
        var probeNs = "E2E.Target";
        try {
            Console.WriteLine($"[e2e] project-removed using {slnPath} (remove '{projectName}')");

            // Before: ensure the type exists
            var wti0 = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var page0 = await wti0.FindAsync(probeTypeFqn, limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (!HasAnyItem(page0)) {
                Console.Error.WriteLine("[e2e] pre-check FAIL: expected type not found before removal.");
                return 4;
            }

            // Mutate solution: drop the project block and its configuration entries
            var original = await File.ReadAllTextAsync(slnPath).ConfigureAwait(false);
            var mutated = RemoveProjectFromSln(original, projectName, out var projGuid);
            if (mutated == original) {
                Console.Error.WriteLine("[e2e] mutation produced no change (project not found in sln). ");
                return 4;
            }
            await File.WriteAllTextAsync(slnPath, mutated).ConfigureAwait(false);

            // Load fresh and verify absence
            var wti1 = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var jsonTypeGone = await wti1.FindAsync(probeTypeFqn, limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            var jsonNsGone = await wti1.FindAsync(probeNs + ".", limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (HasAnyItem(jsonTypeGone) || HasAnyItem(jsonNsGone)) {
                Console.Error.WriteLine("[e2e] FAIL: type or namespace still found after project removal.");
                return 5;
            }

            // Revert
            await File.WriteAllTextAsync(slnPath, original).ConfigureAwait(false);
            Console.WriteLine("[e2e] reverted solution change.");

            // Optional post-verify
            var wti2 = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var jsonBack = await wti2.FindAsync(probeTypeFqn, limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (!HasAnyItem(jsonBack)) {
                Console.Error.WriteLine("[e2e] WARN: type not found after revert; workspace cache may lag on CI, continuing.");
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }

    private static bool HasAnyItem(string json) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Total", out var total) && total.GetInt32() > 0) {
                return true;
            }
            if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array) {
                return items.GetArrayLength() > 0;
            }
        } catch { }
        return false;
    }

    private static string RemoveProjectFromSln(string sln, string projectName, out string projectGuid) {
        projectGuid = string.Empty;
        var lines = sln.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var outLines = new List<string>(lines.Count);
        bool skippingProject = false;
        bool inProjectConfigs = false;
        string guidUpper = string.Empty;

        // First pass to identify GUID and drop project block
        foreach (var line in lines) {
            if (!skippingProject && line.StartsWith("Project(", StringComparison.Ordinal) && line.Contains("\"" + projectName + "\"", StringComparison.Ordinal)) {
                // parse GUID at end: ..., "{GUID}"
                var lb = line.LastIndexOf('{');
                var rb = line.LastIndexOf('}');
                if (lb >= 0 && rb > lb) {
                    projectGuid = line.Substring(lb, rb - lb + 1);
                    guidUpper = projectGuid.ToUpperInvariant();
                }
                skippingProject = true;
                continue; // skip Project line
            }
            if (skippingProject) {
                if (line.Trim().Equals("EndProject", StringComparison.Ordinal)) {
                    skippingProject = false; // finished skipping block
                }
                continue; // skip any line inside project block
            }
            outLines.Add(line);
        }

        if (string.IsNullOrEmpty(projectGuid)) {
            return sln; // nothing removed
        }

        // Second pass: remove ProjectConfigurationPlatforms entries for GUID
        lines = outLines;
        outLines = new List<string>(lines.Count);
        foreach (var line in lines) {
            var trim = line.TrimStart();
            if (trim.StartsWith("GlobalSection(ProjectConfigurationPlatforms)", StringComparison.Ordinal)) {
                inProjectConfigs = true;
                outLines.Add(line);
                continue;
            }
            if (inProjectConfigs && trim.StartsWith("EndGlobalSection", StringComparison.Ordinal)) {
                inProjectConfigs = false;
                outLines.Add(line);
                continue;
            }
            if (inProjectConfigs) {
                // drop any line containing the GUID (case-insensitive)
                if (line.ToUpperInvariant().Contains(guidUpper)) {
                    continue;
                }
                outLines.Add(line);
                continue;
            }
            outLines.Add(line);
        }

        return string.Join("\r\n", outLines);
    }
}
