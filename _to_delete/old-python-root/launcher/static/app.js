/* Padra Platform Launcher — control panel logic.

   Strings come from /api/locales/<culture>.json; numbers, sizes and dates are
   formatted by Intl for the active culture, which covers Persian digits and
   the Jalali calendar. No user-facing text is written in this file. */

const STATUS_POLL_MS = 2500;
const LOG_POLL_MS = 1500;
const NOTICE_MS = 6000;
const KIB = 1024;

const state = {
  apiKey: "",
  culture: "en-US",
  cultures: [],
  strings: {},
  direction: "ltr",
  models: [],
  backends: [],
  network: null,
  local: { models: [], totalBytes: 0, roots: [] },
  catalog: { repos: [], repo: null },
  clients: null,
  settings: {},
  logCursor: 0,
  logEntries: [],
  online: false,
  tab: "local",
  networkDirty: false,
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => Array.from(document.querySelectorAll(selector));

/* ----------------------------------------------------------- localization */
function t(key, vars) {
  let text = state.strings[key];
  if (text === undefined) return key;
  if (vars) {
    for (const [name, value] of Object.entries(vars)) {
      text = text.split(`{${name}}`).join(String(value));
    }
  }
  return text;
}

function formatInt(value) {
  return new Intl.NumberFormat(state.culture).format(Number(value) || 0);
}

function formatBytes(bytes) {
  const value = Number(bytes) || 0;
  const units = ["unit.byte", "unit.kib", "unit.mib", "unit.gib", "unit.tib"];
  let index = 0;
  let scaled = value;
  while (scaled >= KIB && index < units.length - 1) {
    scaled /= KIB;
    index += 1;
  }
  const formatted = new Intl.NumberFormat(state.culture, {
    maximumFractionDigits: index === 0 ? 0 : 1,
  }).format(scaled);
  return t(units[index], { value: formatted });
}

function formatTime(date) {
  return new Intl.DateTimeFormat(state.culture, {
    hour: "2-digit", minute: "2-digit", second: "2-digit",
  }).format(date);
}

function formatDateTime(date) {
  return new Intl.DateTimeFormat(state.culture, {
    dateStyle: "medium", timeStyle: "medium",
  }).format(date);
}

function listSeparator() {
  return t("format.list_separator");
}

function applyStrings() {
  document.documentElement.lang = state.culture;
  document.documentElement.dir = state.direction;
  document.title = t("app.title");

  $$("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
  $$("[data-i18n-placeholder]").forEach((el) => {
    el.placeholder = t(el.dataset.i18nPlaceholder);
  });
  $$("[data-i18n-aria-label]").forEach((el) => {
    el.setAttribute("aria-label", t(el.dataset.i18nAriaLabel));
  });
}

async function loadCulture(code) {
  const data = await request(`/api/locales/${encodeURIComponent(code)}`);
  const meta = data._meta || {};
  state.culture = meta.code || code;
  state.direction = meta.dir === "rtl" ? "rtl" : "ltr";
  state.strings = {};
  for (const [key, value] of Object.entries(data)) {
    if (key !== "_meta") state.strings[key] = value;
  }
  applyStrings();
  renderAll();
}

/* ------------------------------------------------------------------ http */
function messageFrom(payload, fallback) {
  const detail = payload && payload.detail !== undefined ? payload.detail : payload;
  if (typeof detail === "string") return detail;
  if (detail && typeof detail === "object") {
    const error = detail.error;
    if (typeof error === "string") return error;
    if (error && typeof error.message === "string") {
      if (error.message === "folder") return t("error.folder", { path: error.path || "" });
      if (error.message === "missing") return t(`dialog.missing.${error.agent}`);
      return error.message;
    }
    if (typeof detail.message === "string") return detail.message;
  }
  return fallback;
}

async function request(path, options = {}) {
  const headers = Object.assign({}, options.headers || {});
  if (state.apiKey) headers.Authorization = `Bearer ${state.apiKey}`;
  if (options.body) headers["Content-Type"] = "application/json";
  const response = await fetch(path, Object.assign({}, options, { headers }));
  if (!response.ok) {
    let payload = null;
    try { payload = await response.json(); } catch (_) { payload = null; }
    const error = new Error(messageFrom(payload, `HTTP ${response.status}`));
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  const type = response.headers.get("content-type") || "";
  return type.includes("json") ? response.json() : response.text();
}

/* ------------------------------------------------------------- messaging */
let noticeTimer = 0;

function showError(message) {
  const box = $("#global-error");
  box.textContent = message || "";
  box.hidden = !message;
  if (message) box.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function showNotice(message) {
  const box = $("#global-notice");
  box.textContent = message || "";
  box.hidden = !message;
  window.clearTimeout(noticeTimer);
  if (message) noticeTimer = window.setTimeout(() => { box.hidden = true; }, NOTICE_MS);
}

function pushLocalLog(text) {
  state.logEntries.push({ seq: -1, at: Date.now() / 1000, text, local: true });
  state.logEntries = state.logEntries.slice(-400);
  renderLog();
}

async function withBusy(button, task, { refresh = true } = {}) {
  if (button) { button.classList.add("is-busy"); button.disabled = true; }
  try {
    const result = await task();
    showError("");
    return result;
  } catch (error) {
    showError(t("error.request", { error: error.message }));
    return null;
  } finally {
    if (button) { button.classList.remove("is-busy"); button.disabled = false; }
    if (refresh) await pollStatus();
  }
}

/* ------------------------------------------------------------ components */
function cell(text, className) {
  const td = document.createElement("td");
  if (className) td.className = className;
  td.textContent = text;
  return td;
}

function actionButton(key, className, handler, disabled) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.textContent = t(key);
  button.disabled = Boolean(disabled);
  button.addEventListener("click", () => handler(button));
  return button;
}

function placeholderRow(tbody, columns, key) {
  const row = document.createElement("tr");
  row.className = "placeholder-row";
  const td = document.createElement("td");
  td.colSpan = columns;
  td.textContent = t(key);
  row.append(td);
  tbody.append(row);
}

function badgeFor(backend) {
  if (backend && backend.external) return { key: "state.attached", cls: "badge badge-attached" };
  if (backend && backend.running) return { key: "state.loaded", cls: "badge badge-loaded" };
  if (backend && backend.last_error) return { key: "state.failed", cls: "badge badge-failed" };
  return { key: "state.idle", cls: "badge" };
}

/* --------------------------------------------------------------- rendering */
function renderAll() {
  renderStatus();
  renderFleet();
  renderLocal();
  renderCatalog();
  renderNetwork();
  renderClients();
  renderLaunchModels();
  renderLog();
}

function renderStatus() {
  const pill = $("#status-pill");
  const label = $("#status-text");
  const summary = $("#footer-summary");

  if (!state.online) {
    pill.className = "pill pill-idle";
    label.textContent = t("status.starting");
    summary.textContent = t("footer.never");
    return;
  }

  const loaded = state.backends.filter((b) => b.running);
  pill.className = "pill pill-ok";
  label.textContent = loaded.length
    ? t("status.running_with", { models: loaded.map((b) => b.alias).join(listSeparator()) })
    : t("status.running_idle");

  summary.textContent = `${t("footer.models_loaded", {
    count: formatInt(loaded.length),
    total: formatInt(state.backends.length),
  })} · ${t("footer.updated", { timestamp: formatTime(new Date()) })}`;
}

function renderFleet() {
  const tbody = $("#fleet-rows");
  tbody.textContent = "";

  if (!state.models.length) {
    placeholderRow(tbody, 7, "empty.fleet");
    return;
  }

  const byAlias = new Map(state.backends.map((b) => [b.alias, b]));
  for (const model of state.models) {
    const backend = byAlias.get(model.alias);
    const badge = badgeFor(backend);
    const row = document.createElement("tr");

    const activeCell = document.createElement("td");
    const radio = document.createElement("input");
    radio.type = "radio";
    radio.name = "active-model";
    radio.value = model.alias;
    radio.checked = Boolean(model.isDefault);
    radio.setAttribute("aria-label", t("aria.activate", { alias: model.alias }));
    radio.addEventListener("change", () => {
      if (radio.checked) activateModel(model.alias, radio);
    });
    activeCell.append(radio);

    const status = document.createElement("td");
    const span = document.createElement("span");
    span.className = badge.cls;
    span.textContent = t(badge.key);
    if (backend && backend.last_error) span.title = backend.last_error;
    status.append(span);

    const actions = document.createElement("td");
    const group = document.createElement("div");
    group.className = "row-actions";
    group.append(
      actionButton("btn.load", "btn", (btn) => loadModel(model.alias, btn),
                   backend && backend.running),
      actionButton("btn.unload", "btn", (btn) => unloadModel(model.alias, btn),
                   !(backend && backend.running)),
      actionButton("btn.remove", "btn btn-danger",
                   (btn) => removeModel(model.alias, btn)),
    );
    actions.append(group);

    row.append(
      activeCell,
      cell(model.alias),
      cell(model.quant ? `${model.repo}:${model.quant}` : model.repo, "model"),
      cell(model.contextLength ? formatInt(model.contextLength) : t("value.unknown"), "num"),
      cell(formatInt(model.port), "num"),
      status,
      actions,
    );
    tbody.append(row);
  }
}

function renderLocal() {
  const tbody = $("#local-rows");
  const summary = $("#local-summary");
  tbody.textContent = "";

  summary.textContent = state.local.models.length
    ? t("summary.local", {
        count: formatInt(state.local.models.length),
        size: formatBytes(state.local.totalBytes),
      })
    : t("summary.local_empty");

  if (!state.local.models.length) {
    placeholderRow(tbody, 6, "empty.local");
    return;
  }

  for (const model of state.local.models) {
    const row = document.createElement("tr");
    const actions = document.createElement("td");
    actions.append(actionButton(
      model.inFleet ? "btn.in_fleet" : "btn.add_to_fleet",
      model.inFleet ? "btn" : "btn btn-primary",
      (btn) => addModel(model, btn),
      model.inFleet,
    ));
    row.append(
      cell(model.repo, "model"),
      cell(model.quant || t("value.none")),
      cell(formatBytes(model.sizeBytes), "num"),
      cell(model.contextLength ? formatInt(model.contextLength) : t("value.unknown"), "num"),
      cell(t(`source.${model.source}`)),
      actions,
    );
    tbody.append(row);
  }
}

function renderCatalog() {
  const tbody = $("#catalog-rows");
  tbody.textContent = "";

  if (!state.catalog.repos.length) {
    placeholderRow(tbody, 4, "empty.catalog");
  } else {
    for (const repo of state.catalog.repos) {
      const row = document.createElement("tr");
      const status = document.createElement("td");
      const span = document.createElement("span");
      span.className = repo.hasLocal ? "badge badge-loaded" : "badge";
      span.textContent = t(repo.hasLocal ? "state.downloaded" : "state.remote");
      status.append(span);

      const actions = document.createElement("td");
      actions.append(actionButton("btn.variants", "btn",
                                  (btn) => openVariants(repo.repo, btn)));

      row.append(
        cell(repo.repo, "model"),
        cell(formatInt(repo.downloads), "num"),
        status,
        actions,
      );
      tbody.append(row);
    }
  }

  const wrap = $("#variant-wrap");
  const variantBody = $("#variant-rows");
  variantBody.textContent = "";
  const record = state.catalog.repo;
  if (!record) {
    wrap.hidden = true;
    return;
  }
  wrap.hidden = false;
  $("#variant-title").textContent = t("title.variants", { repo: record.repo });

  for (const variant of record.variants) {
    const row = document.createElement("tr");
    const status = document.createElement("td");
    const span = document.createElement("span");
    span.className = variant.downloaded ? "badge badge-loaded" : "badge";
    span.textContent = t(variant.downloaded ? "state.downloaded" : "state.remote");
    status.append(span);

    const actions = document.createElement("td");
    const inFleet = state.models.some(
      (m) => m.repo.toLowerCase() === record.repo.toLowerCase() &&
             (m.quant || "").toLowerCase() === (variant.quant || "").toLowerCase());
    actions.append(actionButton(
      inFleet ? "btn.in_fleet" : "btn.add_to_fleet",
      inFleet ? "btn" : "btn btn-primary",
      (btn) => addModel({
        repo: record.repo,
        quant: variant.quant,
        contextLength: variant.contextLength,
      }, btn),
      inFleet,
    ));

    row.append(
      cell(variant.quant || t("value.none")),
      cell(formatBytes(variant.sizeBytes), "num"),
      cell(variant.contextLength ? formatInt(variant.contextLength) : t("value.unknown"), "num"),
      status,
      actions,
    );
    variantBody.append(row);
  }
}

function renderNetwork() {
  const list = $("#lan-urls");
  list.textContent = "";
  const network = state.network;
  if (!network) return;

  if (!state.networkDirty) {
    $("#allow-lan").checked = Boolean(network.shared || network.allowLan);
    $("#gateway-port").value = network.port;
  }
  $("#firewall-hint").textContent = network.shared
    ? t("hint.firewall", { command: network.firewallCommand })
    : t("hint.local_only");

  const entries = [{ url: network.localUrl, label: t("label.local_url") }];
  for (const item of network.lan) {
    entries.push({ url: item.url, label: item.address });
  }
  for (const entry of entries) {
    const li = document.createElement("li");
    const label = document.createElement("span");
    label.className = "muted";
    label.textContent = entry.label;
    const code = document.createElement("code");
    code.textContent = entry.url;
    const copy = actionButton("btn.copy", "btn btn-ghost",
                              () => copyText(entry.url, "log.copied_url"));
    li.append(label, code, copy);
    list.append(li);
  }
}

function renderClients() {
  const box = $("#client-availability");
  if (!state.clients) { box.textContent = ""; return; }
  const missing = state.clients.agents.filter((a) => !a.installed).map((a) => t(`client.${a.id}`));
  const unsloth = state.clients.unsloth.installed;
  const parts = [];
  if (!unsloth) parts.push(t("warn.unsloth_missing"));
  if (missing.length) parts.push(t("warn.agents_missing", { agents: missing.join(listSeparator()) }));
  box.textContent = parts.join(" · ");
  box.className = parts.length ? "warn" : "muted";
}

function renderLaunchModels() {
  const select = $("#launch-model");
  const previous = select.value || state.settings.selectedAlias || "";
  select.textContent = "";
  if (!state.models.length) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = t("empty.fleet_short");
    select.append(option);
    select.disabled = true;
    return;
  }
  select.disabled = false;
  for (const model of state.models) {
    const option = document.createElement("option");
    option.value = model.alias;
    option.textContent = model.isDefault
      ? t("option.model_default", { alias: model.alias })
      : model.alias;
    select.append(option);
  }
  const fallback = (state.models.find((m) => m.isDefault) || state.models[0]).alias;
  select.value = state.models.some((m) => m.alias === previous) ? previous : fallback;
}

function renderLog() {
  const list = $("#log");
  const atBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 40;
  list.textContent = "";

  if (!state.logEntries.length) {
    const empty = document.createElement("li");
    empty.className = "muted localized";
    empty.textContent = t("empty.log");
    list.append(empty);
    return;
  }

  for (const entry of state.logEntries) {
    const item = document.createElement("li");
    if (entry.local) item.className = "localized";
    item.textContent = entry.text;
    item.title = formatDateTime(new Date(entry.at * 1000));
    list.append(item);
  }
  if (atBottom) list.scrollTop = list.scrollHeight;
}

/* ----------------------------------------------------------------- actions */
function loadModel(alias, button) {
  return withBusy(button, async () => {
    pushLocalLog(t("log.loading_model", { alias }));
    await request(`/api/models/${encodeURIComponent(alias)}/load`, { method: "POST" });
    pushLocalLog(t("log.model_ok", { alias }));
  });
}

function unloadModel(alias, button) {
  return withBusy(button, async () => {
    pushLocalLog(t("log.unloading_model", { alias }));
    await request(`/api/models/${encodeURIComponent(alias)}/unload`, { method: "POST" });
  });
}

function activateModel(alias, control) {
  return withBusy(control, async () => {
    pushLocalLog(t("log.activating", { alias }));
    await request(`/api/models/${encodeURIComponent(alias)}/activate`, { method: "POST" });
    showNotice(t("notice.activated", { alias }));
  });
}

async function removeModel(alias, button) {
  if (!window.confirm(t("confirm.remove_model", { alias }))) return;
  await withBusy(button, async () => {
    const result = await request(`/api/models/${encodeURIComponent(alias)}`,
                                 { method: "DELETE" });
    state.models = result.models;
    pushLocalLog(t("log.model_removed", { alias }));
    await refreshLocal(false);
  });
}

async function addModel(model, button) {
  await withBusy(button, async () => {
    const result = await request("/api/models", {
      method: "POST",
      body: JSON.stringify({
        repo: model.repo,
        quant: model.quant || "",
        contextLength: model.contextLength || null,
      }),
    });
    state.models = result.models;
    showNotice(t("notice.model_added", { alias: result.model.alias }));
    await refreshLocal(false);
    if (state.catalog.repo) await openVariants(state.catalog.repo.repo, null);
  });
}

async function refreshLocal(refresh) {
  const data = await request(`/api/models/local${refresh ? "?refresh=true" : ""}`);
  state.local = {
    models: data.models,
    totalBytes: data.totalBytes,
    roots: data.roots,
  };
  renderLocal();
}

async function searchCatalog(button) {
  const term = $("#catalog-search").value.trim();
  await withBusy(button, async () => {
    const data = await request(
      `/api/models/catalog?search=${encodeURIComponent(term)}`);
    state.catalog.repos = data.repos;
    state.catalog.repo = null;
    renderCatalog();
  }, { refresh: false });
}

async function openVariants(repo, button) {
  await withBusy(button, async () => {
    const data = await request(`/api/models/catalog?repo=${encodeURIComponent(repo)}`);
    state.catalog.repo = data.repo;
    renderCatalog();
    $("#variant-wrap").scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, { refresh: false });
}

async function copyText(text, messageKey) {
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
  } catch (_) {
    const helper = document.createElement("textarea");
    helper.value = text;
    helper.setAttribute("readonly", "readonly");
    helper.className = "sr-only";
    document.body.append(helper);
    helper.select();
    document.execCommand("copy");
    helper.remove();
  }
  pushLocalLog(t(messageKey));
}

function selectedTargets() {
  return $$(".targets input[type=checkbox]").filter((el) => el.checked)
    .map((el) => el.value);
}

async function installConfigs(button) {
  const targets = selectedTargets();
  if (!targets.length) {
    showError(t("error.no_targets"));
    return;
  }
  const scope = $("#install-scope").value;
  if (!window.confirm(t("dialog.install.body"))) return;
  await withBusy(button, async () => {
    const result = await request("/api/install-configs", {
      method: "POST",
      body: JSON.stringify({ targets, scope }),
    });
    if (result.ok) {
      showNotice(t("notice.install_ok", {
        count: formatInt(result.written.length),
        url: result.baseUrl,
      }));
    } else {
      showError(t("error.install_failed", { targets: result.failed.join(listSeparator()) }));
    }
  }, { refresh: false });
}

async function downloadBundle(button) {
  const scope = $("#install-scope").value;
  await withBusy(button, async () => {
    const headers = state.apiKey ? { Authorization: `Bearer ${state.apiKey}` } : {};
    const response = await fetch(`/api/client-bundle?scope=${encodeURIComponent(scope)}`,
                                 { headers });
    if (!response.ok) {
      let payload = null;
      try { payload = await response.json(); } catch (_) { payload = null; }
      throw new Error(messageFrom(payload, `HTTP ${response.status}`));
    }
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = "padra-client.zip";
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
    pushLocalLog(t("log.bundle_saved"));
  }, { refresh: false });
}

async function launchAgent(agent, button) {
  const folder = $("#project-folder").value.trim();
  const alias = $("#launch-model").value;
  if (!folder) {
    showError(t("error.folder", { path: t("value.none") }));
    $("#project-folder").focus();
    return;
  }
  if (!alias) {
    showError(t("error.no_model"));
    return;
  }
  await withBusy(button, async () => {
    const result = await request("/api/launch-agent", {
      method: "POST",
      body: JSON.stringify({ agent, alias, folder }),
    });
    if (result.ok) {
      pushLocalLog(t("log.launched", { agent, alias, folder }));
    } else {
      showError(t("log.no_terminal", { command: result.command }));
    }
  }, { refresh: false });
}

async function applyNetwork(button) {
  const allowLan = $("#allow-lan").checked;
  const port = Number($("#gateway-port").value) || 0;
  const idleTimeout = Number($("#idle-timeout").value) || 0;
  await withBusy(button, async () => {
    const result = await request("/api/network", {
      method: "POST",
      body: JSON.stringify({ allowLan, port: port || null, idleTimeout }),
    });
    state.networkDirty = false;
    state.network = result.network;
    renderNetwork();
    showNotice(result.restartRequired ? t("notice.restart_required") : t("notice.saved"));
  }, { refresh: false });
}

async function openFirewall(button) {
  await withBusy(button, async () => {
    const result = await request("/api/network/firewall", { method: "POST" });
    if (result.ok) {
      showNotice(t("notice.firewall_added"));
    } else {
      showError(t("error.firewall", { command: result.command }));
    }
  }, { refresh: false });
}

/* ----------------------------------------------------------------- polling */
async function pollStatus() {
  try {
    const data = await request("/api/status");
    state.backends = data.backends;
    state.models = data.models;
    state.network = data.network;
    state.online = true;
    $("#mode").value = data.mode;
  } catch (_) {
    state.online = false;
  }
  renderStatus();
  renderFleet();
  renderNetwork();
  renderLaunchModels();
}

async function pollLog() {
  try {
    const data = await request(`/api/log?since=${state.logCursor}`);
    if (data.entries.length) {
      state.logCursor = data.cursor;
      state.logEntries.push(...data.entries);
      state.logEntries = state.logEntries.slice(-400);
      renderLog();
    }
  } catch (_) { /* the gateway is starting; the next tick retries */ }
}

function selectTab(name) {
  state.tab = name;
  for (const tab of ["local", "catalog"]) {
    const active = tab === name;
    $(`#tab-${tab}`).setAttribute("aria-selected", String(active));
    $(`#tab-${tab}`).setAttribute("tabindex", active ? "0" : "-1");
    $(`#tab-${tab}`).classList.toggle("is-active", active);
    $(`#pane-${tab}`).hidden = !active;
  }
  if (name === "catalog" && !state.catalog.repos.length) searchCatalog($("#catalog-go"));
}

/* -------------------------------------------------------------------- init */
async function init() {
  const boot = await request("/api/bootstrap");
  state.apiKey = boot.apiKey;
  state.cultures = boot.cultures;
  state.models = boot.models;
  state.network = boot.network;
  state.settings = boot.settings || {};

  const select = $("#culture");
  for (const culture of boot.cultures) {
    const option = document.createElement("option");
    option.value = culture.code;
    option.textContent = culture.name;
    select.append(option);
  }
  select.value = state.settings.culture || boot.cultures[0]?.code || "en-US";
  $("#mode").value = boot.mode;
  $("#endpoint-url").textContent = `${location.origin}/v1`;
  $("#project-folder").value = state.settings.projectFolder || "";
  $("#idle-timeout").value = boot.idleTimeout || 0;

  const chosen = new Set(state.settings.installTargets || boot.installTargets);
  $$(".targets input[type=checkbox]").forEach((el) => { el.checked = chosen.has(el.value); });

  await loadCulture(select.value);

  select.addEventListener("change", async () => {
    await request("/api/settings", {
      method: "POST",
      body: JSON.stringify({ culture: select.value }),
    });
    await loadCulture(select.value);
    pushLocalLog(t("log.language_changed", {
      language: state.cultures.find((c) => c.code === state.culture)?.name || state.culture,
    }));
  });

  $("#mode").addEventListener("change", async (event) => {
    const mode = event.target.value;
    await withBusy(null, async () => {
      await request("/api/settings", { method: "POST", body: JSON.stringify({ mode }) });
      pushLocalLog(t("log.mode_changed", { mode: t(`mode.${mode}`) }));
    });
  });

  $("#copy-url").addEventListener("click", () => copyText(`${location.origin}/v1`, "log.copied_url"));
  $("#copy-key").addEventListener("click", () => copyText(state.apiKey, "log.copied_key"));
  $("#refresh-status").addEventListener("click", (e) => withBusy(e.currentTarget, pollStatus));
  $("#rescan").addEventListener("click", (e) =>
    withBusy(e.currentTarget, () => refreshLocal(true), { refresh: false }));
  const tabs = ["local", "catalog"];
  for (const name of tabs) {
    const tab = $(`#tab-${name}`);
    tab.addEventListener("click", () => selectTab(name));
    tab.addEventListener("keydown", (event) => {
      const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
      if (!step) return;
      event.preventDefault();
      const forward = state.direction === "rtl" ? -step : step;
      const next = tabs[(tabs.indexOf(name) + forward + tabs.length) % tabs.length];
      selectTab(next);
      $(`#tab-${next}`).focus();
    });
  }
  $("#catalog-go").addEventListener("click", (e) => searchCatalog(e.currentTarget));
  $("#catalog-search").addEventListener("keydown", (event) => {
    if (event.key === "Enter") { event.preventDefault(); searchCatalog($("#catalog-go")); }
  });
  $("#install-configs").addEventListener("click", (e) => installConfigs(e.currentTarget));
  $("#download-bundle").addEventListener("click", (e) => downloadBundle(e.currentTarget));
  $("#open-codex").addEventListener("click", (e) => launchAgent("codex", e.currentTarget));
  $("#open-qwen").addEventListener("click", (e) => launchAgent("qwen", e.currentTarget));
  $("#open-claude").addEventListener("click", (e) => launchAgent("claude", e.currentTarget));
  for (const id of ["#allow-lan", "#gateway-port", "#idle-timeout"]) {
    $(id).addEventListener("input", () => { state.networkDirty = true; });
  }
  $("#apply-network").addEventListener("click", (e) => applyNetwork(e.currentTarget));
  $("#open-firewall").addEventListener("click", (e) => openFirewall(e.currentTarget));
  $("#clear-log").addEventListener("click", () => { state.logEntries = []; renderLog(); });
  $("#project-folder").addEventListener("change", (event) => {
    request("/api/settings", {
      method: "POST",
      body: JSON.stringify({ projectFolder: event.target.value.trim() }),
    }).catch(() => { /* saving a preference must never interrupt the user */ });
  });
  $$(".targets input[type=checkbox]").forEach((el) => {
    el.addEventListener("change", () => {
      request("/api/settings", {
        method: "POST",
        body: JSON.stringify({ installTargets: selectedTargets() }),
      }).catch(() => { /* preference only */ });
    });
  });

  await pollStatus();
  await pollLog();
  request("/api/clients")
    .then((data) => { state.clients = data; renderClients(); })
    .catch(() => { /* availability is advisory */ });
  refreshLocal(false).catch((error) => showError(
    t("error.request", { error: error.message })));

  window.setInterval(pollStatus, STATUS_POLL_MS);
  window.setInterval(pollLog, LOG_POLL_MS);
}

init().catch((error) => {
  const box = document.createElement("p");
  box.className = "banner";
  box.setAttribute("role", "alert");
  box.textContent = error.message;
  document.body.prepend(box);
});
