function requireObject(value, label) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object`);
  }
  return value;
}

function requireExactKeys(value, keys, label) {
  const object = requireObject(value, label);
  const actual = Object.keys(object).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new Error(`${label} has unexpected fields`);
  }
  return object;
}

function requireString(value, label) {
  if (typeof value !== "string") {
    throw new Error(`${label} must be a string`);
  }
  return value;
}

function requireNonblankString(value, label) {
  const text = requireString(value, label);
  if (!text.trim()) {
    throw new Error(`${label} must not be blank`);
  }
  return text;
}

function requireNullableString(value, label) {
  return value === null ? null : requireString(value, label);
}

function requireBoolean(value, label) {
  if (typeof value !== "boolean") {
    throw new Error(`${label} must be a boolean`);
  }
  return value;
}

function requireNonnegativeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${label} must be a nonnegative safe integer`);
  }
  return value;
}

function requireNullableObject(value, validator, label) {
  return value === null ? null : validator(value, label);
}

function requireAuthority(value, label) {
  const authority = requireExactKeys(value, [
    "refId", "timelineId", "timelineGeneration", "timelineHeadRowId",
    "controlGeneration", "controlStateDigest", "storeInstanceId",
    "storeSchemaVersion", "recipeDigest", "throughRowId",
    "throughDescriptorDigest",
  ], label);
  requireNonblankString(authority.refId, `${label}.refId`);
  requireNonblankString(authority.timelineId, `${label}.timelineId`);
  requireNonnegativeInteger(authority.timelineGeneration, `${label}.timelineGeneration`);
  requireNullableString(authority.timelineHeadRowId, `${label}.timelineHeadRowId`);
  requireNonnegativeInteger(authority.controlGeneration, `${label}.controlGeneration`);
  requireNonblankString(authority.controlStateDigest, `${label}.controlStateDigest`);
  requireNonblankString(authority.storeInstanceId, `${label}.storeInstanceId`);
  requireNonnegativeInteger(authority.storeSchemaVersion, `${label}.storeSchemaVersion`);
  requireNonblankString(authority.recipeDigest, `${label}.recipeDigest`);
  requireNonblankString(authority.throughRowId, `${label}.throughRowId`);
  requireNonblankString(authority.throughDescriptorDigest, `${label}.throughDescriptorDigest`);
  return authority;
}

function requireReadinessMetrics(value, label) {
  const metrics = requireExactKeys(value, [
    "selectedRows", "recipeRowSteps", "examinedAssignments", "missingAssignments",
  ], label);
  for (const key of Object.keys(metrics)) {
    requireNonnegativeInteger(metrics[key], `${label}.${key}`);
  }
  return metrics;
}

function requireMissingAssignment(value, label) {
  const missing = requireExactKeys(value, [
    "ordinal", "rowId", "recipeDigest", "logicalColumnId", "evaluationKey",
  ], label);
  requireNonnegativeInteger(missing.ordinal, `${label}.ordinal`);
  for (const key of ["rowId", "recipeDigest", "logicalColumnId", "evaluationKey"]) {
    requireNonblankString(missing[key], `${label}.${key}`);
  }
  return missing;
}

function requireReserveBootstrap(value, label) {
  const reserve = requireExactKeys(value, [
    "refId", "timelineId", "timelineGeneration", "timelineHeadRowId",
    "cadenceGeneration", "cadenceDomainDigest", "controlGeneration",
    "controlStateDigest", "storeInstanceId", "storeSchemaVersion",
    "retainedHistoryLoad", "requiredHistoryLoad", "verifiedRows", "metrics",
  ], label);
  for (const key of [
    "refId", "timelineId", "cadenceDomainDigest", "controlStateDigest", "storeInstanceId",
  ]) {
    requireNonblankString(reserve[key], `${label}.${key}`);
  }
  requireNullableString(reserve.timelineHeadRowId, `${label}.timelineHeadRowId`);
  for (const key of [
    "timelineGeneration", "cadenceGeneration", "controlGeneration", "storeSchemaVersion",
    "retainedHistoryLoad", "requiredHistoryLoad", "verifiedRows",
  ]) {
    requireNonnegativeInteger(reserve[key], `${label}.${key}`);
  }
  const metrics = requireExactKeys(reserve.metrics, [
    "examinedTimelineRows", "examinedRawEvents", "examinedHistoryUnits",
    "examinedRenderedUtf8Bytes",
  ], `${label}.metrics`);
  for (const key of Object.keys(metrics)) {
    requireNonnegativeInteger(metrics[key], `${label}.metrics.${key}`);
  }
  return reserve;
}

