using System.Reflection;
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
        Assert.NotNull(engineType.Assembly.GetType(
            "Atelia.SessionJournal.RecentHistorySlice"
        ));
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
