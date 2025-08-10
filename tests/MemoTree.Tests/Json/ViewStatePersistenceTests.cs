using System.Diagnostics;
using Xunit;

namespace MemoTree.Tests.Json;

public class ViewStatePersistenceTests
{
    [Fact]
    public void Expand_Then_Collapse_Persists_In_View_File()
    {
        // Arrange: temp workspace
        var tempDir = Path.Combine(Path.GetTempPath(), "mt-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // init
            RunCli(tempDir, "init");

            // create node
            RunCliWithInput(tempDir, "create \"IT Node\"", "hello");
            var cogNodes = Path.Combine(tempDir, ".memotree", "CogNodes");
            var nodeId = Directory.GetDirectories(cogNodes).Select(Path.GetFileName)!.First()!;

            // expand
            RunCli(tempDir, $"expand {nodeId}");
            var viewPath = Path.Combine(tempDir, ".memotree", "views", "default.json");
            Assert.True(File.Exists(viewPath));
            var json1 = File.ReadAllText(viewPath);
            Assert.Contains("\"currentLevel\": 2", json1); // Full

            // collapse
            RunCli(tempDir, $"collapse {nodeId}");
            var json2 = File.ReadAllText(viewPath);
            Assert.Contains("\"currentLevel\": 0", json2); // Gist
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void RunCli(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{FindCliProject()}\" --no-build -- {args}",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        if (p.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException($"CLI exited with code {p.ExitCode}.\nArgs: {args}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }

    private static void RunCliWithInput(string cwd, string args, string input)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{FindCliProject()}\" --no-build -- {args}",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        if (p.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException($"CLI exited with code {p.ExitCode}.\nArgs: {args}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }

    private static string FindCliProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var sln = Path.Combine(dir.FullName, "MemoTree.sln");
            if (File.Exists(sln))
            {
                var cli = Path.Combine(dir.FullName, "src", "MemoTree.Cli", "MemoTree.Cli.csproj");
                if (!File.Exists(cli)) throw new FileNotFoundException($"CLI project not found at {cli}");
                return cli;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root (MemoTree.sln)");
    }
}