function requireReadiness(value, label) {
  const readiness = requireExactKeys(value, [
    "freshness", "state", "observedRawHead", "authority", "metrics",
    "orderedMissing", "code", "detail", "reserveBootstrap",
  ], label);
  if (!["exact", "stale"].includes(readiness.freshness)) {
    throw new Error(`${label}.freshness is unknown`);
  }
  const states = [
    "ready", "raw-only", "reserve-bootstrap-raw-only", "frontier",
    "fulfillment-missing", "blocked", "no-rows", "no-active", "invalid",
    "limited", "cancelled", "unavailable", "stale", "busy", "unprovisioned",
  ];
  if (!states.includes(readiness.state)) {
    throw new Error(`${label}.state is unknown`);
  }
  requireNullableString(readiness.observedRawHead, `${label}.observedRawHead`);
  requireNullableObject(readiness.authority, requireAuthority, `${label}.authority`);
  requireNullableObject(readiness.metrics, requireReadinessMetrics, `${label}.metrics`);
  if (readiness.orderedMissing !== null) {
    if (!Array.isArray(readiness.orderedMissing)) {
      throw new Error(`${label}.orderedMissing must be an array or null`);
    }
    readiness.orderedMissing.forEach((item, index) =>
      requireMissingAssignment(item, `${label}.orderedMissing[${index}]`));
  }
  requireNullableString(readiness.code, `${label}.code`);
  requireNullableString(readiness.detail, `${label}.detail`);
  requireNullableObject(
    readiness.reserveBootstrap,
    requireReserveBootstrap,
    `${label}.reserveBootstrap`,
  );
  return readiness;
}

export function requireRecentTurnsResponse(value) {
  const recent = requireExactKeys(value, [
    "turns", "rewindLatestToken", "recapGridReadiness",
  ], "recent turns response");
  if (!Array.isArray(recent.turns)) {
    throw new Error("recent turns response.turns must be an array");
  }
  recent.turns.forEach((value, index) => {
    const turn = requireExactKeys(value, ["userText", "assistant"], `turns[${index}]`);
    requireString(turn.userText, `turns[${index}].userText`);
    const assistant = requireExactKeys(
      turn.assistant,
      ["text", "reasoningText"],
      `turns[${index}].assistant`,
    );
    requireString(assistant.text, `turns[${index}].assistant.text`);
    requireNullableString(assistant.reasoningText, `turns[${index}].assistant.reasoningText`);
  });
  requireNullableString(recent.rewindLatestToken, "recent turns response.rewindLatestToken");
  requireNullableObject(
    recent.recapGridReadiness,
    requireReadiness,
    "recent turns response.recapGridReadiness",
  );
  return recent;
}

export function requireStreamLimits(value) {
  const limits = requireExactKeys(value, [
    "maximumConnectionBytes", "maximumFrameBytes",
  ], "stream limits");
  if (!Number.isSafeInteger(limits.maximumConnectionBytes)
      || limits.maximumConnectionBytes <= 0) {
    throw new Error("stream limits.maximumConnectionBytes must be a positive safe integer");
  }
  if (!Number.isSafeInteger(limits.maximumFrameBytes)
      || limits.maximumFrameBytes <= 0) {
    throw new Error("stream limits.maximumFrameBytes must be a positive safe integer");
  }
  if (limits.maximumFrameBytes > limits.maximumConnectionBytes) {
    throw new Error("stream limits frame bound exceeds connection bound");
  }
  return limits;
}

export class GalateaSseProtocolError extends Error {
  constructor(message) {
    super(message);
    this.name = "GalateaSseProtocolError";
  }
}

export class GalateaSseEofBeforeTerminalError extends Error {
  constructor(message = "SSE ended before its terminal event") {
    super(message);
    this.name = "GalateaSseEofBeforeTerminalError";
  }
}

export class GalateaSseV1Parser {
  constructor(limitsValue) {
    this.limits = requireStreamLimits(limitsValue);
    this.connectionBytes = 0;
    this.frameBytes = new Uint8Array(Math.min(
      4096,
      this.limits.maximumFrameBytes,
    ));
    this.frameLength = 0;
    this.utf8Validator = new TextDecoder("utf-8", { fatal: true });
    this.terminal = null;
    this.finished = false;
  }

  push(chunk) {
    if (!(chunk instanceof Uint8Array)) {
      throw new GalateaSseProtocolError("SSE chunk must be raw bytes");
    }
    if (this.finished) {
      throw new GalateaSseProtocolError("SSE bytes arrived after EOF");
    }
    if (this.terminal !== null && chunk.byteLength !== 0) {
      throw new GalateaSseProtocolError("SSE data followed a terminal event");
    }
    if (chunk.byteLength
        > this.limits.maximumConnectionBytes - this.connectionBytes) {
      throw new GalateaSseProtocolError("SSE connection byte limit exceeded");
    }
    this.connectionBytes += chunk.byteLength;

    const rawFrames = [];
    for (const byte of chunk) {
      if (byte === 0x0d) {
        throw new GalateaSseProtocolError("SSE CR bytes are forbidden");
      }
      this.appendFrameByte(byte);
      if (this.frameLength >= 2
          && this.frameBytes[this.frameLength - 2] === 0x0a
          && this.frameBytes[this.frameLength - 1] === 0x0a) {
        rawFrames.push(this.frameBytes.slice(0, this.frameLength - 2));
        this.frameLength = 0;
      }
    }
    try {
      this.utf8Validator.decode(chunk, { stream: true });
    } catch {
      throw new GalateaSseProtocolError("SSE contains invalid UTF-8");
    }

    const events = [];
    for (let index = 0; index < rawFrames.length; index += 1) {
      if (this.terminal !== null) {
        throw new GalateaSseProtocolError("SSE data followed a terminal event");
      }
      const event = this.parseFrame(rawFrames[index]);
      events.push(event);
      if (event.type === "done" || event.type === "error") {
        this.terminal = event;
        if (index !== rawFrames.length - 1 || this.frameLength !== 0) {
          throw new GalateaSseProtocolError("SSE data followed a terminal event");
        }
      }
    }
    return events;
  }

