using System.Collections.Immutable;
using System.Reflection;
using Atelia.SessionJournal.MemoPod;
using Xunit;

namespace Atelia.SessionJournal.MemoPod.PublicSurface.Tests;

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
                || method.Name.Contains("Recall", StringComparison.Ordinal)
                || method.Name.Contains("Prompt", StringComparison.Ordinal)
                || method.Name.Contains("Snapshot", StringComparison.Ordinal)
                || method.Name.Contains("Backend", StringComparison.Ordinal)
        );
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
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
}
