const $ = (id) => document.getElementById(id);
const queueInput = $("queue");

function toast(text, isError = false) {
  const t = $("toast");
  t.textContent = text;
  t.className = isError ? "error" : "";
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), isError ? 8000 : 3000);
}

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = res.status === 204 ? null : await res.json().catch(() => null);
  if (!res.ok) throw new Error(data?.error ?? `HTTP ${res.status}`);
  return data;
}

const queueUrl = () => `/api/queues/${encodeURIComponent(queueInput.value.trim())}/messages`;

async function loadMessages() {
  const tbody = $("messages");
  try {
    const msgs = await api("GET", queueUrl());
    $("count").textContent = `(${msgs.length})`;
    tbody.replaceChildren(...msgs.map(renderRow));
    if (!msgs.length) {
      const tr = document.createElement("tr");
      tr.innerHTML = `<td colspan="6" class="muted">Fronta je prázdná.</td>`;
      tbody.append(tr);
    }
  } catch (e) {
    $("count").textContent = "";
    tbody.replaceChildren();
    toast(e.message, true);
  }
}

function renderRow(m) {
  const tr = document.createElement("tr");
  const cell = (text, cls) => {
    const td = document.createElement("td");
    if (cls) td.className = cls;
    td.textContent = text;
    tr.append(td);
    return td;
  };
  cell(new Date(m.putDateTime).toLocaleString("cs-CZ"));
  cell(m.messageId, "mono").title = `CorrelationId: ${m.correlationId}`;
  cell(`${m.format} (${m.characterSet})`);
  cell(m.length);
  const bodyTd = cell("", "body");
  const pre = document.createElement("pre");
  pre.textContent = m.body;
  bodyTd.append(pre);

  const actions = cell("");
  const del = document.createElement("button");
  del.className = "danger small";
  del.textContent = "Smazat";
  del.onclick = async () => {
    if (!confirm("Opravdu smazat tuto zprávu?")) return;
    del.disabled = true;
    try {
      await api("DELETE", `${queueUrl()}/${m.messageId}`);
      toast("Zpráva smazána.");
      loadMessages();
      loadQueues();
    } catch (e) {
      del.disabled = false;
      toast(e.message, true);
    }
  };
  actions.append(del);
  return tr;
}

// ---------- Výběr fronty s vyhledáváním ----------

const queueList = $("queueList");
const showSystem = $("showSystem");
let queues = [];          // seznam front z /api/queues
let queuesError = null;   // chyba při načítání seznamu (např. chybí oprávnění)
let queuesLoaded = false;
let filterText = "";      // text, podle kterého se filtruje
let visible = [];         // aktuálně zobrazené položky
let activeIndex = -1;

const store = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch {} },
};

async function loadQueues() {
  const btn = $("refreshQueues");
  btn.disabled = true;
  try {
    queues = await api("GET", "/api/queues");
    queuesError = null;
  } catch (e) {
    queues = [];
    queuesError = e.message;
  } finally {
    btn.disabled = false;
    queuesLoaded = true;
  }
  if (!queueList.hidden) renderQueueList();
}

function isSystem(name) {
  return name.startsWith("SYSTEM.");
}

function filteredQueues() {
  const f = filterText.trim().toUpperCase();
  return queues.filter((q) =>
    (showSystem.checked || !isSystem(q.name)) &&
    (!f || q.name.toUpperCase().includes(f) || q.description.toUpperCase().includes(f)));
}

// Zvýrazní hledaný text v názvu fronty (bez innerHTML kvůli bezpečnosti)
function highlight(el, text, query) {
  const i = query ? text.toUpperCase().indexOf(query.toUpperCase()) : -1;
  if (i < 0) { el.textContent = text; return; }
  const mark = document.createElement("mark");
  mark.textContent = text.slice(i, i + query.length);
  el.append(text.slice(0, i), mark, text.slice(i + query.length));
}

function infoItem(text) {
  const li = document.createElement("li");
  li.className = "info";
  li.textContent = text;
  return li;
}