  finish() {
    if (this.finished) {
      throw new GalateaSseProtocolError("SSE parser was already finished");
    }
    this.finished = true;
    try {
      this.utf8Validator.decode();
    } catch {
      throw new GalateaSseProtocolError("SSE ended inside a UTF-8 sequence");
    }
    if (this.frameLength !== 0 || this.terminal === null) {
      throw new GalateaSseEofBeforeTerminalError();
    }
    return this.terminal;
  }

  appendFrameByte(byte) {
    if (this.frameLength === this.limits.maximumFrameBytes) {
      throw new GalateaSseProtocolError("SSE frame byte limit exceeded");
    }
    if (this.frameLength === this.frameBytes.length) {
      const nextLength = Math.min(
        this.limits.maximumFrameBytes,
        Math.max(1, this.frameBytes.length * 2),
      );
      const grown = new Uint8Array(nextLength);
      grown.set(this.frameBytes);
      this.frameBytes = grown;
    }
    this.frameBytes[this.frameLength++] = byte;
  }

  parseFrame(rawBytes) {
    let raw;
    try {
      raw = new TextDecoder("utf-8", { fatal: true }).decode(rawBytes);
    } catch {
      throw new GalateaSseProtocolError("SSE frame contains invalid UTF-8");
    }
    const lines = raw.split("\n");
    if (lines.length !== 2
        || !lines[0].startsWith("event: ")
        || !lines[1].startsWith("data: ")) {
      throw new GalateaSseProtocolError(
        "SSE frame must contain exact event and data lines",
      );
    }
    const eventName = lines[0].slice("event: ".length);
    const dataText = lines[1].slice("data: ".length);
    if (!eventName || !dataText
        || !dataText.startsWith("{") || !dataText.endsWith("}")) {
      throw new GalateaSseProtocolError("SSE event or data line is not exact");
    }
    let payload;
    try {
      payload = JSON.parse(dataText);
      return requireSseEvent(eventName, payload);
    } catch (error) {
      if (error instanceof GalateaSseProtocolError) {
        throw error;
      }
      throw new GalateaSseProtocolError(
        `SSE ${eventName} payload is invalid: ${error?.message ?? "invalid JSON"}`,
      );
    }
  }
}

function requireSseEvent(eventName, value) {
  switch (eventName) {
    case "status": {
      const object = requireObject(value, "SSE status");
      const code = requireString(object.code, "SSE status.code");
      if (code === "input-normalization-finished") {
        const payload = requireExactKeys(
          object,
          ["code", "changed"],
          "SSE status",
        );
        requireBoolean(payload.changed, "SSE status.changed");
        return { type: "status", code, changed: payload.changed };
      }
      if (!["generating", "normalizing-input", "using-tools"].includes(code)) {
        throw new Error("SSE status.code is unknown");
      }
      requireExactKeys(object, ["code"], "SSE status");
      return { type: "status", code };
    }
    case "reasoning-delta":
    case "text-delta": {
      const payload = requireExactKeys(value, ["delta"], `SSE ${eventName}`);
      const delta = requireString(payload.delta, `SSE ${eventName}.delta`);
      if (!delta) {
        throw new Error(`SSE ${eventName}.delta must be nonempty`);
      }
      return { type: eventName, delta };
    }
    case "done": {
      const payload = requireExactKeys(value, ["recent"], "SSE done");
      const recent = payload.recent === null
        ? null
        : requireRecentTurnsResponse(payload.recent);
      return { type: "done", recent };
    }
    case "error": {
      const payload = requireExactKeys(value, ["code", "message"], "SSE error");
      const codes = [
        "operator-stop", "server-shutdown", "completion-failed",
        "turn-unavailable", "internal-failure",
      ];
      if (!codes.includes(payload.code)) {
        throw new Error("SSE error.code is unknown");
      }
      requireNonblankString(payload.message, "SSE error.message");
      return { type: "error", code: payload.code, message: payload.message };
    }
    default:
      throw new Error("SSE event name is unknown");
  }
}

export function capturePopProvisional(recentValue) {
  const recent = requireRecentTurnsResponse(recentValue);
  return Object.freeze({
    submittedToken: requireNonblankString(
      recent.rewindLatestToken,
      "pop provisional.rewindLatestToken",
    ),
    poppedUserText: recent.turns[0]?.userText ?? "",
  });
}

export function stagePopProvisional(recentValue) {
  const provisional = capturePopProvisional(recentValue);
  return Object.freeze({
    provisional,
    rewindLatestToken: null,
    pendingPoppedDraftText: provisional.poppedUserText,
    inputValue: provisional.poppedUserText,
  });
}

export function reconcilePopProvisional(provisionalValue, recentValue) {
  const provisional = requireExactKeys(
    provisionalValue,
    ["submittedToken", "poppedUserText"],
    "pop provisional",
  );
  requireNonblankString(provisional.submittedToken, "pop provisional.submittedToken");
  requireString(provisional.poppedUserText, "pop provisional.poppedUserText");
  const recent = requireRecentTurnsResponse(recentValue);
  return recent.rewindLatestToken !== provisional.submittedToken
    ? provisional.poppedUserText
    : null;
}

export function canContinueWithoutInitialRecent(
  currentValue,
  errorCode,
) {
  const current = requireCurrentTurn(currentValue);
  return errorCode === "recent-view-busy"
    && current.status === "running";
}

