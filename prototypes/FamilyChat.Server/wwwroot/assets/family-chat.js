(function () {
  const bootstrapConfig = window.familyChatBootstrap ?? {};
  const connections = Array.isArray(bootstrapConfig.connections) ? bootstrapConfig.connections : [];
  const userKey = bootstrapConfig.userId ?? "anonymous";

  const state = {
    recentTurns: [],
    pendingPoppedTurn: null,
    liveText: "",
    liveReasoning: "",
    streaming: false,
    stopRequested: false,
    activeTurnId: null,
    streamGeneration: 0,
    selectedConnectionId: null,
  };

  function resolveConnectionId(candidate) {
    if (candidate && connections.some((c) => c.id === candidate)) {
      return candidate;
    }
    if (
      bootstrapConfig.defaultConnectionId
      && connections.some((c) => c.id === bootstrapConfig.defaultConnectionId)
    ) {
      return bootstrapConfig.defaultConnectionId;
    }
    return connections.length > 0 ? connections[0].id : null;
  }

  function getConnection(connectionId) {
    return connections.find((c) => c.id === connectionId) ?? null;
  }

  function connectionStorageKey() {
    return ["family-chat", "connection", userKey].join(":");
  }

  function autoPrefillStorageKey(connectionId) {
    return ["family-chat", "auto-prefill-think-open-tag", userKey, connectionId ?? "unknown"].join(":");
  }

  const turnList = document.getElementById("turn-list");
  const form = document.getElementById("chat-form");
  const input = document.getElementById("message-input");
  const sendButton = document.getElementById("send-button");
  const undoLastButton = document.getElementById("undo-last-button");
  const stopButton = document.getElementById("stop-button");
  const autoPrefillThinkOpenTagCheckbox = document.getElementById("auto-repair-missing-think-open-tag");
  const connectionPicker = document.getElementById("connection-picker");
  const composerModeHint = document.getElementById("composer-mode-hint");
  const statusText = document.getElementById("status-text");
  const liveTurn = document.getElementById("live-turn");
  const liveText = document.getElementById("live-text");
  const liveReasoning = document.getElementById("live-reasoning");
  const liveReasoningPanel = document.getElementById("live-reasoning-panel");
  const scrollToTop = document.getElementById("scroll-to-top");

  scrollToTop?.addEventListener("click", () => {
    input.scrollIntoView({ behavior: "smooth", block: "start" });
    input.focus();
  });

  async function fetchJson(url, options) {
    const response = await fetch(url, {
      credentials: "same-origin",
      ...options,
    });
    if (!response.ok) {
      throw new Error(await response.text());
    }
    return await response.json();
  }

  function escapeHtml(text) {
    return text
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;");
  }

  function renderTurns() {
    turnList.innerHTML = state.recentTurns.map(renderTurn).join("");
    refreshComposerMode();
  }

  function renderTurn(turn) {
    if (turn.isRecap) {
      return `
        <article class="turn-card recap-card">
          <header>Earlier Summary</header>
          <pre>${escapeHtml(turn.assistant.text ?? "")}</pre>
        </article>
      `;
    }

    const reasoning = turn.assistant.hasReasoning
      ? `<details class="reasoning-panel"><summary>Reasoning</summary><pre>${escapeHtml(turn.assistant.reasoningText ?? "")}</pre></details>`
      : "";

    return `
      <article class="turn-card assistant">
        <header>Assistant</header>
        ${reasoning}
        <pre>${escapeHtml(turn.assistant.text ?? "")}</pre>
      </article>
      <article class="turn-card user">
        <header>User</header>
        <pre>${escapeHtml(turn.userText ?? "")}</pre>
      </article>
    `;
  }

  function setStreaming(streaming, status) {
    state.streaming = streaming;
    sendButton.disabled = streaming;
    input.disabled = streaming;
    if (stopButton) {
      stopButton.disabled = !streaming;
    }
    if (autoPrefillThinkOpenTagCheckbox) {
      autoPrefillThinkOpenTagCheckbox.disabled = streaming;
    }
    if (connectionPicker) {
      connectionPicker.querySelectorAll('input[name="connection"]').forEach((radio) => {
        radio.disabled = streaming;
      });
    }
    statusText.textContent = status || "";
    refreshComposerMode();
  }

  function refreshComposerMode() {
    if (composerModeHint) {
      if (state.pendingPoppedTurn) {
        composerModeHint.textContent = "最近一轮已撤销。修改后重新发送即可。";
        composerModeHint.classList.remove("hidden");
      } else {
        composerModeHint.textContent = "";
        composerModeHint.classList.add("hidden");
      }
    }

    if (undoLastButton) {
      undoLastButton.disabled = state.streaming || state.pendingPoppedTurn !== null || !hasUndoableTurn();
    }
  }

  function hasUndoableTurn() {
    return state.recentTurns.some((turn) => !turn.isRecap);
  }

  function clearPendingPoppedTurn() {
    state.pendingPoppedTurn = null;
    refreshComposerMode();
  }

  function resetLive() {
    state.liveText = "";
    state.liveReasoning = "";
    liveText.textContent = "";
    liveReasoning.textContent = "";
    liveTurn.classList.add("hidden");
    liveReasoningPanel.classList.add("hidden");
  }

  function beginLive() {
    state.liveText = "";
    state.liveReasoning = "";
    liveText.textContent = "";
    liveReasoning.textContent = "";
    liveTurn.classList.remove("hidden");
    liveReasoningPanel.classList.add("hidden");
  }

  function clearActiveTurn() {
    state.activeTurnId = null;
    state.stopRequested = false;
    state.streamGeneration += 1;
  }

  function loadAutoPrefillPreference(connectionId) {
    const stored = window.localStorage.getItem(autoPrefillStorageKey(connectionId));
    if (stored === "true") {
      return true;
    }

    if (stored === "false") {
      return false;
    }

    const connection = getConnection(connectionId);
    return connection?.defaultAutoPrefillThinkOpenTag === true;
  }

  function saveAutoPrefillPreference() {
    if (!autoPrefillThinkOpenTagCheckbox) {
      return;
    }

    window.localStorage.setItem(
      autoPrefillStorageKey(state.selectedConnectionId),
      autoPrefillThinkOpenTagCheckbox.checked ? "true" : "false"
    );
  }

  function escapeAttr(text) {
    return escapeHtml(text).replaceAll('"', "&quot;");
  }

  function renderConnectionPicker() {
    if (!connectionPicker) {
      return;
    }

    if (connections.length <= 1) {
      // A single (or no) connection needs no picker; keep it hidden but functional.
      connectionPicker.classList.add("hidden");
      return;
    }

    connectionPicker.classList.remove("hidden");
    const legend = "<legend>\u6a21\u578b\u8fde\u63a5</legend>";
    const options = connections
      .map((connection) => {
        const checked = connection.id === state.selectedConnectionId ? " checked" : "";
        return `
          <label class="connection-option">
            <input type="radio" name="connection" value="${escapeAttr(connection.id)}"${checked}>
            <span class="connection-name">${escapeHtml(connection.displayName ?? connection.id)}</span>
            <span class="connection-model">${escapeHtml(connection.modelId ?? "")}</span>
          </label>
        `;
      })
      .join("");
    connectionPicker.innerHTML = legend + options;

    connectionPicker.querySelectorAll('input[name="connection"]').forEach((radio) => {
      radio.disabled = state.streaming;
      radio.addEventListener("change", () => {
        if (radio.checked) {
          selectConnection(radio.value, { persist: true, reloadAutoPrefill: true });
        }
      });
    });
  }

  function selectConnection(connectionId, options = {}) {
    const { persist = false, reloadAutoPrefill = false, updateRadio = false } = options;
    const resolved = resolveConnectionId(connectionId);
    state.selectedConnectionId = resolved;

    if (persist && resolved) {
      window.localStorage.setItem(connectionStorageKey(), resolved);
    }

    if (updateRadio && connectionPicker) {
      connectionPicker.querySelectorAll('input[name="connection"]').forEach((radio) => {
        radio.checked = radio.value === resolved;
      });
    }

    if (reloadAutoPrefill && autoPrefillThinkOpenTagCheckbox) {
      autoPrefillThinkOpenTagCheckbox.checked = loadAutoPrefillPreference(resolved);
    }
  }

  async function loadRecentTurns() {
    applyRecentTurnsPayload(await fetchJson("/api/recent-turns"));
    renderTurns();
  }

  function applyRecentTurnsPayload(payload) {
    state.recentTurns = payload?.turns ?? [];
  }

  async function loadCurrentTurn() {
    return await fetchJson("/api/chat/turns/current");
  }

  async function waitForCurrentTurnIdle() {
    while (true) {
      const currentTurn = await loadCurrentTurn();
      if (currentTurn?.status === "idle") {
        return;
      }

      await sleep(250);
    }
  }

  function sleep(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms));
  }

  async function readEventStream(response) {
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });

      while (true) {
        const separatorIndex = buffer.indexOf("\n\n");
        if (separatorIndex < 0) {
          break;
        }

        const rawEvent = buffer.slice(0, separatorIndex);
        buffer = buffer.slice(separatorIndex + 2);
        handleRawEvent(rawEvent);
      }
    }
  }

  function handleRawEvent(rawEvent) {
    let eventName = "message";
    let data = "";

    for (const line of rawEvent.split("\n")) {
      if (line.startsWith("event:")) {
        eventName = line.slice("event:".length).trim();
      } else if (line.startsWith("data:")) {
        data += line.slice("data:".length).trim();
      }
    }

    const payload = data ? JSON.parse(data) : null;
    handleEvent(eventName, payload);
  }

  async function handleEvent(eventName, payload) {
    switch (eventName) {
      case "meta":
        if (payload?.phase === "turn-start") {
          setStreaming(true, "正在生成…");
        } else if (payload?.phase === "input-normalization-start") {
          setStreaming(true, "正在清洗输入…");
        } else if (payload?.phase === "input-normalization-finish") {
          if (payload?.changed) {
            setStreaming(true, "已纠正输入，继续生成…");
          } else {
            setStreaming(true, "输入清洗完成，继续生成…");
          }
        } else if (payload?.phase === "compaction-start") {
          setStreaming(true, "正在压缩上下文…");
        } else if (payload?.phase === "compaction-finish") {
          setStreaming(true, "上下文压缩完成，继续生成…");
        } else if (payload?.phase === "tool-loop-start") {
          setStreaming(true, "正在调用工具…");
        }
        break;
      case "reasoning-delta":
        state.liveReasoning += payload?.delta ?? "";
        liveReasoning.textContent = state.liveReasoning;
        liveReasoningPanel.classList.toggle("hidden", state.liveReasoning.length === 0);
        break;
      case "text-delta":
        state.liveText += payload?.delta ?? "";
        liveText.textContent = state.liveText;
        break;
      case "done":
        applyRecentTurnsPayload({
          turns: payload?.recentTurns ?? [],
        });
        clearPendingPoppedTurn();
        renderTurns();
        clearActiveTurn();
        resetLive();
        setStreaming(true, "正在收尾…");
        input.value = "";
        break;
      case "error":
        clearActiveTurn();
        setStreaming(false, payload?.message ?? "请求失败");
        break;
    }
  }

  async function popLatestTurn(status) {
    if (state.pendingPoppedTurn) {
      return state.pendingPoppedTurn;
    }

    setStreaming(true, status || "正在取出最近一轮…");

    const response = await fetch("/api/chat/turns/pop-latest", {
      method: "POST",
      credentials: "same-origin",
    });

    const payload = await response.json().catch(() => null);

    if (response.status === 409) {
      if (payload?.turnId) {
        await attachToTurn(payload.turnId, payload.error || "正在恢复生成…");
        return null;
      }

      await loadRecentTurns().catch(() => {});
      setStreaming(false, payload?.error || "当前没有可取出的最近一轮");
      return null;
    }

    if (!response.ok) {
      setStreaming(false, payload?.error || "请求失败");
      return null;
    }

    state.pendingPoppedTurn = payload?.turn ?? null;
    if (!state.pendingPoppedTurn) {
      setStreaming(false, "当前没有可取出的最近一轮");
      return null;
    }

    const firstUndoableTurnIndex = state.recentTurns.findIndex((turn) => !turn.isRecap);
    if (firstUndoableTurnIndex >= 0) {
      state.recentTurns = [
        ...state.recentTurns.slice(0, firstUndoableTurnIndex),
        ...state.recentTurns.slice(firstUndoableTurnIndex + 1),
      ];
    }
    input.value = state.pendingPoppedTurn.userText ?? "";
    renderTurns();
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
    setStreaming(false, "");
    return state.pendingPoppedTurn;
  }

  async function attachToTurn(turnId, status) {
    const normalizedTurnId = turnId ?? "";
    if (!normalizedTurnId) {
      return;
    }

    state.activeTurnId = normalizedTurnId;
    const generation = ++state.streamGeneration;

    while (state.activeTurnId === normalizedTurnId && generation === state.streamGeneration) {
      beginLive();
      setStreaming(true, status || "正在连接生成流…");

      try {
        const response = await fetch(`/api/chat/turns/${encodeURIComponent(normalizedTurnId)}/events`, {
          credentials: "same-origin",
        });

        if (response.status === 404) {
          clearActiveTurn();
          resetLive();
          setStreaming(false, "生成任务已结束或不存在");
          await loadRecentTurns();
          return;
        }

        if (!response.ok || !response.body) {
          throw new Error("连接生成流失败");
        }

        await readEventStream(response);
        if (state.activeTurnId !== normalizedTurnId || generation !== state.streamGeneration) {
          if (!state.activeTurnId) {
            await waitForCurrentTurnIdle();
            setStreaming(false, "");
          }
          return;
        }

        if (!state.streaming) {
          return;
        }

        setStreaming(true, "连接已断开，正在重连…");
      } catch (error) {
        if (state.activeTurnId !== normalizedTurnId || generation !== state.streamGeneration) {
          return;
        }

        setStreaming(true, error?.message || "连接已断开，正在重连…");
      }

      await sleep(800);
      if (state.activeTurnId !== normalizedTurnId || generation !== state.streamGeneration) {
        return;
      }
    }
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (state.streaming) {
      return;
    }

    const message = input.value.trim();
    if (!message) {
      return;
    }

    const replacingPoppedTurn = state.pendingPoppedTurn !== null;
    state.stopRequested = false;
    setStreaming(true, replacingPoppedTurn ? "正在重新生成…" : "正在发送…");

    const response = await fetch("/api/chat/turns", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        message,
        autoPrefillThinkOpenTag: autoPrefillThinkOpenTagCheckbox?.checked ?? false,
        connectionId: state.selectedConnectionId,
      }),
    });

    const payload = await response.json().catch(() => null);

    if (response.status === 409) {
      if (payload?.turnId) {
        await attachToTurn(payload.turnId, payload.error || "正在恢复生成…");
        return;
      }

      setStreaming(false, payload?.error || "该账号当前正在生成，请稍后。");
      return;
    }

    if (!response.ok) {
      setStreaming(false, payload?.error || "请求失败");
      return;
    }

    if (replacingPoppedTurn) {
      await attachToTurn(payload?.turnId, "正在重新生成…");
      return;
    }

    await attachToTurn(payload?.turnId, "正在生成…");
  });

  undoLastButton?.addEventListener("click", async () => {
    if (state.streaming || state.pendingPoppedTurn || !hasUndoableTurn()) {
      return;
    }

    await popLatestTurn("正在撤销最近一轮…");
  });

  stopButton?.addEventListener("click", async () => {
    if (!state.streaming || !state.activeTurnId) {
      return;
    }

    state.stopRequested = true;
    setStreaming(true, "正在停止生成…");

    const response = await fetch(`/api/chat/turns/${encodeURIComponent(state.activeTurnId)}/stop`, {
      method: "POST",
      credentials: "same-origin",
    });

    const payload = await response.json().catch(() => null);
    if (!response.ok) {
      state.stopRequested = false;
      setStreaming(true, payload?.error || "停止请求失败，继续等待生成完成…");
      return;
    }

    setStreaming(true, "已发送停止请求，等待模型收尾…");
  });

  autoPrefillThinkOpenTagCheckbox?.addEventListener("change", saveAutoPrefillPreference);

  async function initializeApp() {
    const storedConnectionId = window.localStorage.getItem(connectionStorageKey());
    selectConnection(storedConnectionId ?? bootstrapConfig.defaultConnectionId, { persist: false });
    renderConnectionPicker();
    if (autoPrefillThinkOpenTagCheckbox) {
      autoPrefillThinkOpenTagCheckbox.checked = loadAutoPrefillPreference(state.selectedConnectionId);
    }

    await loadRecentTurns();
    const currentTurn = await loadCurrentTurn();
    if (currentTurn?.status === "running" && currentTurn.turnId) {
      if (currentTurn.connectionId) {
        selectConnection(currentTurn.connectionId, { updateRadio: true });
      }
      if (
        autoPrefillThinkOpenTagCheckbox
        && typeof currentTurn.autoPrefillThinkOpenTag === "boolean"
      ) {
        autoPrefillThinkOpenTagCheckbox.checked = currentTurn.autoPrefillThinkOpenTag;
      }
      await attachToTurn(currentTurn.turnId, "正在恢复生成…");
      return;
    }

    resetLive();
    refreshComposerMode();
    setStreaming(false, "");
  }

  initializeApp().catch((error) => {
    clearActiveTurn();
    resetLive();
    setStreaming(false, error.message || "加载失败");
  });
})();
