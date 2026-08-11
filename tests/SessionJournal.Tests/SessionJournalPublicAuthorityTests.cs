using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalPublicAuthorityTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void PublicMutationSurface_IsNarrowAndRoleSpecific() {
        Type engineType = typeof(SessionJournalEngine);
        string[] lowLevelAppendMethods = [
            nameof(SessionJournalLegacyImportWriter.AppendObservation),
            "AppendRuntimeConfigSetup",
            nameof(SessionJournalLegacyImportWriter.AppendSystemPromptSetup),
            nameof(SessionJournalLegacyImportWriter.AppendImportedAgentAction)
        ];
        string[] publicEngineMethods = engineType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToArray();
        foreach (string methodName in lowLevelAppendMethods) {
            Assert.DoesNotContain(methodName, publicEngineMethods);
        }
        Assert.DoesNotContain(
            engineType.GetMethods(
                BindingFlags.Public | BindingFlags.Static
            ).SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType
                == typeof(SessionCreationOrigin)
        );

        Type optionsType = typeof(SessionCreateOptions);
        Assert.Null(optionsType.GetProperty("Origin"));
        Assert.DoesNotContain(
            optionsType.GetConstructors().SelectMany(
                constructor => constructor.GetParameters()
            ),
            parameter => parameter.ParameterType
                == typeof(SessionCreationOrigin)
        );

        Type writerType = typeof(SessionJournalLegacyImportWriter);
        Assert.True(writerType.IsSealed);
        Assert.Empty(writerType.GetConstructors());
        Assert.Equal(
            [
                "AppendImportedAgentAction",
                "AppendObservation",
                "AppendSystemPromptSetup",
                "Create",
                "Dispose",
                "ReadCurrentHead"
            ],
            writerType.GetMethods(
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly
            ).Select(method => method.Name).Order().ToArray()
        );
        AssertPublicMethod(
            writerType,
            isStatic: true,
            "Create",
            writerType,
            typeof(string),
            typeof(SessionCreateOptions)
        );
        AssertPublicMethod(
            writerType,
            isStatic: false,
            "ReadCurrentHead",
            typeof(EventAddress)
        );
        AssertPublicMethod(
            writerType,
            isStatic: false,
            "AppendObservation",
            typeof(EventAddress),
            typeof(string)
        );
        AssertPublicMethod(
            writerType,
            isStatic: false,
            "AppendSystemPromptSetup",
            typeof(EventAddress),
            typeof(string)
        );
        AssertPublicMethod(
            writerType,
            isStatic: false,
            "AppendImportedAgentAction",
            typeof(EventAddress),
            typeof(ActionMessage),
            typeof(CompletionDescriptor)
        );
        AssertPublicMethod(
            writerType,
            isStatic: false,
            "Dispose",
            typeof(void)
        );
        Type[] forbiddenAuthorityTypes = [
            engineType,
            typeof(SessionRuntime),
            typeof(SessionRuntimeConfiguration),
            typeof(SessionCreationOrigin)
        ];
        Assert.DoesNotContain(
            writerType.GetMethods(
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly
            ),
            method => forbiddenAuthorityTypes.Contains(
                    method.ReturnType
                )
                || method.GetParameters().Any(parameter =>
                    forbiddenAuthorityTypes.Contains(
                        parameter.ParameterType
                    )
                )
        );
        Assert.DoesNotContain(
            writerType.GetProperties(
                BindingFlags.Public | BindingFlags.Instance
            ),
            property => forbiddenAuthorityTypes.Contains(
                property.PropertyType
            )
        );
        Assert.DoesNotContain(
            writerType.GetFields(
                BindingFlags.Public | BindingFlags.Instance
            ),
            field => forbiddenAuthorityTypes.Contains(
                field.FieldType
            )
        );

        Assert.Null(engineType.Assembly.GetType(
            "Atelia.SessionJournal.IRecentHistoryAnalyzer"
        ));
        Assert.Null(engineType.Assembly.GetType(
            "Atelia.SessionJournal.RecentHistoryAnalysisContext"
        ));
        Assert.Null(engineType.Assembly.GetType(
            "Atelia.SessionJournal.RecentHistory" + "Slice"
        ));
    }

    [Fact]
    public void PublicOnlineMutationSurface_RequiresExactExpectedHead() {
        Type engineType = typeof(SessionJournalEngine);
        MethodInfo[] publicOnlineMethods = engineType.GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly
            )
            .Where(method => method.Name is nameof(
                    SessionJournalEngine.SendAsync
                ) or nameof(SessionJournalEngine.ResumeAsync))
            .ToArray();

        Assert.Equal(
            [
                "ResumeAsync(EventAddress,CancellationToken)",
                "ResumeAsync(EventAddress,CompletionStreamObserver,CancellationToken)",
                "SendAsync(EventAddress,String,CancellationToken)",
                "SendAsync(EventAddress,String,CompletionStreamObserver,CancellationToken)"
            ],
            publicOnlineMethods
                .Select(FormatSignature)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            publicOnlineMethods,
            method => {
                Assert.Equal(
                    typeof(EventAddress),
                    method.GetParameters()[0].ParameterType
                );
                Assert.Equal(
                    method.Name == nameof(SessionJournalEngine.SendAsync)
                        ? typeof(Task<TurnResult>)
                        : typeof(Task<ResumeOutcome>),
                    method.ReturnType
                );
            }
        );
    }

    [Fact]
    public void PublicFactorySurface_RequiresSeparateRuntimeAttachment_AndHidesRawPayloadBytes() {
        Type engineType = typeof(SessionJournalEngine);
        Assert.Equal(
            [
                "Create(String,SessionCreateOptions)",
                "Open(String)",
                "Open(String,String)"
            ],
            engineType.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
                )
                .Where(method => method.Name is nameof(
                        SessionJournalEngine.Create
                    ) or nameof(SessionJournalEngine.Open))
                .Select(FormatSignature)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.DoesNotContain(
            engineType.GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly
            ),
            method => method.Name == "ReadPayloadBytes"
        );
        AssertPublicMethod(
            engineType,
            isStatic: false,
            nameof(SessionJournalEngine.UseRuntime),
            typeof(void),
            typeof(SessionRuntime)
        );
    }

    [Fact]
    public async Task ExternalConsumer_CannotCompileUnboundOnlineMutations_ButCanCompileBoundOverloads() {
        string tempRoot = NewPath();
        Directory.CreateDirectory(tempRoot);
        string sessionJournalProject = SecurityElement.Escape(
            Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(CurrentSourceFile())!,
                    "..",
                    "..",
                    "prototypes",
                    "SessionJournal",
                    "SessionJournal.csproj"
                )
            )
        )!;
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "ExternalConsumer.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{sessionJournalProject}}" />
              </ItemGroup>
            </Project>
            """
        );
        string probePath = Path.Combine(
            tempRoot,
            "ExternalConsumerProbe.cs"
        );
        await File.WriteAllTextAsync(
            probePath,
            """
            using Atelia.SessionJournal;

            public static class ExternalConsumerProbe {
                public static async Task UseUnboundAsync(
                    SessionJournalEngine engine
                ) {
                    _ = await engine.SendAsync("first");
                    _ = await engine.SendAsync(
                        "second",
                        observer: null
                    );
                    _ = await engine.ResumeAsync();
                    _ = await engine.ResumeAsync(
                        observer: null
                    );
                }
            }
            """
        );

        (int unboundExitCode, string unboundOutput) =
            await CompileExternalConsumerAsync(tempRoot);

        Assert.NotEqual(0, unboundExitCode);
        Assert.Contains("ExternalConsumerProbe.cs", unboundOutput);
        Assert.True(
            unboundOutput.Split(
                ": error CS",
                StringSplitOptions.None
            ).Length >= 5,
            unboundOutput
        );

        await File.WriteAllTextAsync(
            probePath,
            """
            using Atelia.SessionJournal;

            public static class ExternalConsumerProbe {
                public static async Task UseBoundAsync(
                    SessionJournalEngine engine
                ) {
                    var expectedHead = engine.ReadCurrentHead()!.Value;
                    _ = await engine.SendAsync(
                        expectedHead,
                        "first"
                    );
                    _ = await engine.SendAsync(
                        expectedHead,
                        "second",
                        observer: null
                    );
                    _ = await engine.ResumeAsync(expectedHead);
                    _ = await engine.ResumeAsync(
                        expectedHead,
                        observer: null
                    );
                }
            }
            """
        );

        (int boundExitCode, string boundOutput) =
            await CompileExternalConsumerAsync(tempRoot);

        Assert.True(boundExitCode == 0, boundOutput);
    }

    [Fact]
    public async Task ExternalSecondHost_UsesRuntimeAfterFactory_AndCannotCompileRuntimeFactoriesOrRawPayloadRead() {
        string tempRoot = NewPath();
        Directory.CreateDirectory(tempRoot);
        string sessionJournalProject = SecurityElement.Escape(
            Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(CurrentSourceFile())!,
                    "..",
                    "..",
                    "prototypes",
                    "SessionJournal",
                    "SessionJournal.csproj"
                )
            )
        )!;
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "ExternalConsumer.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{sessionJournalProject}}" />
              </ItemGroup>
            </Project>
            """
        );
        string probePath = Path.Combine(
            tempRoot,
            "ExternalConsumerProbe.cs"
        );
        await File.WriteAllTextAsync(
            probePath,
            """
            using Atelia.SessionJournal;

            public static class ExternalSecondHost {
                public static void UseUnsupportedFactories(
                    string createPath,
                    string openPath,
                    string branchName,
                    SessionCreateOptions options,
                    SessionRuntime runtime
                ) {
                    _ = SessionJournalEngine.Create(
                        createPath,
                        options,
                        runtime
                    );
                    _ = SessionJournalEngine.Open(openPath, runtime);
                    _ = SessionJournalEngine.Open(
                        openPath,
                        branchName,
                        runtime
                    );
                    using var inspection =
                        SessionJournalEngine.OpenReadOnly(openPath);
                    _ = inspection.ReadPayloadBytes(default);
                }
            }
            """
        );

        (int unsupportedExitCode, string unsupportedOutput) =
            await CompileExternalConsumerAsync(tempRoot);

        Assert.NotEqual(0, unsupportedExitCode);
        Assert.Contains("ExternalConsumerProbe.cs", unsupportedOutput);
        Assert.Contains("ReadPayloadBytes", unsupportedOutput);
        Assert.True(
            unsupportedOutput.Split(
                ": error CS",
                StringSplitOptions.None
            ).Length >= 5,
            unsupportedOutput
        );

        await File.WriteAllTextAsync(
            probePath,
            """
            using Atelia.SessionJournal;

            public static class ExternalSecondHost {
                public static void UseSupportedFactories(
                    string createPath,
                    string openPath,
                    string branchName,
                    SessionCreateOptions options,
                    SessionRuntime runtime
                ) {
                    using var created = SessionJournalEngine.Create(
                        createPath,
                        options
                    );
                    created.UseRuntime(runtime);

                    using var opened =
                        SessionJournalEngine.Open(openPath);
                    opened.UseRuntime(runtime);

                    using var branch = SessionJournalEngine.Open(
                        openPath,
                        branchName
                    );
                    branch.UseRuntime(runtime);
                }
            }
            """
        );

        (int supportedExitCode, string supportedOutput) =
            await CompileExternalConsumerAsync(tempRoot);

        Assert.True(supportedExitCode == 0, supportedOutput);
    }

    [Fact]
    public void CreateAuthorities_ForceTheirOwnedOrigins() {
        string nativePath = NewPath();
        using (var engine = SessionJournalEngine.Create(
                   nativePath,
                   CreateOptions()
               )) {
        }

        string legacyPath = NewPath();
        EventAddress initialHead;
        EventAddress finalHead;
        using (var writer = SessionJournalLegacyImportWriter.Create(
                   legacyPath,
                   CreateOptions()
               )) {
            initialHead = writer.ReadCurrentHead();
            writer.AppendObservation("imported observation");
            writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("imported action")
                ]),
                new CompletionDescriptor(
                    "legacy-import",
                    "legacy-import-v1",
                    "model-a"
                )
            );
            finalHead = writer.AppendSystemPromptSetup(
                "updated imported prompt"
            );
            Assert.Equal(finalHead, writer.ReadCurrentHead());
            Assert.NotEqual(initialHead, finalHead);
        }

        Assert.Equal(
            SessionCreationOrigin.Native,
            ReadCreationOrigin(nativePath)
        );
        Assert.Equal(
            SessionCreationOrigin.LegacyImport,
            ReadCreationOrigin(legacyPath)
        );
    }

    [Fact]
    public void LegacyImportWriter_CannotCreateOverExistingRepository() {
        string path = NewPath();
        EventAddress originalHead;
        using (var engine = SessionJournalEngine.Create(
                   path,
                   CreateOptions()
               )) {
            originalHead = engine.ReadCurrentHead()!.Value;
        }

        Assert.ThrowsAny<Exception>(() =>
            SessionJournalLegacyImportWriter.Create(
                path,
                CreateOptions()
            )
        );

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        Assert.Equal(originalHead, reopened.ReadCurrentHead());
        Assert.Equal(
            SessionCreationOrigin.Native,
            ReadCreationOrigin(path)
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test repositories.
            }
        }
    }

    private static SessionCreateOptions CreateOptions()
        => new("model-a", "system-a", "surface-a");

    private static void AssertPublicMethod(
        Type declaringType,
        bool isStatic,
        string name,
        Type returnType,
        params Type[] parameterTypes
    ) {
        BindingFlags flags = BindingFlags.Public
            | BindingFlags.DeclaredOnly
            | (isStatic
                ? BindingFlags.Static
                : BindingFlags.Instance);
        MethodInfo method = Assert.Single(
            declaringType.GetMethods(flags),
            candidate => candidate.Name == name
        );
        Assert.Equal(returnType, method.ReturnType);
        Assert.Equal(
            parameterTypes,
            method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray()
        );
    }

    private static string FormatSignature(MethodInfo method)
        => $"{method.Name}({string.Join(",", method.GetParameters().Select(
            parameter => parameter.ParameterType.Name
        ))})";

    private static string CurrentSourceFile(
        [CallerFilePath] string path = ""
    ) => path;

    private static async Task<(int ExitCode, string Output)>
        CompileExternalConsumerAsync(string workingDirectory) {
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("ExternalConsumer.csproj");
        start.ArgumentList.Add("-m:1");
        start.ArgumentList.Add("-nr:false");
        start.ArgumentList.Add("--nologo");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Failed to start external consumer compilation."
            );
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (
            process.ExitCode,
            await outputTask + await errorTask
        );
    }

    private static SessionCreationOrigin ReadCreationOrigin(string path) {
        var events = new List<SessionJournalAuditEvent>();
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        _ = engine.ScanCheckedAuditEvents(events.Add);
        SessionJournalAuditEvent created = Assert.Single(
            events,
            item => item.Kind == SessionEventKind.SessionCreated
        );
        return Assert.IsType<SessionJournalAuditSessionCreatedFact>(
            created.Fact
        ).Origin;
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-journal-public-authority-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }
}