export async function loadInitialSessionState(
  loadCurrent,
  loadRecent,
) {
  let current = await loadCurrent();
  try {
    await loadRecent();
  } catch (error) {
    if (error?.code !== "recent-view-busy") {
      throw error;
    }
    current = await loadCurrent();
    if (!canContinueWithoutInitialRecent(current, error.code)) {
      throw error;
    }
  }
  return current;
}

function requireApiError(value) {
  const error = requireExactKeys(value, ["code", "error"], "API error");
  requireNonblankString(error.code, "API error.code");
  requireNonblankString(error.error, "API error.error");
  return error;
}

function requireBusyError(value) {
  const error = requireExactKeys(value, ["code", "error", "turnId"], "busy error");
  if (error.code !== "turn-busy") {
    throw new Error("busy error.code is unknown");
  }
  requireNonblankString(error.error, "busy error.error");
  if (error.turnId !== null && !/^[0-9a-f]{32}$/.test(error.turnId)) {
    throw new Error("busy error.turnId is invalid");
  }
  return error;
}

function requireAcceptedTurn(value) {
  const accepted = requireExactKeys(value, ["turnId"], "accepted turn");
  if (!/^[0-9a-f]{32}$/.test(accepted.turnId)) {
    throw new Error("accepted turn.turnId is invalid");
  }
  return accepted;
}

function requirePopReceipt(value) {
  const receipt = requireExactKeys(value, ["poppedUserText"], "pop receipt");
  requireString(receipt.poppedUserText, "pop receipt.poppedUserText");
  return receipt;
}

function requireCurrentTurn(value) {
  const current = requireExactKeys(value, [
    "status", "turnId", "connectionId", "restartRequired", "recoveryHead",
  ], "current turn");
  requireNullableString(current.turnId, "current turn.turnId");
  requireNullableString(current.connectionId, "current turn.connectionId");
  requireBoolean(current.restartRequired, "current turn.restartRequired");
  requireNullableString(current.recoveryHead, "current turn.recoveryHead");
  if (current.status === "running") {
    const published = current.turnId !== null || current.connectionId !== null;
    if (published && (!/^[0-9a-f]{32}$/.test(current.turnId ?? "") || !current.connectionId)) {
      throw new Error("running current turn is only partially published");
    }
    if (current.restartRequired || current.recoveryHead !== null) {
      throw new Error("running current turn carries recovery state");
    }
  } else if (current.status === "recovery-required") {
    if (current.turnId !== null || current.connectionId !== null || !current.recoveryHead) {
      throw new Error("recovery current turn has an invalid state matrix");
    }
  } else if (["idle", "unprovisioned"].includes(current.status)) {
    if (current.turnId !== null || current.connectionId !== null
        || current.restartRequired || current.recoveryHead !== null) {
      throw new Error("terminal current turn has an invalid state matrix");
    }
  } else {
    throw new Error("current turn.status is unknown");
  }
  return current;
}

async function readJsonResponse(response, validator) {
  const contentType = response.headers.get("content-type") ?? "";
  if (!/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(contentType)) {
    throw new Error("API response has an invalid Content-Type");
  }
  const text = await response.text();
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    throw new Error("API response is not valid JSON");
  }
  return validator(value);
}

