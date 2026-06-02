(function () {
  const state = {
    recentTurns: [],
    liveText: "",
    liveReasoning: "",
    streaming: false,
  };

  const turnList = document.getElementById("turn-list");
  const form = document.getElementById("chat-form");
  const input = document.getElementById("message-input");
  const sendButton = document.getElementById("send-button");
  const statusText = document.getElementById("status-text");
  const liveTurn = document.getElementById("live-turn");
  const liveText = document.getElementById("live-text");
  const liveReasoning = document.getElementById("live-reasoning");
  const liveReasoningPanel = document.getElementById("live-reasoning-panel");

  async function fetchJson(url) {
    const response = await fetch(url, { credentials: "same-origin" });
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

  async function loadRecentTurns() {
    state.recentTurns = await fetchJson("/api/recent-turns");
    renderTurns();
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

  function handleEvent(eventName, payload) {
    switch (eventName) {
      case "meta":
        if (payload?.phase === "turn-start") {
          beginLive();
          setStreaming(true, "正在生成…");
        } else if (payload?.phase === "compaction-start") {
          setStreaming(true, "正在压缩上下文…");
        } else if (payload?.phase === "compaction-finish") {
          setStreaming(true, "上下文压缩完成，继续生成…");
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
        resetLive();
        setStreaming(false, "");
        input.value = "";
        input.focus();
        break;
      case "error":
        setStreaming(false, payload?.message ?? "请求失败");
        break;
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

    const response = await fetch("/api/chat/stream", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ message }),
    });

    if (response.status === 409) {
      const payload = await response.json();
      setStreaming(false, payload.error || "该账号当前正在生成，请稍后。");
      return;
    }

    if (!response.ok || !response.body) {
      setStreaming(false, "请求失败");
      return;
    }

    await readEventStream(response);
  });

  loadRecentTurns().catch((error) => {
    setStreaming(false, error.message || "加载失败");
  });
})();