function renderQueueList() {
  visible = filteredQueues();
  if (activeIndex >= visible.length) activeIndex = visible.length - 1;

  const items = visible.map((q, i) => {
    const li = document.createElement("li");
    li.role = "option";
    li.id = `q-opt-${i}`;
    if (i === activeIndex) li.className = "active";

    const name = document.createElement("span");
    name.className = "qname";
    highlight(name, q.name, filterText.trim());
    if (q.description) {
      const desc = document.createElement("span");
      desc.className = "qdesc";
      desc.textContent = q.description;
      name.append(desc);
    }

    const badge = document.createElement("span");
    badge.className = `badge ${q.type}`;
    badge.textContent = q.type;

    const depth = document.createElement("span");
    depth.className = "depth" + (q.depth ? " nonzero" : "");
    depth.textContent = q.depth ?? "";
    depth.title = q.depth != null ? `Zpráv ve frontě (max. ${q.maxDepth})` : "";

    li.append(name, badge, depth);
    // mousedown místo click, aby výběr proběhl dřív, než input ztratí fokus
    li.onmousedown = (e) => { e.preventDefault(); selectQueue(q.name); };
    li.onmousemove = () => { if (activeIndex !== i) { activeIndex = i; updateActive(); } };
    return li;
  });

  if (queuesError) items.unshift(infoItem(`Seznam front nelze načíst: ${queuesError}. Název lze zadat ručně.`));
  else if (!visible.length) items.push(infoItem(queuesLoaded ? "Žádná fronta neodpovídá hledání." : "Načítám…"));

  queueList.replaceChildren(...items);
  updateActive();
}

function updateActive() {
  [...queueList.querySelectorAll("li[role=option]")].forEach((li, i) =>
    li.classList.toggle("active", i === activeIndex));
  const active = $(`q-opt-${activeIndex}`);
  active?.scrollIntoView({ block: "nearest" });
  queueInput.setAttribute("aria-activedescendant", active ? active.id : "");
}

function openList() {
  if (!queueList.hidden) return;
  queueList.hidden = false;
  queueInput.setAttribute("aria-expanded", "true");
  renderQueueList();
}

function closeList() {
  queueList.hidden = true;
  queueInput.setAttribute("aria-expanded", "false");
  activeIndex = -1;
}

function selectQueue(name) {
  queueInput.value = name;
  closeList();
  store.set("queue", name);
  loadMessages();
}

// Při otevření zobrazit všechny fronty, filtrovat se začne až psaním.
// Text se označí, takže psaní rovnou nahradí aktuální název.
queueInput.onfocus = () => {
  filterText = "";
  activeIndex = -1;
  queueInput.select();
  openList();
};
queueInput.onclick = () => openList();
queueInput.oninput = () => {
  filterText = queueInput.value;
  activeIndex = filterText.trim() ? 0 : -1;
  openList();
  renderQueueList();
};
queueInput.onblur = () => closeList();
queueInput.onkeydown = (e) => {
  switch (e.key) {
    case "ArrowDown":
      e.preventDefault();
      openList();
      activeIndex = Math.min(activeIndex + 1, visible.length - 1);
      updateActive();
      break;
    case "ArrowUp":
      e.preventDefault();
      activeIndex = Math.max(activeIndex - 1, 0);
      updateActive();
      break;
    case "Enter":
      if (!queueList.hidden && visible[activeIndex]) {
        e.preventDefault();
        selectQueue(visible[activeIndex].name);
      }
      break;
    case "Escape":
      closeList();
      break;
  }
};

showSystem.onchange = () => {
  store.set("showSystem", showSystem.checked ? "1" : "");
  if (!queueList.hidden) renderQueueList();
};

$("refreshQueues").onclick = async () => {
  await loadQueues();
  if (queuesError) toast(`Seznam front nelze načíst: ${queuesError}`, true);
  else toast(`Načteno front: ${queues.length}.`);
};

$("queueForm").onsubmit = (e) => {
  e.preventDefault();
  closeList();
  store.set("queue", queueInput.value.trim());
  loadMessages();
};

$("putForm").onsubmit = async (e) => {
  e.preventDefault();
  try {
    const r = await api("POST", queueUrl(), {
      body: $("body").value,
      correlationId: $("correlId").value.trim() || null,
    });
    toast(`Zpráva odeslána (${r.messageId}).`);
    $("body").value = "";
    loadMessages();
    loadQueues();
  } catch (err) {
    toast(err.message, true);
  }
};

$("clearBtn").onclick = async () => {
  const q = queueInput.value.trim();
  if (!q || !confirm(`Opravdu smazat VŠECHNY zprávy z fronty ${q}?`)) return;
  try {
    const r = await api("DELETE", queueUrl());
    toast(`Smazáno zpráv: ${r.deleted}.`);
    loadMessages();
    loadQueues();
  } catch (e) {
    toast(e.message, true);
  }
};

(async () => {
  showSystem.checked = store.get("showSystem") === "1";
  loadQueues();
  const cfg = await api("GET", "/api/config").catch(() => ({}));
  queueInput.value = store.get("queue") || cfg.defaultQueue || "";
  if (queueInput.value) loadMessages();
})();
