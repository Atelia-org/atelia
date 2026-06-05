(function () {
  const state = {
    recentTurns: [],
    liveText: "",
    liveReasoning: "",
    streaming: false,
    activeTurnId: null,
    streamGeneration: 0,
  };

  const turnList = document.getElementById("turn-list");
  const form = document.getElementById("chat-form");
  const input = document.getElementById("message-input");
  const sendButton = document.getElementById("send-button");
  const regenerateButton = document.getElementById("regenerate-button");
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
    refreshRegenerateButton();
  }

  function renderTurn(turn) {
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
    statusText.textContent = status || "";
    refreshRegenerateButton();
  }

  function refreshRegenerateButton() {
    if (!regenerateButton) {
      return;
    }

    regenerateButton.disabled = state.streaming || state.recentTurns.length === 0;
  }

  function removeLatestTurnFromView() {
    if (state.recentTurns.length === 0) {
      return;
    }

    state.recentTurns = state.recentTurns.slice(1);
    renderTurns();
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
    state.streamGeneration += 1;
  }

  async function loadRecentTurns() {
    state.recentTurns = await fetchJson("/api/recent-turns");
    renderTurns();
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
        state.recentTurns = payload?.recentTurns ?? [];
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

    setStreaming(true, "正在发送…");

    const response = await fetch("/api/chat/turns", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ message }),
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

    await attachToTurn(payload?.turnId, "正在生成…");
  });

  regenerateButton?.addEventListener("click", async () => {
    if (state.streaming || state.recentTurns.length === 0) {
      return;
    }

    setStreaming(true, "正在准备重新生成…");

    const response = await fetch("/api/chat/turns/regenerate-latest", {
      method: "POST",
      credentials: "same-origin",
    });

    const payload = await response.json().catch(() => null);

    if (response.status === 409) {
      if (payload?.turnId) {
        await attachToTurn(payload.turnId, payload.error || "正在恢复生成…");
        return;
      }

      await loadRecentTurns().catch(() => {});
      setStreaming(false, payload?.error || "当前没有可重新生成的最近回复");
      return;
    }

    if (!response.ok) {
      setStreaming(false, payload?.error || "请求失败");
      return;
    }

    removeLatestTurnFromView();
    await attachToTurn(payload?.turnId, "正在重新生成…");
  });

  async function bootstrap() {
    await loadRecentTurns();
    const currentTurn = await loadCurrentTurn();
    if (currentTurn?.status === "running" && currentTurn.turnId) {
      await attachToTurn(currentTurn.turnId, "正在恢复生成…");
      return;
    }

    resetLive();
    setStreaming(false, "");
  }

  bootstrap().catch((error) => {
    clearActiveTurn();
    resetLive();
    setStreaming(false, error.message || "加载失败");
  });
})();
