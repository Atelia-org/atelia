using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal.RecapGrid;

if (args.Length > 1) {
    Console.Error.WriteLine(
        "Usage: dotnet run --project scripts/SessionJournal.RecapGrid.ApiInventory -- [output-path]"
    );
    return 2;
}

Assembly target = typeof(FamilyDefinition).Assembly;
string assemblyName = target.GetName().Name
    ?? throw new InvalidOperationException("The target assembly has no name.");
if (!string.Equals(
        assemblyName,
        "Atelia.SessionJournal.RecapGrid",
        StringComparison.Ordinal)) {
    throw new InvalidOperationException(
        $"Unexpected target assembly: {assemblyName}."
    );
}

Inventory first = BuildInventory(target, assemblyName);
Inventory second = BuildInventory(target, assemblyName);
if (!first.Bytes.AsSpan().SequenceEqual(second.Bytes)) {
    throw new InvalidOperationException(
        "The API inventory was not byte-stable across repeated generation."
    );
}

if (args.Length == 0 || string.Equals(args[0], "-", StringComparison.Ordinal)) {
    Console.OpenStandardOutput().Write(first.Bytes);
}
else {
    string outputPath = args[0];
    string? outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory)) {
        Directory.CreateDirectory(outputDirectory);
    }
    File.WriteAllBytes(outputPath, first.Bytes);
}

string sha256 = Convert.ToHexString(SHA256.HashData(first.Bytes))
    .ToLowerInvariant();
Console.Error.WriteLine(
    $"API inventory: assembly={assemblyName} types={first.TypeCount} "
    + $"members={first.MemberCount} lines={first.LineCount} "
    + $"sha256={sha256} deterministic=true"
);
return 0;

static Inventory BuildInventory(Assembly assembly, string assemblyName) {
    Type[] types = assembly.GetTypes()
        .Where(IsEffectivelyPublic)
        .OrderBy(TypeId, StringComparer.Ordinal)
        .ToArray();
    var typeLines = new List<string>(types.Length);
    var members = new List<MemberInventory>();
    foreach (Type type in types) {
        string id = TypeId(type);
        typeLines.Add(JsonSerializer.Serialize(new {
            k = "t",
            id,
            a = TypeAccessibility(type),
            c = TypeCategory(type)
        }));
        members.AddRange(DeclaredApiMembers(type));
    }
    members.Sort(static (left, right) =>
        StringComparer.Ordinal.Compare(left.Id, right.Id));

    // formatVersion 1 is one summary row, followed by k=t rows (id, a, c)
    // and k=m rows (id, a). a=p/f/fo means public/protected/
    // protected-internal; c=c/s/e/i/d means class/struct/enum/interface/
    // delegate. Member ids embed declaring type, kind and signature.
    string summary = JsonSerializer.Serialize(new {
        kind = "summary",
        formatVersion = 1,
        assembly = assemblyName,
        typeCount = types.Length,
        memberCount = members.Count
    });
    var lines = new List<string>(1 + typeLines.Count + members.Count) {
        summary
    };
    lines.AddRange(typeLines);
    lines.AddRange(members.Select(static member => member.Json));
    byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    return new Inventory(bytes, types.Length, members.Count, lines.Count);
}

