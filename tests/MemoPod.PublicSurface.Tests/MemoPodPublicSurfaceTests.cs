using System.Collections.Immutable;
using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.MemoPod;
using Xunit;

namespace Atelia.MemoPod.PublicSurface.Tests;

public sealed class MemoPodPublicSurfaceTests {
    [Fact]
    public void ExportedSurfaceIsExactlyTheLifecycleContract() {
        Type[] expected = [
            typeof(Memo),
            typeof(MemoId),
            typeof(MemoPod),
            typeof(MemoPodCommitIndeterminateException),
            typeof(MemoPodId),
            typeof(MemoPodInvalidatedException),
            typeof(MemoPodLimits),
            typeof(MemoPodPersistenceException),
            typeof(MemoPodPersistenceFailureKind),
            typeof(MemoPodPhase),
            typeof(MemoRecallException),
            typeof(MemoRecallFailureKind),
            typeof(MemoRecallOptions),
            typeof(MemoRecallResult),
        ];

        Assert.Equal(
            expected.Select(static type => type.FullName)
                .Order(StringComparer.Ordinal),
            typeof(MemoPod).Assembly.GetExportedTypes()
                .Select(static type => type.FullName)
                .Order(StringComparer.Ordinal)
        );
        Assert.DoesNotContain(
            typeof(MemoPod).Assembly.GetExportedTypes(),
            static type => type.Name.Contains(
                "Prompt",
                StringComparison.Ordinal
            ) || type.Name.Contains("Store", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void MemoPodHasOnlyTheLockedPublicLifecycleShape() {
        Type type = typeof(MemoPod);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            ["Phase", "PodId", "Topic"],
            type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            type.GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static property => Assert.False(property.CanWrite)
        );
        Assert.Equal(
            new[] {
                "Append",
                "Create",
                "FreezeAsync",
                "Get",
                "List",
                "Open",
                "RecallAsync",
                "Remove",
                "ResumeEditing",
                "TryGet",
            },
            type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name.Contains(
                "Replace",
                StringComparison.Ordinal
            ) || method.Name.Contains("Update", StringComparison.Ordinal)
                || method.Name.Contains("Upsert", StringComparison.Ordinal)
                || method.Name.Contains("SetTopic", StringComparison.Ordinal)
                || method.Name.Contains("Prompt", StringComparison.Ordinal)
                || method.Name.Contains("Snapshot", StringComparison.Ordinal)
                || method.Name.Contains("Backend", StringComparison.Ordinal)
        );
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
    }

    [Fact]
    public void MemoExposesImmutableIdentityTextAndNullableMetadata() {
        Type type = typeof(Memo);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            ["ExactText", "Gist", "Id", "Summary", "Title"],
            type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            type.GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static property => Assert.False(property.CanWrite)
        );
        Assert.Equal(typeof(string), type.GetProperty(nameof(Memo.ExactText))!
            .PropertyType);
        Assert.Equal(typeof(string), type.GetProperty(nameof(Memo.Title))!
            .PropertyType);
        Assert.Equal(typeof(string), type.GetProperty(nameof(Memo.Gist))!
            .PropertyType);
        Assert.Equal(typeof(string), type.GetProperty(nameof(Memo.Summary))!
            .PropertyType);
    }

    [Fact]
    public void SignaturesUseOnlyPublicImmutableValuesAndStandardTask() {
        Type type = typeof(MemoPod);

        MethodInfo create = type.GetMethod(nameof(MemoPod.Create))!;
        Assert.True(create.IsStatic);
        Assert.Equal(typeof(MemoPod), create.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(MemoPodId), typeof(string)],
            create.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        MethodInfo open = type.GetMethod(nameof(MemoPod.Open))!;
        Assert.True(open.IsStatic);
        Assert.Equal(typeof(MemoPod), open.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(MemoPodId)],
            open.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.Equal(
            typeof(MemoId),
            type.GetMethod(nameof(MemoPod.Append))!.ReturnType
        );
        MethodInfo append = type.GetMethod(nameof(MemoPod.Append))!;
        Assert.Equal(
            [
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
            ],
            append.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.All(
            append.GetParameters().Skip(1),
            static parameter => Assert.Null(parameter.DefaultValue)
        );
        Assert.Equal(
            typeof(ImmutableArray<Memo>),
            type.GetMethod(nameof(MemoPod.List))!.ReturnType
        );
        MethodInfo freeze = type.GetMethod(nameof(MemoPod.FreezeAsync))!;
        Assert.Equal(typeof(Task), freeze.ReturnType);
        ParameterInfo cancellation = Assert.Single(freeze.GetParameters());
        Assert.Equal(typeof(CancellationToken), cancellation.ParameterType);
        Assert.True(cancellation.HasDefaultValue);
        MethodInfo tryGet = type.GetMethod(nameof(MemoPod.TryGet))!;
        Assert.True(tryGet.GetParameters()[1].IsOut);
        Assert.Equal(
            typeof(Memo).MakeByRefType(),
            tryGet.GetParameters()[1].ParameterType
        );
        MethodInfo recall = type.GetMethod(nameof(MemoPod.RecallAsync))!;
        Assert.Equal(typeof(Task<MemoRecallResult>), recall.ReturnType);
        Assert.Equal(
            [
                typeof(ICompletionClient),
                typeof(string),
                typeof(string),
                typeof(MemoRecallOptions),
                typeof(CancellationToken),
            ],
            recall.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.True(recall.GetParameters()[4].HasDefaultValue);
    }

    [Fact]
    public void PersistenceExceptionsAreCatchableButNotCallerConstructible() {
        Assert.True(
            typeof(IOException).IsAssignableFrom(
                typeof(MemoPodPersistenceException)
            )
        );
        Assert.True(
            typeof(MemoPodPersistenceException).IsAssignableFrom(
                typeof(MemoPodCommitIndeterminateException)
            )
        );
        Assert.Empty(typeof(MemoPodPersistenceException).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Empty(
            typeof(MemoPodCommitIndeterminateException).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance
            )
        );
        Assert.Empty(typeof(MemoPodInvalidatedException).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            new[] {
                "AlreadyExists",
                "CommitIndeterminate",
                "InvalidDocument",
                "IoFailure",
                "NotFound",
                "UnsafePath",
            },
            Enum.GetNames<MemoPodPersistenceFailureKind>()
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.Equal(
            ["Editable", "Frozen"],
            Enum.GetNames<MemoPodPhase>()
        );
    }

    [Fact]
    public void RecallTypesExposeOnlyValidatedOptionsAndClosedResult() {
        Assert.Null(typeof(MemoPodLimits).GetField(
            "MaximumRecallToolArgumentsUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static
        ));
        Assert.Null(typeof(MemoPodLimits).GetField(
            "MaximumToolCallIdUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static
        ));
        Assert.Equal(
            new[] {
                "MaxResults",
                "MaxTokens",
                "MaximumFrozenPromptUtf8Bytes",
                "MaximumHydratedExactTextUtf8Bytes",
            },
            typeof(MemoRecallOptions).GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            typeof(MemoRecallOptions).GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static property => Assert.False(property.CanWrite)
        );
        ConstructorInfo optionsConstructor = Assert.Single(
            typeof(MemoRecallOptions).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance
            )
        );
        Assert.Equal(
            [typeof(int), typeof(int), typeof(int), typeof(int)],
            optionsConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );

        Assert.Empty(typeof(MemoRecallResult).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            ["FrozenPromptSha256", "Memos", "Usage"],
            typeof(MemoRecallResult).GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            typeof(MemoRecallResult).GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static property => Assert.False(property.CanWrite)
        );
        Assert.Empty(typeof(MemoRecallException).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            ["InvalidModelOutput", "LocalLimitExceeded", "ProviderFailure"],
            Enum.GetNames<MemoRecallFailureKind>()
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
    }
}
