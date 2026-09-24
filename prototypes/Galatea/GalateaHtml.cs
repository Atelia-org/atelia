using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Atelia.Galatea.Server;

internal static class GalateaHtml {
    public static string RenderLoginPage(bool invalidCredentials, string assetVersion) {
        string errorHtml = invalidCredentials
            ? "<p class=\"error\">Player ID 或密码不正确。</p>"
            : string.Empty;

        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Galatea 登录</title>
    <link rel="stylesheet" href="{{stylesheetPath}}">
</head>
<body class="login-body">
  <main class="login-shell">
    <h1>Galatea</h1>
    <p class="login-copy">Player 登录</p>
    <p class="login-hint">首次启动后，请先确认 <code>.atelia/galatea/config.json</code>。</p>
    {{errorHtml}}
    <form method="post" action="/login" class="login-form">
      <label>Player ID<input name="playerId" autocomplete="username" required></label>
      <label>密码<input type="password" name="password" autocomplete="current-password" required></label>
      <button type="submit">登录</button>
    </form>
  </main>
</body>
</html>
""";
    }

    public static string RenderCharacterDirectory(
        GalateaPlayerConfig player,
        IReadOnlyList<GalateaCharacterConfig> characters,
        string assetVersion
    ) {
        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);
        string entries = string.Join("\n", characters.Select(character =>
            "<li><a href=\"/characters/" + Uri.EscapeDataString(character.CharacterId)
            + "\"><strong>" + WebUtility.HtmlEncode(character.CharacterName.Value)
            + "</strong><span>" + WebUtility.HtmlEncode(character.CharacterId)
            + "</span></a></li>"));
        if (characters.Count == 0) {
            entries = "<li>尚未配置角色。</li>";
        }
        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>选择角色 · Galatea</title>
  <link rel="stylesheet" href="{{stylesheetPath}}">
</head>
<body class="app-body">
  <main class="app-shell">
    <header class="character-header">
      <div><h1>选择角色</h1><span>Player：{{WebUtility.HtmlEncode(player.Name.Value)}}</span></div>
      <form method="post" action="/logout"><button type="submit" class="ghost-button">退出</button></form>
    </header>
    <ul class="character-directory">{{entries}}</ul>
  </main>
</body>
</html>
""";
    }

    public static string RenderAppPage(
        GalateaCharacterConfig character,
        GalateaPlayerConfig player,
        IReadOnlyList<GalateaConnectionInfoDto> connections,
        bool maintenanceMode,
        string assetVersion
    ) {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(character.DefaultConnectionId);
        string connectionsJson = JsonSerializer.Serialize(
            connections,
            GalateaJson.Options
        );
        string maintenanceBanner = maintenanceMode
            ? "<p class=\"maintenance-banner\" role=\"status\">维护模式：会话只读，发送、恢复、撤销与停止已禁用。</p>"
            : string.Empty;
        string maintenanceDisabled = maintenanceMode
            ? " disabled"
            : string.Empty;
        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);
        string scriptPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.js", assetVersion);
        string maximumStreamConnectionBytes =
            GalateaSseLimits.BrowserMaximumConnectionBytes.ToString(
                CultureInfo.InvariantCulture
            );
        string maximumStreamFrameBytes =
            GalateaSseLimits.BrowserMaximumFrameBytes.ToString(
                CultureInfo.InvariantCulture
            );

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{WebUtility.HtmlEncode(character.CharacterName.Value)}} · Galatea</title>
    <link rel="stylesheet" href="{{stylesheetPath}}">