static IEnumerable<MemberInventory> DeclaredApiMembers(Type type) {
    const BindingFlags flags = BindingFlags.DeclaredOnly
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.Public
        | BindingFlags.NonPublic;
    PropertyInfo[] properties = type.GetProperties(flags);
    EventInfo[] events = type.GetEvents(flags);
    var accessors = new HashSet<MethodInfo>(
        properties.SelectMany(static property => new[] {
            property.GetGetMethod(nonPublic: true),
            property.GetSetMethod(nonPublic: true)
        }).Concat(events.SelectMany(static eventInfo => new[] {
            eventInfo.GetAddMethod(nonPublic: true),
            eventInfo.GetRemoveMethod(nonPublic: true),
            eventInfo.GetRaiseMethod(nonPublic: true)
        })).OfType<MethodInfo>()
    );

    foreach (ConstructorInfo constructor in type.GetConstructors(flags)
                 .Where(IsApiVisibleMethod)) {
        string signature = $".ctor({Parameters(constructor)})";
        yield return Member(type, "constructor",
            MethodAccessibility(constructor), signature);
    }
    foreach (MethodInfo method in type.GetMethods(flags)
                 .Where(IsApiVisibleMethod)
                 .Where(method => !accessors.Contains(method))) {
        string genericArity = method.IsGenericMethodDefinition
            ? $"``{method.GetGenericArguments().Length}"
            : string.Empty;
        string signature = $"{FormatType(method.ReturnType)} "
            + $"{method.Name}{genericArity}({Parameters(method)})";
        yield return Member(type, "method",
            MethodAccessibility(method), signature);
    }
    foreach (FieldInfo field in type.GetFields(flags).Where(IsApiVisibleField)) {
        string modifier = field.IsLiteral
            ? "const "
            : field.IsInitOnly ? "readonly " : string.Empty;
        string signature = $"{modifier}{FormatType(field.FieldType)} {field.Name}";
        yield return Member(type, "field",
            FieldAccessibility(field), signature);
    }
    foreach (PropertyInfo property in properties
                 .Where(static property => ApiPropertyAccessors(property).Any())) {
        MethodInfo[] visibleAccessors = ApiPropertyAccessors(property).ToArray();
        string indices = string.Join(", ", property.GetIndexParameters()
            .Select(FormatParameter));
        string indexSuffix = indices.Length == 0 ? string.Empty : $"[{indices}]";
        string accessorText = string.Join(", ", visibleAccessors
            .OrderBy(static accessor => accessor.Name, StringComparer.Ordinal)
            .Select(accessor => $"{AccessorKind(accessor)}:{MethodAccessibility(accessor)}"));
        string signature = $"{FormatType(property.PropertyType)} "
            + $"{property.Name}{indexSuffix} {{{accessorText}}}";
        yield return Member(type, "property",
            MostVisible(visibleAccessors), signature);
    }
    foreach (EventInfo eventInfo in events
                 .Where(static eventInfo => ApiEventAccessors(eventInfo).Any())) {
        MethodInfo[] visibleAccessors = ApiEventAccessors(eventInfo).ToArray();
        string accessorText = string.Join(", ", visibleAccessors
            .OrderBy(static accessor => accessor.Name, StringComparer.Ordinal)
            .Select(accessor => $"{AccessorKind(accessor)}:{MethodAccessibility(accessor)}"));
        string signature = $"{FormatType(eventInfo.EventHandlerType!)} "
            + $"{eventInfo.Name} {{{accessorText}}}";
        yield return Member(type, "event",
            MostVisible(visibleAccessors), signature);
    }
}

static MemberInventory Member(
    Type declaringType,
    string memberKind,
    string accessibility,
    string signature
) {
    string declaringTypeId = TypeId(declaringType);
    string id = $"{declaringTypeId}::{memberKind}:{signature}";
    string json = JsonSerializer.Serialize(new {
        k = "m",
        id,
        a = accessibility
    });
    return new MemberInventory(id, json);
}

static IEnumerable<MethodInfo> ApiPropertyAccessors(PropertyInfo property) {
    MethodInfo? getter = property.GetGetMethod(nonPublic: true);
    MethodInfo? setter = property.GetSetMethod(nonPublic: true);
    if (getter is not null && IsApiVisibleMethod(getter)) {
        yield return getter;
    }
    if (setter is not null && IsApiVisibleMethod(setter)) {
        yield return setter;
    }
}

static IEnumerable<MethodInfo> ApiEventAccessors(EventInfo eventInfo) {
    MethodInfo? add = eventInfo.GetAddMethod(nonPublic: true);
    MethodInfo? remove = eventInfo.GetRemoveMethod(nonPublic: true);
    MethodInfo? raise = eventInfo.GetRaiseMethod(nonPublic: true);
    if (add is not null && IsApiVisibleMethod(add)) {
        yield return add;
    }
    if (remove is not null && IsApiVisibleMethod(remove)) {
        yield return remove;
    }
    if (raise is not null && IsApiVisibleMethod(raise)) {
        yield return raise;
    }
}