function startGalateaApp() {
  const bootstrapConfig = window.galateaBootstrap ?? {};
  const connections = Array.isArray(bootstrapConfig.connections) ? bootstrapConfig.connections : [];
  const userKey = bootstrapConfig.userId ?? "anonymous";
  const maintenanceMode = bootstrapConfig.maintenanceMode === true;
  const streamLimits = requireStreamLimits(bootstrapConfig.streamLimits);

  const state = {
    recentTurns: [],
    rewindLatestToken: null,
    pendingPoppedDraftText: null,
    liveText: "",
    liveReasoning: "",
    streaming: false,
    stopRequested: false,
    activeTurnId: null,
    streamGeneration: 0,
    selectedConnectionId: null,
    recapGridReadiness: null,
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

  function connectionStorageKey() {
    return ["galatea", "connection", userKey].join(":");
  }

  const turnList = document.getElementById("turn-list");
  const form = document.getElementById("chat-form");
  const input = document.getElementById("message-input");
  const sendButton = document.getElementById("send-button");
  const undoLastButton = document.getElementById("undo-last-button");
  const stopButton = document.getElementById("stop-button");
  const connectionPicker = document.getElementById("connection-picker");
  const composerModeHint = document.getElementById("composer-mode-hint");
  const statusText = document.getElementById("status-text");
  const recapPlanningStatus = document.getElementById("recap-planning-status");
  const recapPlanningSummary = document.getElementById("recap-planning-summary");
  const recapPlanningProgress = document.getElementById("recap-planning-progress");
  const recapPlanningDetail = document.getElementById("recap-planning-detail");
  const liveTurn = document.getElementById("live-turn");
  const liveText = document.getElementById("live-text");
  const liveReasoning = document.getElementById("live-reasoning");
  const liveReasoningPanel = document.getElementById("live-reasoning-panel");
  const scrollToTop = document.getElementById("scroll-to-top");

  scrollToTop?.addEventListener("click", () => {
    input.scrollIntoView({ behavior: "smooth", block: "start" });
    input.focus();
  });

  async function fetchJson(url, validator, options) {
    const response = await fetch(url, {
      credentials: "same-origin",
      ...options,
    });
    if (!response.ok) {
      const error = await readJsonResponse(response, requireApiError);
      const failure = new Error(error.error);
      failure.code = error.code;
      failure.status = response.status;
      throw failure;
    }
    return await readJsonResponse(response, validator);
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
    const reasoningText = turn.assistant.reasoningText ?? "";
    const reasoning = reasoningText.length > 0
      ? `<details class="reasoning-panel"><summary>Reasoning</summary><pre>${escapeHtml(reasoningText)}</pre></details>`
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
    sendButton.disabled = maintenanceMode || streaming;
    input.disabled = maintenanceMode || streaming;
    if (stopButton) {
      stopButton.disabled = maintenanceMode || !streaming;
    }
    if (connectionPicker) {
      connectionPicker.querySelectorAll('input[name="connection"]').forEach((radio) => {
        radio.disabled = maintenanceMode || streaming;
      });
    }
    statusText.textContent = status || "";
    refreshComposerMode();
  }

  function refreshComposerMode() {
    if (composerModeHint) {
      if (state.pendingPoppedDraftText !== null) {
        composerModeHint.textContent = "已撤销一轮。可继续撤销更早轮次，或修改后重新发送。";
        composerModeHint.classList.remove("hidden");
      } else {
        composerModeHint.textContent = "";
        composerModeHint.classList.add("hidden");
      }
    }

    if (undoLastButton) {
      undoLastButton.disabled = maintenanceMode || state.streaming || !hasUndoableTurn();
    }
  }

  function hasUndoableTurn() {
    return Boolean(state.rewindLatestToken);
  }

  function clearPendingPoppedTurn() {
    state.pendingPoppedDraftText = null;
    refreshComposerMode();
  }

  function confirmPendingPoppedTurnReplacement() {
    if (state.pendingPoppedDraftText === null) {
      return true;
    }

    if (input.value === state.pendingPoppedDraftText) {
      return true;
    }

    return window.confirm("继续撤销会覆盖输入框中尚未发送的修改，是否继续？");
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
            <span class="connection-name">${escapeHtml(connection.id)}</span>
            <span class="connection-model">${escapeHtml(connection.modelId ?? "")}</span>
          </label>
        `;
      })
      .join("");
    connectionPicker.innerHTML = legend + options;

    connectionPicker.querySelectorAll('input[name="connection"]').forEach((radio) => {
      radio.disabled = maintenanceMode || state.streaming;
      radio.addEventListener("change", () => {
        if (radio.checked) {
          selectConnection(radio.value, { persist: true });
        }
      });
    });
  }

  function selectConnection(connectionId, options = {}) {
    const { persist = false, updateRadio = false } = options;
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

  }

  async function loadRecentTurns() {
    const recent = await fetchJson(
      "/api/v1/recent-turns",
      requireRecentTurnsResponse,
    );
    applyRecentTurnsPayload(recent);
    renderTurns();
    return recent;
  }

  function applyRecentTurnsPayload(payload) {
    const recent = requireRecentTurnsResponse(payload);
    state.recentTurns = recent.turns;
    state.rewindLatestToken = recent.rewindLatestToken;
    state.recapGridReadiness = recent.recapGridReadiness;
    renderRecapGridReadiness();
  }

  const recapGridCountFormatter = new Intl.NumberFormat("zh-CN");

  function renderRecapGridReadiness() {
    if (!recapPlanningStatus || !recapPlanningSummary || !recapPlanningDetail) {
      return;
    }

    const snapshot = state.recapGridReadiness;
    if (!snapshot) {
      recapPlanningStatus.classList.add("hidden");
      return;
    }

    recapPlanningStatus.classList.remove("hidden");
    recapPlanningSummary.textContent = "";
    recapPlanningDetail.textContent = "";
    recapPlanningProgress?.classList.add("hidden");

    const metrics = snapshot.metrics;
    if (metrics) {
      recapPlanningSummary.textContent = `RecapGrid：${recapGridCountFormatter.format(metrics.selectedRows)} rows · ${recapGridCountFormatter.format(metrics.recipeRowSteps)} recipe steps · ${recapGridCountFormatter.format(metrics.missingAssignments)} missing`;
    }

    switch (snapshot.state) {
      case "ready":
        recapPlanningSummary.textContent ||= "RecapGrid 已就绪";
        recapPlanningDetail.textContent = "当前 active recipe 与 Timeline head 已有精确 fulfilled view。";
        break;
      case "raw-only":
      case "no-active":
      case "no-rows":
        recapPlanningSummary.textContent = "RecapGrid raw-only";
        recapPlanningDetail.textContent = "当前没有可组合的 active recap；请求仍可使用 raw history。";
        break;
      case "reserve-bootstrap-raw-only": {
        const reserve = snapshot.reserveBootstrap;
        recapPlanningSummary.textContent = "RecapGrid reserve bootstrap";
        recapPlanningDetail.textContent = reserve
          ? `当前保留 HistoryLoad ${recapGridCountFormatter.format(reserve.retainedHistoryLoad)} / ${recapGridCountFormatter.format(reserve.requiredHistoryLoad)}；本次请求仅使用 raw history。`
          : "当前 recent reserve 尚不足；本次请求仅使用 raw history。";
        break;
      }
      case "frontier":
        recapPlanningSummary.textContent ||= "RecapGrid 存在待构建 frontier";
        recapPlanningDetail.textContent = `${snapshot.orderedMissing?.length ?? 0} 个有界 assignment 等待构建。`;
        break;
      case "fulfillment-missing":
        recapPlanningSummary.textContent ||= "RecapGrid view 已完成但 fulfillment 尚未发布";
        recapPlanningDetail.textContent = "下一次 lifecycle 可在精确 authority 下补齐 fulfillment。";
        break;
      case "blocked":
      case "limited":
        recapPlanningSummary.textContent ||= "RecapGrid 构建受限";
        recapPlanningDetail.textContent = snapshot.code
          ? `${snapshot.detail || "需要operator处理。"}（${snapshot.code}）`
          : (snapshot.detail || "需要operator处理。");
        break;
      case "unprovisioned":
      case "unavailable":
      case "busy":
      case "cancelled":
        recapPlanningSummary.textContent ||= "RecapGrid readiness 暂时不可用";
        recapPlanningDetail.textContent = snapshot.code
          ? `${snapshot.detail || "请稍后重试。"}（${snapshot.code}）`
          : (snapshot.detail || "请稍后重试。");
        break;
      case "invalid":
        recapPlanningSummary.textContent = "RecapGrid authority 无效";
        recapPlanningDetail.textContent = snapshot.code
          ? `${snapshot.detail || "请运行只读 verify。"}（${snapshot.code}）`
          : (snapshot.detail || "请运行只读 verify。");
        break;
      case "stale":
        recapPlanningSummary.textContent = "RecapGrid readiness 已过期";
        recapPlanningDetail.textContent = snapshot.detail || "请刷新后重试。";
        break;
      default:
        recapPlanningSummary.textContent = "RecapGrid readiness 状态未知";
        recapPlanningDetail.textContent = "请刷新页面后重试。";
        break;
    }

    if (snapshot.freshness === "stale" && snapshot.state !== "stale") {
      recapPlanningDetail.textContent += " 当前authority尚未重新确认。";
    }
  }

  async function loadCurrentTurn() {
    return await fetchJson(
      "/api/v1/chat/turns/current",
      requireCurrentTurn,
    );
  }

  async function waitForPublishedCurrentTurn(currentTurn) {
    while (currentTurn?.status === "running" && !currentTurn.turnId) {
      await sleep(100);
      currentTurn = await loadCurrentTurn();
    }
    return currentTurn;
  }

  async function waitForCurrentTurnTerminal() {
    while (true) {
      const currentTurn = await loadCurrentTurn();
      if (currentTurn?.status !== "running") {
        return currentTurn;
      }

      await sleep(250);
    }
  }

  function sleep(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms));
  }

  async function readEventStream(response) {
    const reader = response.body.getReader();
    const parser = new GalateaSseV1Parser(streamLimits);

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }
      for (const streamEvent of parser.push(value)) {
        handleEvent(streamEvent);
      }
    }
    return parser.finish();
  }

  function handleEvent(streamEvent) {
    switch (streamEvent.type) {
      case "status":
        if (streamEvent.code === "generating") {
          setStreaming(true, "正在生成…");
        } else if (streamEvent.code === "normalizing-input") {
          setStreaming(true, "正在清洗输入…");
        } else if (streamEvent.code === "input-normalization-finished") {
          if (streamEvent.changed) {
            setStreaming(true, "已纠正输入，继续生成…");
          } else {
            setStreaming(true, "输入清洗完成，继续生成…");
          }
        } else if (streamEvent.code === "using-tools") {
          setStreaming(true, "正在调用工具…");
        }
        break;
      case "reasoning-delta":
        state.liveReasoning += streamEvent.delta;
        liveReasoning.textContent = state.liveReasoning;
        liveReasoningPanel.classList.toggle("hidden", state.liveReasoning.length === 0);
        break;
      case "text-delta":
        state.liveText += streamEvent.delta;
        liveText.textContent = state.liveText;
        break;
      case "done":
        if (streamEvent.recent !== null) {
          applyRecentTurnsPayload(streamEvent.recent);
          clearPendingPoppedTurn();
          renderTurns();
        }
        resetLive();
        setStreaming(true, "正在收尾…");
        input.value = "";
        return;
      case "error":
        resetLive();
        setStreaming(false, streamEvent.message);
        return;
    }
  }

  async function popLatestTurn(status) {
    if (!confirmPendingPoppedTurnReplacement()) {
      return null;
    }

    const stagedPop = stagePopProvisional({
      turns: state.recentTurns,
      rewindLatestToken: state.rewindLatestToken,
      recapGridReadiness: state.recapGridReadiness,
    });
    const provisional = stagedPop.provisional;
    const composerBeforePop = {
      inputValue: input.value,
      pendingPoppedDraftText: state.pendingPoppedDraftText,
      recapGridReadiness: state.recapGridReadiness,
    };
    // Keep a coherent draft locally before the POST can become ambiguous.
    // The submitted token is retained only inside `provisional`; UI state is
    // invalidated now so no response-loss path can submit it again.
    state.rewindLatestToken = stagedPop.rewindLatestToken;
    state.recapGridReadiness = null;
    input.value = stagedPop.inputValue;
    state.pendingPoppedDraftText = stagedPop.pendingPoppedDraftText;
    refreshComposerMode();
    setStreaming(true, status || "正在取出最近一轮…");

    let response;
    try {
      response = await fetch("/api/v1/chat/turns/pop-latest", {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          rewindLatestToken: provisional.submittedToken,
        }),
      });
    } catch (error) {
      await reconcileAmbiguousPop(
        provisional,
        composerBeforePop,
        error,
      );
      return null;
    }

    if (!response.ok) {
      let error;
      try {
        error = await readJsonResponse(response, (value) =>
          value?.code === "turn-busy" ? requireBusyError(value) : requireApiError(value));
      } catch (parseError) {
        await reconcileAmbiguousPop(
          provisional,
          composerBeforePop,
          parseError,
        );
        return null;
      }
      input.value = composerBeforePop.inputValue;
      state.pendingPoppedDraftText =
        composerBeforePop.pendingPoppedDraftText;
      state.recapGridReadiness = composerBeforePop.recapGridReadiness;
      if (error.code === "turn-busy" && error.turnId) {
        refreshComposerMode();
        await attachToTurn(error.turnId, error.error);
        return null;
      }
      state.rewindLatestToken = provisional.submittedToken;
      await loadRecentTurns().catch(() => {});
      refreshComposerMode();
      setStreaming(false, error.error);
      return null;
    }

    // A 200 means the CAS may already be durable. Invalidate the stale token
    // before the first response-body await so no UI path can submit it again.
    let receipt;
    try {
      receipt = await readJsonResponse(response, requirePopReceipt);
    } catch (error) {
      await reconcileAmbiguousPop(
        provisional,
        composerBeforePop,
        error,
      );
      return null;
    }
    input.value = receipt.poppedUserText;
    state.pendingPoppedDraftText = receipt.poppedUserText;
    refreshComposerMode();
    try {
      await loadRecentTurns();
      setStreaming(false, "");
    } catch (error) {
      setStreaming(false, `撤销已生效；recent view 暂不可用：${error?.message || "加载失败"}`);
    }
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
    return receipt;
  }

  async function reconcileAmbiguousPop(
    provisional,
    composerBeforePop,
    cause,
  ) {
    try {
      const recent = await loadRecentTurns();
      const restoredDraft = reconcilePopProvisional(
        provisional,
        recent,
      );
      if (restoredDraft !== null) {
        input.value = restoredDraft;
        state.pendingPoppedDraftText = restoredDraft;
        refreshComposerMode();
        setStreaming(false, "撤销已生效；响应不完整。未重复提交。");
      } else {
        input.value = composerBeforePop.inputValue;
        state.pendingPoppedDraftText =
          composerBeforePop.pendingPoppedDraftText;
        refreshComposerMode();
        setStreaming(false, "撤销结果未改变；未重复提交。");
      }
    } catch {
      setStreaming(
        false,
        cause?.message || "撤销结果未知；未重复提交。请稍后刷新。",
      );
    }
  }

  async function attachToTurn(turnId, status) {
    const normalizedTurnId = turnId ?? "";
    if (!normalizedTurnId) {
      return;
    }

    state.activeTurnId = normalizedTurnId;
    const generation = ++state.streamGeneration;
    let reconciliationFailures = 0;

    while (state.activeTurnId === normalizedTurnId && generation === state.streamGeneration) {
      beginLive();
      setStreaming(true, status || "正在连接生成流…");

      try {
        const response = await fetch(`/api/v1/chat/turns/${encodeURIComponent(normalizedTurnId)}/events`, {
          credentials: "same-origin",
        });

        if (response.status === 404) {
          throw new GalateaSseEofBeforeTerminalError(
            "SSE turn was not found",
          );
        }

        if (!response.ok || !response.body) {
          throw new GalateaSseEofBeforeTerminalError(
            "SSE transport is unavailable",
          );
        }

        const terminalEvent = await readEventStream(response);
        const currentTurn = await waitForCurrentTurnTerminal();
        clearActiveTurn();
        resetLive();
        let recentUnavailable = false;
        if (terminalEvent.type === "error" || terminalEvent.recent === null) {
          try {
            await loadRecentTurns();
          } catch {
            recentUnavailable = true;
          }
        }
        if (currentTurn?.status === "recovery-required") {
          setStreaming(false, currentTurn.restartRequired
            ? "上次模型调用结果不确定；需要明确授权后才能恢复。"
            : "本轮保留在可恢复状态；刷新页面可继续恢复。");
        } else if (currentTurn?.status === "unprovisioned") {
          setStreaming(false, "会话仓库尚未完成初始化。");
        } else if (terminalEvent.type === "error") {
          setStreaming(false, terminalEvent.message);
        } else if (recentUnavailable) {
          setStreaming(false, "生成已完成；recent view 暂不可用，请稍后刷新。");
        } else {
          setStreaming(false, "");
        }
        return;
      } catch (error) {
        if (state.activeTurnId !== normalizedTurnId || generation !== state.streamGeneration) {
          return;
        }
        if (error instanceof GalateaSseProtocolError) {
          setStreaming(
            true,
            "生成流协议无效；已停止自动重连，请刷新页面。",
          );
          return;
        }
        let currentTurn;
        try {
          currentTurn = await loadCurrentTurn();
          reconciliationFailures = 0;
        } catch {
          reconciliationFailures += 1;
          if (reconciliationFailures >= 3) {
            setStreaming(
              true,
              "无法确认生成状态；已停止自动重连，请刷新页面。",
            );
            return;
          }
          setStreaming(true, "连接已断开，正在确认生成状态…");
          await sleep(800);
          continue;
        }
        if (currentTurn?.status === "running"
            && (currentTurn.turnId === normalizedTurnId
                || currentTurn.turnId === null)) {
          setStreaming(true, "连接已断开，正在从头重连…");
        } else {
          clearActiveTurn();
          resetLive();
          await loadRecentTurns().catch(() => {});
          if (currentTurn?.status === "recovery-required") {
            setStreaming(false, currentTurn.restartRequired
              ? "生成中断且结果不确定；需要明确授权后才能恢复。"
              : "生成中断；本轮保留在可恢复状态。");
          } else if (currentTurn?.status === "unprovisioned") {
            setStreaming(false, "会话仓库尚未完成初始化。");
          } else {
            setStreaming(false, "生成流在terminal前中断；已刷新持久化视图。");
          }
          return;
        }
      }

      await sleep(800);
      if (state.activeTurnId !== normalizedTurnId || generation !== state.streamGeneration) {
        return;
      }
    }
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (maintenanceMode || state.streaming) {
      return;
    }

    const message = input.value.trim();
    if (!message) {
      return;
    }

    const replacingPoppedTurn = state.pendingPoppedDraftText !== null;
    state.stopRequested = false;
    setStreaming(true, replacingPoppedTurn ? "正在重新生成…" : "正在发送…");

    const response = await fetch("/api/v1/chat/turns", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        message,
        connectionId: state.selectedConnectionId,
      }),
    });

    if (!response.ok) {
      const error = await readJsonResponse(response, (value) =>
        value?.code === "turn-busy" ? requireBusyError(value) : requireApiError(value));
      if (error.code === "turn-busy" && error.turnId) {
        await attachToTurn(error.turnId, error.error);
        return;
      }

      setStreaming(false, error.error);
      return;
    }

    const payload = await readJsonResponse(response, requireAcceptedTurn);
    if (replacingPoppedTurn) {
      await attachToTurn(payload.turnId, "正在重新生成…");
      return;
    }

    await attachToTurn(payload.turnId, "正在生成…");
  });

  undoLastButton?.addEventListener("click", async () => {
    if (maintenanceMode || state.streaming || !hasUndoableTurn()) {
      return;
    }

    await popLatestTurn("正在撤销最近一轮…");
  });

  stopButton?.addEventListener("click", async () => {
    if (maintenanceMode || !state.streaming || !state.activeTurnId) {
      return;
    }

    state.stopRequested = true;
    setStreaming(true, "正在停止生成…");

    const response = await fetch(`/api/v1/chat/turns/${encodeURIComponent(state.activeTurnId)}/stop`, {
      method: "POST",
      credentials: "same-origin",
    });

    if (!response.ok) {
      const error = await readJsonResponse(response, requireApiError);
      state.stopRequested = false;
      setStreaming(true, error.error);
      return;
    }
    if (response.status !== 204 || (await response.text()) !== "") {
      throw new Error("stop response must be an empty 204");
    }

    setStreaming(true, "已发送停止请求，等待模型收尾…");
  });

  async function initializeApp() {
    const storedConnectionId = window.localStorage.getItem(connectionStorageKey());
    selectConnection(storedConnectionId ?? bootstrapConfig.defaultConnectionId, { persist: false });
    renderConnectionPicker();

    let currentTurn = await loadInitialSessionState(
      loadCurrentTurn,
      loadRecentTurns,
    );
    if (maintenanceMode) {
      resetLive();
      refreshComposerMode();
      setStreaming(false, "维护模式：会话只读。");
      return;
    }
    currentTurn = await waitForPublishedCurrentTurn(currentTurn);
    if (currentTurn?.status === "running" && currentTurn.turnId) {
      if (currentTurn.connectionId) {
        selectConnection(currentTurn.connectionId, { updateRadio: true });
      }
      await attachToTurn(currentTurn.turnId, "正在恢复生成…");
      return;
    }
    if (currentTurn?.status === "recovery-required") {
      const restartUncertainCompletion = currentTurn.restartRequired
        ? window.confirm(
          "上次模型调用的结果不确定。重新调用可能产生重复请求；是否明确授权重新调用？"
        )
        : false;
      if (currentTurn.restartRequired && !restartUncertainCompletion) {
        resetLive();
        refreshComposerMode();
        setStreaming(false, "已保留不确定状态，未重新调用模型。");
        return;
      }
      const response = await fetch("/api/v1/chat/turns/resume", {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          expectedHead: currentTurn.recoveryHead,
          connectionId: state.selectedConnectionId,
          restartUncertainCompletion,
        }),
      });
      if (response.ok) {
        const payload = await readJsonResponse(response, requireAcceptedTurn);
        await attachToTurn(payload.turnId, "正在恢复生成…");
        return;
      }
      const error = await readJsonResponse(response, (value) =>
        value?.code === "turn-busy" ? requireBusyError(value) : requireApiError(value));
      if (error.code === "turn-busy" && error.turnId) {
        await attachToTurn(error.turnId, error.error);
        return;
      }
      resetLive();
      refreshComposerMode();
      setStreaming(false, error.error);
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
}

if (typeof window !== "undefined" && typeof document !== "undefined") {
  startGalateaApp();
}
