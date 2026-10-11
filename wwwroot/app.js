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
    } catch (e) {
      del.disabled = false;
      toast(e.message, true);
    }
  };
  actions.append(del);
  return tr;
}

$("queueForm").onsubmit = (e) => {
  e.preventDefault();
  localStorage.setItem("queue", queueInput.value.trim());
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
  } catch (e) {
    toast(e.message, true);
  }
};

(async () => {
  let saved = null;
  try { saved = localStorage.getItem("queue"); } catch {}
  const cfg = await api("GET", "/api/config").catch(() => ({}));
  queueInput.value = saved || cfg.defaultQueue || "";
  if (queueInput.value) loadMessages();
})();
