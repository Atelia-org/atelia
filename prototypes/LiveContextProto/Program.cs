using System;
using System.Net.Http;
using System.Text;
using Atelia.Diagnostics;
using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;

namespace Atelia.LiveContextProto;

internal static class Program {
    private const string DebugCategory = "History";
    private const string DefaultProxyModel = "vscode-lm-proxy";
    private const string AnthropicProxyUrl = "http://localhost:4000/anthropic/";
    private const string DefaultProfileName = "anthropic-v1";

    public static int Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        DebugUtil.Print(DebugCategory, "LiveContextProto bootstrap starting");

        var anthropicClient = new AnthropicClient(apiKey: null, baseAddress: new Uri(AnthropicProxyUrl));
        var defaultProfile = new LlmProfile(anthropicClient, DefaultProxyModel, DefaultProfileName);

        var agent = new CharacterAgent();
        var loop = new ConsoleTui(agent, defaultProfile);

        loop.Run();

        DebugUtil.Print(DebugCategory, "LiveContextProto shutdown");
        return 0;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : value + "/";
}
