using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core.Tool;
using Atelia.Agent.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class TextEditor2WidgetTests {
    [Fact]
    public void ReplaceAsync_OnValidationError_DoesNotEmitSchemaViolationFlag() {
        // Arrange
        var widget = new TextEditor2Widget("MyFile.txt", "myfile", "initial content");
        var replaceAsync = GetMethod(nameof(TextEditor2Widget), "ReplaceAsync");

        // Act
        var result = InvokeAsync(replaceAsync, widget, string.Empty, "bar");

        // Assert
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.DoesNotContain("`SchemaViolation`", result.Result.Detail);
        Assert.Contains("`DiagnosticHint`", result.Result.Detail);
    }

    [Fact]
    public void ReplaceSelectionAsync_OnExternalConflict_EmitsOutOfSyncFlags() {
        // Arrange
        var widget = new TextEditor2Widget("MyFile.txt", "myfile", "foo foo");

        var replaceAsync = GetMethod(nameof(TextEditor2Widget), "ReplaceAsync");
        var replaceSelectionAsync = GetMethod(nameof(TextEditor2Widget), "ReplaceSelectionAsync");
        var currentTextField = typeof(TextEditor2Widget).GetField("_currentText", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate _currentText field.");
        var stateControllerField = typeof(TextEditor2Widget).GetField("_stateController", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate _stateController field.");

        // Act 1: generate multi-match selections
        var multiMatchResult = InvokeAsync(replaceAsync, widget, "foo", "bar");
        Assert.Equal(ToolExecutionStatus.Success, multiMatchResult.Status);
        Assert.Contains("status: `MultiMatch`", multiMatchResult.Result.Detail);

        // mutate underlying text to force a conflict
        var conflictText = "baz baz";
        currentTextField.SetValue(widget, conflictText);

        // Act 2: attempt to apply selection -> should detect external conflict
        var conflictResult = InvokeAsync(replaceSelectionAsync, widget, 1, null);

        // Assert
        Assert.Equal(ToolExecutionStatus.Failed, conflictResult.Status);
        Assert.Contains("status: `ExternalConflict`", conflictResult.Result.Detail);
        Assert.Contains("state: `OutOfSync`", conflictResult.Result.Detail);
        Assert.Contains("`ExternalConflict`", conflictResult.Result.Detail);
        Assert.Contains("`DiagnosticHint`", conflictResult.Result.Detail);
        Assert.Contains("请重新调用 myfile_replace 工具生成新的选区。", conflictResult.Result.Detail);

        var stateController = (TextEditStateController)stateControllerField.GetValue(widget)!;
        Assert.Equal(TextEditWorkflowState.OutOfSync, stateController.CurrentState);
    }

    private static MethodInfo GetMethod(string typeName, string methodName) {
        return typeof(TextEditor2Widget).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate method {methodName} on {typeName}.");
    }

    private static LodToolExecuteResult InvokeAsync(MethodInfo method, object instance, params object?[]? parameters) {
        var methodParameters = method.GetParameters();
        object?[] args;

        if (parameters is null || parameters.Length == 0) {
            args = Array.Empty<object?>();
        }
        else {
            args = new object?[parameters.Length];
            Array.Copy(parameters, args, parameters.Length);
        }

        if (methodParameters.Length > args.Length) {
            if (methodParameters.Length - args.Length != 1 || methodParameters[^1].ParameterType != typeof(CancellationToken)) { throw new InvalidOperationException($"Unable to supply parameters for {method.Name}."); }

            Array.Resize(ref args, methodParameters.Length);
            args[^1] = CancellationToken.None;
        }

        var result = method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Invocation of {method.Name} returned null.");

        if (result is ValueTask<LodToolExecuteResult> valueTask) { return valueTask.GetAwaiter().GetResult(); }

        throw new InvalidOperationException($"Unexpected return type for {method.Name}: {result.GetType().FullName}");
    }
}
