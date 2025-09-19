using Xunit;
using CodeCortex.Tests.Util;
using Microsoft.CodeAnalysis;
using CodeCortexV2.Formatting;
using System.Text.Json;

namespace CodeCortex.Tests;

public class V2_JsonFormatterTests {
    private const string Source = @"namespace N {
    /// <summary>Demo type.</summary>
    public class C {
        /// <summary>Combine two values into a tuple.</summary>
        /// <typeparam name=""TKey"">Key type.</typeparam>
        /// <typeparam name=""TValue"">Value type.</typeparam>
        /// <param name=""key"">The <see cref=""TKey""/> key.</param>
        /// <param name=""value"">The <see cref=""TValue""/> value.</param>
        /// <returns>A tuple of ([key], [value]).</returns>
        /// <exception cref=""System.ArgumentNullException"">If <paramref name=""key""/> is null for reference types.</exception>
        public (TKey, TValue) Combine<TKey, TValue>(TKey key, TValue value) => (key, value);

        /// <summary>Table showcase</summary>
        /// <param name=""count"">Item count.</param>
        /// <returns>
        /// <list type=""table""><listheader><term>State</term><term>Meaning</term></listheader>
        /// <item><term>Empty</term><term>No items</term></item>
        /// <item><term>NonEmpty</term><term>Has items</term></item>
        /// </list>
        /// </returns>
        /// <exception cref=""System.ArgumentException"">
        /// <list type=""table""><listheader><term>Param</term><term>Rule</term></listheader>
        /// <item><term>count</term><term>Must be &gt;= 0</term></item>
        /// </list>
        /// </exception>
        public string TableShowcase(int count) => string.Empty;
    }
}";

    private static IMethodSymbol GetMethod(string typeName, string methodName) {
        var (_, type) = RoslynTestHost.CreateSingleType(Source, typeName);
        var m = type.GetMembers().OfType<IMethodSymbol>().First(x => x.Name == methodName);
        return m;
    }

    private static bool FindTableWithHeaders(JsonElement el, string[] headers) {
        if (el.ValueKind == JsonValueKind.Object) {
            if (el.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "table") {
                if (el.TryGetProperty("headers", out var hdrProp) && hdrProp.ValueKind == JsonValueKind.Array) {
                    var hs = hdrProp.EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (hs.SequenceEqual(headers)) { return true; }
                }
            }
            foreach (var prop in el.EnumerateObject()) {
                if (FindTableWithHeaders(prop.Value, headers)) { return true; }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array) {
            foreach (var child in el.EnumerateArray()) {
                if (FindTableWithHeaders(child, headers)) { return true; }
            }
        }
        return false;
    }
}

