using System.Reflection;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalReadViewTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void PublicSurface_IsConstructionFreeAndExactlyReadOnly() {
        Type viewType = typeof(SessionJournalReadView);

        Assert.Empty(viewType.GetConstructors());
        Assert.False(typeof(IDisposable).IsAssignableFrom(viewType));
        Assert.Empty(viewType.GetFields(
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.DeclaredOnly
        ));
        Assert.Empty(viewType.GetEvents(
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.DeclaredOnly
        ));

        PropertyInfo[] properties = viewType.GetProperties(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly
            )
            .OrderBy(
                static property => property.Name,
                StringComparer.Ordinal
            )
            .ToArray();
        (string Name, Type Type)[] expectedProperties = [
            (nameof(SessionJournalReadView.BranchName), typeof(string)),
            (nameof(SessionJournalReadView.BranchRefId), typeof(RefId)),
            (nameof(SessionJournalReadView.Path), typeof(string)),
        ];
        Assert.Equal(expectedProperties.Length, properties.Length);
        for (int index = 0; index < properties.Length; index++) {
            PropertyInfo property = properties[index];
            Assert.Equal(expectedProperties[index].Name, property.Name);
            Assert.Equal(expectedProperties[index].Type, property.PropertyType);
            Assert.True(property.CanRead);
            Assert.False(property.CanWrite);
            Assert.NotNull(property.GetMethod);
            Assert.False(property.GetMethod!.IsStatic);
            Assert.Null(property.SetMethod);
            Assert.Empty(property.GetIndexParameters());
        }

        MethodContract[] expectedMethods = [
            Method<SessionHistoryPlanningSeed>(
                nameof(SessionJournalReadView.CreateHistoryPlanningSeed),
                Required<EventAddress>(),
                Required<SessionContextAnchorSetupReferences>(),
                Optional<CancellationToken>()
            ),
            Method<SessionExecutionBoundaryInspection>(
                nameof(SessionJournalReadView.InspectExecutionBoundary),
                Optional<CancellationToken>()
            ),
            Method<SessionHistoryPlanningSeed>(
                nameof(SessionJournalReadView.MaterializeHistoryPlanningSeed),
                Required<SessionGoverningSetupProof>(),
                Optional<CancellationToken>()
            ),
            Method<SessionHistoryPlanningWindow>(
                nameof(SessionJournalReadView.MaterializeHistoryPlanningWindow),
                Required<SessionHistoryPlanningWindowProof>(),
                Required<SessionHistoryPlanningSeed>(),
                Optional<CancellationToken>()
            ),
            Method<SessionExpectedObservationTurnReadResult>(
                nameof(SessionJournalReadView
                    .ProveExpectedObservationTurnAtSelectedHead),
                Required<SessionExpectedObservationTurnRequest>(),
                Optional<CancellationToken>()
            ),
            Method<SessionGoverningSetupProofResult>(
                nameof(SessionJournalReadView.ProveGoverningSetupAtBounded),
                Required<EventAddress>(),
                Required<SessionContextAnchorSetupReferences>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionGoverningSetupProofResult>(
                nameof(SessionJournalReadView.ProveGoverningSetupInPrefix),
                Required<SessionCurrentLineagePrefix>(),
                Required<EventAddress>(),
                Required<SessionContextAnchorSetupReferences>()
            ),
            Method<SessionGoverningSetupProof>(
                nameof(SessionJournalReadView.ProveGoverningSetupTransition),
                Required<SessionHistoryPlanningWindowProof>(),
                Required<SessionGoverningSetupProof>(),
                Required<SessionContextAnchorSetupReferences>()
            ),
            Method<SessionHistoryPlanningWindowProofResult>(
                nameof(SessionJournalReadView.ProveHistoryPlanningWindowAtBounded),
                Required<EventAddress>(),
                Required<EventAddress>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionHistoryPlanningWindowProofResult>(
                nameof(SessionJournalReadView.ProveHistoryPlanningWindowInPrefix),
                Required<SessionCurrentLineagePrefix>(),
                Required<EventAddress>(),
                Required<EventAddress>(),
                Required<int>()
            ),
            Method<EventAddress?>(
                nameof(SessionJournalReadView.ReadCurrentHead)
            ),
            Method<SessionCurrentLineagePrefix>(
                nameof(SessionJournalReadView.ReadCurrentLineagePrefix),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionHistoryPlanningWindowReadResult>(
                nameof(SessionJournalReadView.ReadHistoryPlanningWindowAtBounded),
                Required<EventAddress>(),
                Required<EventAddress>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionHistoryPlanningWindowReadResult>(
                nameof(SessionJournalReadView.ReadHistoryPlanningWindowAtBounded),
                Required<EventAddress>(),
                Required<SessionHistoryPlanningSeed>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionCurrentLineagePrefix>(
                nameof(SessionJournalReadView.ReadLineagePrefixAt),
                Required<EventAddress>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<EventJournalPhysicalAppendFrontier>(
                nameof(SessionJournalReadView
                    .ReadPhysicalAppendFrontier)
            ),
            Method<SessionCreatedPlanningSeedReadResult>(
                nameof(SessionJournalReadView.ReadSessionCreatedPlanningSeedAtBounded),
                Required<EventAddress>(),
                Required<int>(),
                Optional<CancellationToken>()
            ),
            Method<SessionGoverningSetup>(
                nameof(SessionJournalReadView.ResolveGoverningSetup),
                Required<EventAddress>(),
                Optional<CancellationToken>()
            ),
            Method(
                typeof(void),
                nameof(SessionJournalReadView.ValidateGoverningSetupPayloads),
                Required<IEnumerable<SessionGoverningSetupProof>>(),
                Optional<CancellationToken>()
            ),
        ];
        MethodInfo[] methods = viewType.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly
            )
            .Where(static method => !method.IsSpecialName)
            .OrderBy(
                static method => method.Name,
                StringComparer.Ordinal
            )
            .ThenBy(static method => string.Join(
                "|",
                method.GetParameters().Select(static parameter =>
                    parameter.ParameterType.FullName
                )
            ), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedMethods.Length, methods.Length);
        for (int methodIndex = 0;
             methodIndex < methods.Length;
             methodIndex++) {
            MethodInfo actual = methods[methodIndex];
            MethodContract expected = expectedMethods[methodIndex];
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.ReturnType, actual.ReturnType);
            Assert.Equal(expected.IsStatic, actual.IsStatic);
            ParameterInfo[] parameters = actual.GetParameters();
            Assert.Equal(expected.Parameters.Length, parameters.Length);
            for (int parameterIndex = 0;
                 parameterIndex < parameters.Length;
                 parameterIndex++) {
                ParameterInfo parameter = parameters[parameterIndex];
                ParameterContract contract =
                    expected.Parameters[parameterIndex];
                Assert.Equal(contract.Type, parameter.ParameterType);
                Assert.Equal(contract.IsOptional, parameter.IsOptional);
                Assert.Equal(
                    contract.HasDefaultValue,
                    parameter.HasDefaultValue
                );
                if (contract.HasDefaultValue) {
                    Assert.Equal(
                        contract.DefaultValue,
                        parameter.DefaultValue
                    );
                }
            }
        }
    }

    [Fact]
    public void ReadView_WritableAndReadOnlyExposeEquivalentAuthority() {
        string path = NewJournalPath();
        ReadAuthorityEvidence writableEvidence;

        using (var writable = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            SessionJournalReadView view = writable.ReadView;
            Assert.Same(view, writable.ReadView);
            Assert.Equal(writable.Path, view.Path);
            Assert.Equal(writable.BranchName, view.BranchName);
            Assert.Equal(writable.BranchRefId, view.BranchRefId);
            writableEvidence = ReadAuthority(view);
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        SessionJournalReadView readOnlyView = readOnly.ReadView;
        Assert.Same(readOnlyView, readOnly.ReadView);

        ReadAuthorityEvidence readOnlyEvidence =
            ReadAuthority(readOnlyView);
        Assert.Equal(writableEvidence, readOnlyEvidence);
    }

    [Fact]
    public void ReadView_DoesNotOutliveOwningEngine() {
        string path = NewJournalPath();
        var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        SessionJournalReadView view = engine.ReadView;
        EventAddress head = view.ReadCurrentHead()!.Value;

        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => view.ReadCurrentHead());
        Assert.Throws<ObjectDisposedException>(() =>
            view.ProveExpectedObservationTurnAtSelectedHead(new(
                head,
                head,
                "expected observation"
            )));
        Assert.Throws<ObjectDisposedException>(
            () => view.ReadPhysicalAppendFrontier()
        );
        Assert.Throws<ObjectDisposedException>(() => _ = view.Path);
        Assert.Throws<ObjectDisposedException>(() => _ = engine.ReadView);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    private static ReadAuthorityEvidence ReadAuthority(
        SessionJournalReadView view
    ) {
        EventAddress head = view.ReadCurrentHead()!.Value;
        EventJournalPhysicalAppendFrontier physicalFrontier =
            view.ReadPhysicalAppendFrontier();
        Assert.True(physicalFrontier.Contains(head));
        var initial = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(view.ReadSessionCreatedPlanningSeedAtBounded(head, 3));
        var availableProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(view.ProveGoverningSetupAtBounded(
            head,
            initial.Seed.Setups,
            3
        ));
        Assert.Equal(0, availableProof.Proof.Diagnostics.PayloadReads);
        Assert.Equal(
            0,
            availableProof.Proof.Diagnostics.DecodedPayloadBytes
        );

        SessionHistoryPlanningSeed materialized =
            view.MaterializeHistoryPlanningSeed(availableProof.Proof);
        SessionGoverningSetup resolved =
            view.ResolveGoverningSetup(head);

        Assert.Equal(head, availableProof.Proof.Boundary);
        Assert.Equal(head, materialized.Address);
        Assert.Equal(initial.Seed.Setups, materialized.Setups);
        Assert.Equal(
            materialized.Setups.RuntimeConfig.Address,
            resolved.RuntimeConfigSetupAddress
        );
        Assert.Equal(
            materialized.Setups.SystemPrompt.Address,
            resolved.SystemPromptSetupAddress
        );
        Assert.Equal("model-A", resolved.RuntimeConfig.ModelId);
        Assert.Equal("system-A", resolved.SystemPrompt);

        return new ReadAuthorityEvidence(
            head,
            physicalFrontier,
            materialized.Setups,
            resolved.RuntimeConfigSetupAddress,
            resolved.SystemPromptSetupAddress,
            resolved.RuntimeConfig.ModelId,
            resolved.SystemPrompt
        );
    }

    private string NewJournalPath() {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-session-journal-read-view-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static MethodContract Method<TReturn>(
        string name,
        params ParameterContract[] parameters
    ) => new(typeof(TReturn), name, IsStatic: false, parameters);

    private static MethodContract Method(
        Type returnType,
        string name,
        params ParameterContract[] parameters
    ) => new(returnType, name, IsStatic: false, parameters);

    private static ParameterContract Required<T>()
        => new(
            typeof(T),
            IsOptional: false,
            HasDefaultValue: false,
            DefaultValue: null
        );

    private static ParameterContract Optional<T>()
        => new(
            typeof(T),
            IsOptional: true,
            HasDefaultValue: true,
            DefaultValue: null
        );

    private sealed record MethodContract(
        Type ReturnType,
        string Name,
        bool IsStatic,
        ParameterContract[] Parameters
    );

    private sealed record ParameterContract(
        Type Type,
        bool IsOptional,
        bool HasDefaultValue,
        object? DefaultValue
    );

    private sealed record ReadAuthorityEvidence(
        EventAddress Head,
        EventJournalPhysicalAppendFrontier PhysicalAppendFrontier,
        SessionContextAnchorSetupReferences Setups,
        EventAddress RuntimeSetupAddress,
        EventAddress PromptSetupAddress,
        string ModelId,
        string SystemPrompt
    );
}