</head>
<body class="app-body">
  <main class="app-shell">

    <header class="character-header">
      <div><h1>{{WebUtility.HtmlEncode(character.CharacterName.Value)}}</h1>
        <span>Character：{{WebUtility.HtmlEncode(character.CharacterId)}} · Player：{{WebUtility.HtmlEncode(player.Name.Value)}}</span></div>
      <nav><a href="/">切换角色</a><form method="post" action="/logout"><button type="submit" class="ghost-button">退出</button></form></nav>
    </header>
    {{maintenanceBanner}}

    <section class="composer">
      <form id="chat-form">
        <fieldset id="connection-picker" class="connection-picker" aria-label="模型连接">
          <legend>模型连接</legend>
        </fieldset>
        <section id="recap-planning-status" class="recap-planning-status hidden" aria-live="polite" title="HistoryLoad 是 Timeline cadence 的内部度量，不是模型 token 数或完整 context window 占用。">
          <div id="recap-cadence-summary" class="recap-planning-summary"></div>
          <progress id="recap-planning-progress" class="recap-planning-progress hidden" max="1" value="0"></progress>
          <div id="recap-cadence-detail" class="recap-planning-detail"></div>
          <div class="recap-grid-readiness">
            <div id="recap-planning-summary" class="recap-planning-summary"></div>
            <div id="recap-planning-detail" class="recap-planning-detail"></div>
          </div>
          <div class="recap-planning-note">HistoryLoad 不是模型 token 数，也不是完整 context window 占用</div>
        </section>
        <textarea id="message-input" rows="3" placeholder="说点什么……" required{{maintenanceDisabled}}></textarea>
        <div id="autonomy-status" class="autonomy-status">
          <span id="autonomy-state" role="status" aria-live="polite">服务端 Agent：正在读取…</span>
          <span id="autonomy-connection"></span>
          <span id="current-turn-connection"></span>
          <span id="autonomy-countdown" aria-live="off"></span>
          <span id="autonomy-last-activation" aria-live="off">上次自主激活：尚无</span>
          <button id="retry-admission" type="button" class="hidden">重试未完成处理</button>
          <button id="stop-admission" type="button" class="hidden">停止本次整理</button>
        </div>
        <div id="mailbox-status" class="mailbox-status" role="status" aria-live="polite">邮箱状态：正在读取…</div>
        <div class="composer-actions">
          <div class="composer-status">
            <span id="composer-mode-hint" class="eyebrow hidden"></span>
            <span id="status-text" class="status-text"></span>
          </div>
          <div class="composer-buttons">
            <button id="resume-turn-button" type="button" class="ghost-button" disabled>恢复待处理轮次</button>
            <button id="pending-stop-button" type="button" class="ghost-button" disabled>结束待处理轮次</button>
            <button id="undo-last-button" type="button" class="ghost-button"{{maintenanceDisabled}}>撤销上一轮</button>
            <button id="stop-button" type="button" class="ghost-button"{{maintenanceDisabled}}>停止</button>
            <button id="send-button" type="submit"{{maintenanceDisabled}}>发送</button>
          </div>
        </div>
      </form>
    </section>

    <section id="live-turn" class="live-turn hidden" aria-live="polite">
      <article class="turn-card assistant live">
        <header id="live-turn-context">{{WebUtility.HtmlEncode(character.CharacterName.Value)}}</header>
        <details class="reasoning-panel hidden" id="live-reasoning-panel">
          <summary>Reasoning</summary>
          <pre id="live-reasoning"></pre>
        </details>
        <pre id="live-text"></pre>
      </article>
    </section>

    <section class="history">
      <div id="turn-list" class="turn-list"></div>
    </section>

    <button id="scroll-to-top" class="scroll-to-top" title="回到顶端">↑ 回到顶端</button>
  </main>

  <script>
    window.galateaBootstrap = {
      characterId: {{JsonSerializer.Serialize(character.CharacterId, GalateaJson.Options)}},
      characterName: {{JsonSerializer.Serialize(character.CharacterName.Value, GalateaJson.Options)}},
      apiBase: {{JsonSerializer.Serialize("/api/v1/characters/" + Uri.EscapeDataString(character.CharacterId), GalateaJson.Options)}},
      connections: {{connectionsJson}},
      maintenanceMode: {{JsonSerializer.Serialize(maintenanceMode, GalateaJson.Options)}},
      streamLimits: {
        maximumConnectionBytes: {{maximumStreamConnectionBytes}},
        maximumFrameBytes: {{maximumStreamFrameBytes}}
      }
    };
  </script>
    <script type="module" src="{{scriptPath}}"></script>
</body>
</html>
""";
    }
}
