using System.CommandLine;
using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Completion.Anthropic;

namespace Atelia.DebugApps;

/// <summary>
/// PipeMux 入口：把 LiveContextProto 包装成可通过 pmux 命令多轮对话的 AI 同类。
///
/// 注册：
/// <code>
/// pmux :register peer /path/to/Atelia.DebugApps.dll Atelia.DebugApps.PeerEntry.BuildPeer --host-path /root/.local/bin/pmux-host
/// </code>
///
/// 状态：进程内单例 CharacterAgent，跨调用保留所有对话历史与状态机进度。
/// 默认连接 <c>http://localhost:8000/</c> 的 Qwen3.5-27b（与 LiveContextProto Program.cs 一致）。
/// </summary>
public static class PeerEntry {
    private const string DefaultEndpoint = "http://localhost:8000/";
    private const string DefaultModel = "Qwen3.5-27b-GPTQ-Int4";
    private const string DefaultProfileName = "anthropic-v1";

    private static AnthropicClient s_client = new(apiKey: null, baseAddress: new Uri(DefaultEndpoint));
    private static LlmProfile s_profile = new(s_client, DefaultModel, DefaultProfileName);
    private static CharacterAgent s_agent = CreateAgent();
    private static DurableTextApp? s_dtApp;

    private static CharacterAgent CreateAgent() {
        var agent = new CharacterAgent();
        s_dtApp = new DurableTextApp();
        agent.RegisterApp(s_dtApp);
        return agent;
    }

    public static RootCommand BuildPeer() {
        var root = new RootCommand("AI peer agent (LiveContextProto wrapped as pmux app)");
        root.Add(BuildAskCommand());
        root.Add(BuildStateCommand());
        root.Add(BuildSystemCommand());
        root.Add(BuildNotebookCommand());
        root.Add(BuildResetCommand());
        root.Add(BuildProfileCommand());
        root.Add(BuildDtCommand());
        return root;
    }

    private static Command BuildAskCommand() {
        var msgArg = new Argument<string>("message") { Description = "要发给 AI 同类的消息" };
        var quietOpt = new Option<bool>("--quiet") {
            Description = "只输出最终的文本回复",
            DefaultValueFactory = _ => false,
        };
        var cmd = new Command("ask", "发一条消息，等待 agent 状态机推进至空闲并打印所有输出") { msgArg, quietOpt };
        cmd.SetAction(async (ctx, ct) => {
            var msg = ctx.GetValue(msgArg)!;
            var quiet = ctx.GetValue(quietOpt);
            var output = ctx.InvocationConfiguration.Output;
            var error = ctx.InvocationConfiguration.Error;

            var notification = $"你收到消息:\n``````\n{msg}\n``````";
            s_agent.AppendNotification(notification);

            try {
                await DrainAsync(output, error, quiet, ct);
            }
            catch (Exception ex) {
                error.WriteLine($"模型调用失败: {ex.Message}");
            }
        });
        return cmd;
    }

