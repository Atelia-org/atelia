using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class RecapCutoverArchitectureBoundaryTests {
    private static readonly IReadOnlyDictionary<ushort, OpCode>
        OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                static opcode => unchecked((ushort)opcode.Value)
            );

    private static readonly HashSet<string> ForbiddenOnlineMethodNames = [
        nameof(SessionJournalEngine.ReadCurrentLineageHeaders),
        nameof(SessionJournalEngine.ReadHistoryPlanningSeeds),
        nameof(SessionJournalEngine.ReadHistoryPlanningWindowAt),
        nameof(SessionJournalEngine.ResolveContextAnchorSetupReferences)
    ];

    // These commands are deliberately offline inspection/import surfaces.
    // Any new exception must be named here rather than hidden by a filename
    // or formatting convention.
    private static readonly HashSet<string> OfflineTypeAllowlist = [
        "Atelia.SessionJournal.Cli.RecapMaterializationInspectionCommands",
        "Atelia.SessionJournal.Cli.SessionJournalLegacyImporter"
    ];

    private static readonly string[] RetiredCommandNames = [
        "run-memory-maintainer",
        "run-derived-memory-orchestration",
        "publish-derived-artifact-set",
        "list-derived-artifact-sets",
        "validate-derived-memory",
        "rebuild-derived-artifact-set-latest",
        "configure-derived-artifact-planner",
        "plan-derived-artifact-epoch",
        "list-derived-artifact-epochs"
    ];

    [Fact]
    public void RetiredDerivedMemorySurface_IsAbsent() {
        string repoRoot = FindRepositoryRoot();
        string retiredProductDirectory = Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.DerivedMemory"
        );
        string retiredTestDirectory = Path.Combine(
            repoRoot,
            "tests",
            "SessionJournal.DerivedMemory.Tests"
        );

        Assert.False(Directory.Exists(retiredProductDirectory));
        Assert.False(Directory.Exists(retiredTestDirectory));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "DerivedMemoryCommands.cs"
        )));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "MemoryMaintainerRun.cs"
        )));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "MemoryMaintainerRunUtils.cs"
        )));

        string program = File.ReadAllText(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "Program.cs"
        ));
        foreach (string command in RetiredCommandNames) {
            Assert.DoesNotContain(
                command,
                program,
                StringComparison.Ordinal
            );
        }

        string solution = File.ReadAllText(Path.Combine(
            repoRoot,
            "Atelia.sln"
        ));
        Assert.DoesNotContain(
            "SessionJournal.DerivedMemory",
            solution,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "D3822044-41C9-47B0-8245-D4110714D7E4",
            solution,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "0B73B345-A8F2-4515-BC29-3D6BDE905C19",
            solution,
            StringComparison.OrdinalIgnoreCase
        );

        foreach (
            string projectFile in Directory.EnumerateFiles(
                repoRoot,
                "*.csproj",
                SearchOption.AllDirectories
            )
        ) {
            Assert.DoesNotContain(
                "SessionJournal.DerivedMemory",
                File.ReadAllText(projectFile),
                StringComparison.Ordinal
            );
        }
        string assemblyInfo = File.ReadAllText(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal",
            "Properties",
            "AssemblyInfo.cs"
        ));
        Assert.DoesNotContain(
            "Atelia.SessionJournal.DerivedMemory.Tests",
            assemblyInfo,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ProjectReferences_PreserveOneWayCompositionBoundaries() {
        string repoRoot = FindRepositoryRoot();

        Assert.Equal(
            [
                "../Completion.Abstractions/Completion.Abstractions.csproj",
                "../SessionJournal/SessionJournal.csproj"
            ],
            ReadProjectReferences(
                repoRoot,
                "prototypes",
                "SessionJournal.DerivedRecap.Maintainers",
                "SessionJournal.DerivedRecap.Maintainers.csproj"
            )
        );
        Assert.Equal(
            ["../ChatSession/ChatSession.csproj"],
            ReadProjectReferences(
                repoRoot,
                "prototypes",
                "ChatSession.LegacyExportCli",
                "ChatSession.LegacyExportCli.csproj"
            )
        );

        string[] cliReferences = ReadProjectReferences(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "SessionJournal.Cli.csproj"
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Maintainers/SessionJournal.DerivedRecap.Maintainers.csproj",
            cliReferences
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Planner/SessionJournal.DerivedRecap.Planner.csproj",
            cliReferences
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Store/SessionJournal.DerivedRecap.Store.csproj",
            cliReferences
        );

        string[] rawJournalReferences = ReadProjectReferences(
            repoRoot,
            "prototypes",
            "SessionJournal",
            "SessionJournal.csproj"
        );
        Assert.DoesNotContain(
            rawJournalReferences,
            reference => reference.Contains(
                "SessionJournal.DerivedRecap",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void OnlineRecapPaths_UseBoundedLineageAndOpaqueAuthorities() {
        Assembly[] onlineAssemblies = [
            typeof(DerivedRecapOperationPreparer).Assembly,
            typeof(DerivedRecapStore).Assembly,
            typeof(Program).Assembly
        ];
        foreach (string offlineType in OfflineTypeAllowlist) {
            Assert.NotNull(typeof(Program).Assembly.GetType(offlineType));
        }
        HashSet<Assembly> onlineAssemblySet = [.. onlineAssemblies];
        MethodBase[] roots = [
            .. onlineAssemblies
                .SelectMany(static assembly => assembly.GetTypes())
                .Where(static type => !IsOfflineAllowlisted(type))
                .SelectMany(GetDeclaredMethods)
        ];

        IReadOnlyList<ForbiddenMethodReference> findings =
            FindForbiddenMethodReferences(
                roots,
                method => method.DeclaringType is { } type
                    && onlineAssemblySet.Contains(type.Assembly)
                    && !IsOfflineAllowlisted(type)
            );

        Assert.True(
            findings.Count == 0,
            "Online recap IL references forbidden unbounded APIs:\n"
            + string.Join(
                "\n",
                findings.Select(static finding =>
                    $"{FormatMethod(finding.Caller)} -> "
                    + FormatMethod(finding.Target))
            )
        );
    }

    [Fact]
    public void OnlineRecapIlGuard_FollowsWrapperCalls() {
        MethodInfo entry = typeof(ForbiddenWrapperFixture).GetMethod(
            nameof(ForbiddenWrapperFixture.Entry),
            BindingFlags.Public | BindingFlags.Static
        )!;

        IReadOnlyList<ForbiddenMethodReference> findings =
            FindForbiddenMethodReferences(
                [entry],
                static method => method.DeclaringType
                    == typeof(ForbiddenWrapperFixture)
            );

        ForbiddenMethodReference finding = Assert.Single(findings);
        Assert.Equal(
            nameof(SessionJournalEngine.ReadCurrentLineageHeaders),
            finding.Target.Name
        );
    }

    [Fact]
    public void BeyondPrefixConstructionRequiresExplicitStage() {
        Type resultType =
            typeof(DerivedRecapExecutionResult.BeyondPrefix);
        ConstructorInfo primary = Assert.Single(
            resultType.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance
            )
        );
        Assert.Equal(
            [
                typeof(DerivedRecapBeyondPrefixStage),
                typeof(SessionCurrentLineageBeyondPrefix)
            ],
            primary.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.DoesNotContain(
            resultType.GetConstructors(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
            ),
            static constructor => {
                ParameterInfo[] parameters =
                    constructor.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType
                        == typeof(SessionCurrentLineageBeyondPrefix);
            }
        );
    }

    private static IReadOnlyList<ForbiddenMethodReference>
        FindForbiddenMethodReferences(
        IEnumerable<MethodBase> roots,
        Func<MethodBase, bool> shouldTraverse
    ) {
        var pending = new Queue<MethodBase>(roots);
        var visited = new HashSet<(Guid Module, int Token)>();
        var reported = new HashSet<(
            Guid CallerModule,
            int CallerToken,
            Guid TargetModule,
            int TargetToken
        )>();
        var findings = new List<ForbiddenMethodReference>();
        while (pending.TryDequeue(out MethodBase? caller)) {
            (Guid Module, int Token) callerIdentity =
                GetMethodIdentity(caller);
            if (!visited.Add(callerIdentity)) {
                continue;
            }
            foreach (MethodBase target in ReadMethodReferences(caller)) {
                if (IsForbiddenOnlineMethod(target)) {
                    (Guid Module, int Token) targetIdentity =
                        GetMethodIdentity(target);
                    if (reported.Add((
                            callerIdentity.Module,
                            callerIdentity.Token,
                            targetIdentity.Module,
                            targetIdentity.Token
                        ))) {
                        findings.Add(new ForbiddenMethodReference(
                            caller,
                            target
                        ));
                    }
                }
                else if (shouldTraverse(target)) {
                    pending.Enqueue(target);
                }
            }
        }
        return findings;
    }

    private static (Guid Module, int Token) GetMethodIdentity(
        MethodBase method
    ) => (method.Module.ModuleVersionId, method.MetadataToken);

    private static IEnumerable<MethodBase> GetDeclaredMethods(Type type) {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        return type.GetMethods(flags).Cast<MethodBase>()
            .Concat(type.GetConstructors(flags));
    }

    private static IEnumerable<MethodBase> ReadMethodReferences(
        MethodBase method
    ) {
        MethodBody? body;
        try {
            body = method.GetMethodBody();
        }
        catch (InvalidOperationException) {
            yield break;
        }
        byte[]? il = body?.GetILAsByteArray();
        if (il is null) {
            yield break;
        }

        int offset = 0;
        while (offset < il.Length) {
            ushort value = il[offset++];
            if (value == 0xFE) {
                value = (ushort)(0xFE00 | il[offset++]);
            }
            OpCode opcode = OpCodesByValue[value];
            if (opcode.OperandType == OperandType.InlineMethod) {
                int token = BitConverter.ToInt32(il, offset);
                MethodBase? referenced = ResolveMethod(method, token);
                if (referenced is not null) {
                    yield return referenced;
                }
            }
            offset += GetOperandSize(opcode.OperandType, il, offset);
        }
    }

    private static MethodBase? ResolveMethod(MethodBase caller, int token) {
        try {
            Type[]? typeArguments = caller.DeclaringType?.IsGenericType
                == true
                ? caller.DeclaringType.GetGenericArguments()
                : null;
            Type[]? methodArguments = caller.IsGenericMethod
                ? caller.GetGenericArguments()
                : null;
            return caller.Module.ResolveMethod(
                token,
                typeArguments,
                methodArguments
            );
        }
        catch (ArgumentException) {
            return null;
        }
    }

    private static int GetOperandSize(
        OperandType operandType,
        byte[] il,
        int operandOffset
    ) => operandType switch {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget
            or OperandType.ShortInlineI
            or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget
            or OperandType.InlineField
            or OperandType.InlineI
            or OperandType.InlineMethod
            or OperandType.InlineSig
            or OperandType.InlineString
            or OperandType.InlineTok
            or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8
            or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4
            + BitConverter.ToInt32(il, operandOffset) * 4,
        _ => throw new InvalidDataException(
            $"Unsupported IL operand type '{operandType}'."
        )
    };

    private static bool IsForbiddenOnlineMethod(MethodBase method) =>
        method.DeclaringType == typeof(SessionJournalEngine)
        && ForbiddenOnlineMethodNames.Contains(method.Name);

    private static bool IsOfflineAllowlisted(Type type) {
        for (Type? cursor = type; cursor is not null;
             cursor = cursor.DeclaringType) {
            if (cursor.FullName is { } name
                && OfflineTypeAllowlist.Contains(name)) {
                return true;
            }
        }
        return false;
    }

    private static string FormatMethod(MethodBase method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}";

    private sealed record ForbiddenMethodReference(
        MethodBase Caller,
        MethodBase Target
    );

    private static class ForbiddenWrapperFixture {
        public static void Entry(SessionJournalEngine engine) =>
            Wrapper(engine);

        private static void Wrapper(SessionJournalEngine engine) =>
            _ = engine.ReadCurrentLineageHeaders();
    }

    private static string[] ReadProjectReferences(
        string repoRoot,
        params string[] relativePath
    ) {
        XDocument project = XDocument.Load(
            Path.Combine([repoRoot, .. relativePath])
        );
        return [
            .. project.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(static value => value is not null)
                .Select(static value => value!.Replace('\\', '/'))
        ];
    }

    private static string FindRepositoryRoot() {
        for (
            DirectoryInfo? cursor =
                new DirectoryInfo(AppContext.BaseDirectory);
            cursor is not null;
            cursor = cursor.Parent
        ) {
            if (File.Exists(Path.Combine(
                    cursor.FullName,
                    "Atelia.sln"
                ))) {
                return cursor.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Atelia repository root from the test assembly path."
        );
    }
}