static string Parameters(MethodBase method)
    => string.Join(", ", method.GetParameters().Select(FormatParameter));

static string FormatParameter(ParameterInfo parameter) {
    Type parameterType = parameter.ParameterType;
    string modifier = string.Empty;
    if (parameterType.IsByRef) {
        modifier = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
        parameterType = parameterType.GetElementType()
            ?? throw new InvalidOperationException("A by-ref parameter has no element type.");
    }
    string name = parameter.Name ?? $"arg{parameter.Position}";
    string optional = parameter.IsOptional ? " = optional" : string.Empty;
    return $"{modifier}{FormatType(parameterType)} {name}{optional}";
}

static string FormatType(Type type) {
    if (type.IsByRef) {
        return FormatType(type.GetElementType()!) + "&";
    }
    if (type.IsPointer) {
        return FormatType(type.GetElementType()!) + "*";
    }
    if (type.IsArray) {
        int rank = type.GetArrayRank();
        return FormatType(type.GetElementType()!)
            + "[" + new string(',', rank - 1) + "]";
    }
    if (type.IsGenericParameter) {
        string prefix = type.DeclaringMethod is null ? "!" : "!!";
        return $"{prefix}{type.GenericParameterPosition}:{type.Name}";
    }
    if (type.IsGenericType) {
        Type definition = type.GetGenericTypeDefinition();
        string definitionName = definition.FullName ?? definition.Name;
        return definitionName + "["
            + string.Join(",", type.GetGenericArguments().Select(FormatType))
            + "]";
    }
    return type.FullName ?? type.Name;
}

static string TypeId(Type type) => FormatType(type);

static bool IsEffectivelyPublic(Type type) {
    if (type.DeclaringType is null) {
        return type.IsPublic;
    }
    return IsEffectivelyPublic(type.DeclaringType)
        && (type.IsNestedPublic
            || type.IsNestedFamily
            || type.IsNestedFamORAssem);
}

static bool IsApiVisibleMethod(MethodBase method)
    => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

static bool IsApiVisibleField(FieldInfo field)
    => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

static string TypeAccessibility(Type type) {
    if (type.IsPublic || type.IsNestedPublic) {
        return "p";
    }
    if (type.IsNestedFamily) {
        return "f";
    }
    if (type.IsNestedFamORAssem) {
        return "fo";
    }
    throw new InvalidOperationException($"Type is not API-visible: {type}.");
}

static string MethodAccessibility(MethodBase method) {
    if (method.IsPublic) {
        return "p";
    }
    if (method.IsFamily) {
        return "f";
    }
    if (method.IsFamilyOrAssembly) {
        return "fo";
    }
    throw new InvalidOperationException($"Method is not API-visible: {method}.");
}

static string FieldAccessibility(FieldInfo field) {
    if (field.IsPublic) {
        return "p";
    }
    if (field.IsFamily) {
        return "f";
    }
    if (field.IsFamilyOrAssembly) {
        return "fo";
    }
    throw new InvalidOperationException($"Field is not API-visible: {field}.");
}

static string MostVisible(IEnumerable<MethodInfo> methods)
    => methods.Select(MethodAccessibility).OrderBy(static value => value switch {
        "p" => 0,
        "fo" => 1,
        "f" => 2,
        _ => 3
    }).First();

static string AccessorKind(MethodInfo method) {
    if (method.Name.StartsWith("get_", StringComparison.Ordinal)) {
        return "get";
    }
    if (method.Name.StartsWith("set_", StringComparison.Ordinal)) {
        return "set";
    }
    if (method.Name.StartsWith("add_", StringComparison.Ordinal)) {
        return "add";
    }
    if (method.Name.StartsWith("remove_", StringComparison.Ordinal)) {
        return "remove";
    }
    return "raise";
}

static string TypeCategory(Type type) {
    if (type.IsEnum) {
        return "e";
    }
    if (type.IsInterface) {
        return "i";
    }
    if (typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)) {
        return "d";
    }
    return type.IsValueType ? "s" : "c";
}

internal sealed record Inventory(
    byte[] Bytes,
    int TypeCount,
    int MemberCount,
    int LineCount
);

internal sealed record MemberInventory(string Id, string Json);