    private static async Task DrainAsync(TextWriter output, TextWriter error, bool quiet, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            AgentStepResult step;
            try {
                step = await s_agent.DoStepAsync(s_profile, ct);
            }
            catch (Exception ex) {
                error.WriteLine($"DoStep 失败: {ex.Message}");
                return;
            }

            if (!step.ProgressMade) {
                if (step.BlockedOnInput) { return; }
                if (!quiet) {
                    error.WriteLine($"[warn] agent 状态机在 {step.StateAfter} 状态未能继续推进");
                }
                return;
            }

            if (step.Output is { } actionEntry) {
                var actionText = actionEntry.Message.GetFlattenedText();
                if (!string.IsNullOrEmpty(actionText)) {
                    output.WriteLine(actionText);
                }
                if (!quiet) {
                    foreach (var call in actionEntry.Message.ToolCalls) {
                        output.WriteLine($"  [ToolCall] {call.ToolCallId} {call.ToolName}");
                    }
                }
            }

            if (step.StateAfter == AgentRunState.WaitingInput) { return; }
        }
    }

    private static Command BuildStateCommand() {
        var cmd = new Command("state", "查看 agent 当前状态");
        cmd.SetAction(ctx => {
            var output = ctx.InvocationConfiguration.Output;
            output.WriteLine($"Profile:        {s_profile.Name} (model={s_profile.ModelId})");
            output.WriteLine($"Endpoint:       {DefaultEndpoint}");
            output.WriteLine($"HistoryEntries: {s_agent.State.RecentHistory.Count}");
            output.WriteLine($"PendingNotif:   {s_agent.State.HasPendingNotification}");
            output.WriteLine($"NotebookLen:    {s_agent.MemoryNotebookSnapshot.Length}");
        });
        return cmd;
    }

    private static Command BuildSystemCommand() {
        var cmd = new Command("system", "打印 agent 的 system prompt");
        cmd.SetAction(ctx => {
            ctx.InvocationConfiguration.Output.WriteLine(s_agent.SystemPrompt);
        });
        return cmd;
    }

    private static Command BuildNotebookCommand() {
        var cmd = new Command("notebook", "查看或修改记忆笔记");

        var viewCmd = new Command("view", "查看当前记忆笔记内容");
        viewCmd.SetAction(ctx => {
            var snap = s_agent.MemoryNotebookSnapshot;
            ctx.InvocationConfiguration.Output.WriteLine($"--- notebook ({snap.Length} chars) ---");
            ctx.InvocationConfiguration.Output.WriteLine(snap);
        });

        var contentArg = new Argument<string>("content") { Description = "新的笔记内容" };
        var setCmd = new Command("set", "完全替换笔记内容") { contentArg };
        setCmd.SetAction(ctx => {
            var content = ctx.GetValue(contentArg)!;
            s_agent.UpdateMemoryNotebook(content);
            ctx.InvocationConfiguration.Output.WriteLine($"笔记已更新（{content.Length} 字符）");
        });

        cmd.Add(viewCmd);
        cmd.Add(setCmd);
        return cmd;
    }

    private static Command BuildResetCommand() {
        var cmd = new Command("reset", "重新创建 CharacterAgent（清空所有历史与笔记，重新挂载 DurableTextApp）");
        cmd.SetAction(ctx => {
            s_agent = CreateAgent();
            ctx.InvocationConfiguration.Output.WriteLine("Agent reset (DurableTextApp re-registered).");
        });
        return cmd;
    }

    private static Command BuildDtCommand() {
        var cmd = new Command("dt", "查看 DurableTextApp 当前内容（旁观刘德智的编辑成果）");
        cmd.SetAction(ctx => {
            var output = ctx.InvocationConfiguration.Output;
            if (s_dtApp is null) {
                output.WriteLine("(DurableTextApp not initialized)");
                return;
            }
            var snap = s_dtApp.Snapshot();
            var blocks = snap.GetAllBlocks();
            output.WriteLine($"--- DurableText ({blocks.Count} blocks) ---");
            if (blocks.Count == 0) {
                output.WriteLine("(empty)");
                return;
            }
            foreach (var b in blocks) {
                output.WriteLine($"[{b.Id}] {b.Content}");
            }
        });
        return cmd;
    }

    private static Command BuildProfileCommand() {
        var modelOpt = new Option<string?>("--model") { Description = "切换模型名" };
        var endpointOpt = new Option<string?>("--endpoint") { Description = "切换 endpoint URL" };
        var cmd = new Command("profile", "查看或切换 LLM profile") { modelOpt, endpointOpt };
        cmd.SetAction(ctx => {
            var model = ctx.GetValue(modelOpt);
            var endpoint = ctx.GetValue(endpointOpt);
            var output = ctx.InvocationConfiguration.Output;

            if (model is not null || endpoint is not null) {
                var newModel = model ?? s_profile.ModelId;
                var newEndpoint = endpoint ?? DefaultEndpoint;
                s_client = new AnthropicClient(apiKey: null, baseAddress: new Uri(newEndpoint));
                s_profile = new LlmProfile(s_client, newModel, DefaultProfileName);
                output.WriteLine($"Profile updated: model={newModel} endpoint={newEndpoint}");
            }
            else {
                output.WriteLine($"Model:    {s_profile.ModelId}");
                output.WriteLine($"Profile:  {s_profile.Name}");
                output.WriteLine($"Endpoint: {DefaultEndpoint}");
            }
        });
        return cmd;
    }
}
