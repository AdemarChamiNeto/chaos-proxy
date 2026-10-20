// Demonstração do Chaos Proxy: escolhe o cenário (só para esta sessão) e compara dois clientes.
"use strict";

const TOTAL = 20;
const PARALELO = 4;

const $ = (sel) => document.querySelector(sel);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- cenário ----------
async function carregarCenarios() {
  const [lista, atual] = await Promise.all([
    fetch("/_chaos/scenarios").then((r) => r.json()),
    fetch("/_chaos/session").then((r) => r.json()),
  ]);
  const box = $("#cenarios");
  for (const c of lista) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "cenario";
    b.setAttribute("role", "radio");
    b.dataset.nome = c.name;
    b.setAttribute("aria-checked", String(c.name === (atual.scenario ?? "normal")));
    b.innerHTML = "<strong></strong><span></span>";
    b.querySelector("strong").textContent = c.title;
    b.querySelector("span").textContent = c.description;
    b.addEventListener("click", () => escolher(c));
    box.append(b);
  }
}

async function escolher(c) {
  const status = $("#status-cenario");
  const r = await fetch("/_chaos/session", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ scenario: c.name }),
  });
  if (!r.ok) {
    status.textContent = r.status === 429 ? "Calma: muitas trocas de cenário. Espere um minuto." : "Não foi possível trocar o cenário.";
    return;
  }
  for (const b of document.querySelectorAll(".cenario")) b.setAttribute("aria-checked", String(b.dataset.nome === c.name));
  status.textContent = `Cenário "${c.title}" ativo para você.`;
}

// ---------- clientes ----------
const descrever = (res, corpoOk) => {
  if (res.status === 200 && corpoOk) return null;
  if (res.status === 200) return "HTML no lugar de JSON";
  return `HTTP ${res.status}`;
};

async function lerJson(res) {
  const tipo = res.headers.get("content-type") ?? "";
  if (!tipo.includes("application/json")) return false;
  try { await res.json(); return true; } catch { return false; }
}

/** Uma tentativa, sem timeout próprio (só o teto de 15 s para a demo não ficar presa). */
async function ingenuo(url) {
  const inicio = performance.now();
  try {
    const res = await fetch(url, { signal: AbortSignal.timeout(15_000) });
    // aceita qualquer resposta: se veio HTML com 502, "funcionou" do ponto de vista dele até o JSON.parse quebrar
    const corpoOk = await lerJson(res);
    return { erro: descrever(res, corpoOk), ms: performance.now() - inicio, tentativas: 1, falha: res.headers.get("x-chaos-fault") };
  } catch (e) {
    return { erro: e.name === "TimeoutError" ? "travou (15 s)" : "conexão caiu", ms: performance.now() - inicio, tentativas: 1, falha: null };
  }
}

// circuito compartilhado entre as requisições do cliente resiliente
const circuito = { falhasSeguidas: 0, abertoAte: 0 };

/** Timeout de 3 s, até 2 novas tentativas com espera crescente, Retry-After e circuit breaker. */
async function resiliente(url) {
  const inicio = performance.now();
  let tentativas = 0;
  let ultima = { erro: "?", falha: null };
  for (let i = 0; i < 3; i++) {
    if (Date.now() < circuito.abertoAte) {
      return { erro: "circuito aberto (nem tentou)", ms: performance.now() - inicio, tentativas, falha: null };
    }
    tentativas++;
    try {
      const res = await fetch(url, { signal: AbortSignal.timeout(3_000) });
      const corpoOk = await lerJson(res);
      const erro = descrever(res, corpoOk);
      ultima = { erro, falha: res.headers.get("x-chaos-fault") };
      if (!erro) {
        circuito.falhasSeguidas = 0;
        return { erro: null, ms: performance.now() - inicio, tentativas, falha: ultima.falha };
      }
      if (res.status === 429) {
        const espera = Number(res.headers.get("retry-after") ?? "1");
        if (espera > 5 || i === 2) break; // esperar demais é pior do que avisar o usuário
        await sleep(espera * 1000);
        continue;
      }
      if (res.status < 500 && res.status !== 200) break; // 4xx é culpa de quem chamou: repetir não adianta
    } catch (e) {
      ultima = { erro: e.name === "TimeoutError" ? "timeout (3 s)" : "conexão caiu", falha: null };
    }
    if (++circuito.falhasSeguidas >= 5) circuito.abertoAte = Date.now() + 10_000;
    if (i < 2) await sleep(300 * 2 ** i + Math.random() * 200);
  }
  return { erro: ultima.erro, ms: performance.now() - inicio, tentativas, falha: ultima.falha };
}

// ---------- disparo ----------
function percentil(valores, p) {
  const v = [...valores].sort((a, b) => a - b);
  return v[Math.min(v.length - 1, Math.ceil((p / 100) * v.length) - 1)] ?? 0;
}

function linha(i, r) {
  const tr = document.createElement("tr");
  const celulas = [
    String(i + 1),
    r.erro ? r.erro : "200 OK",
    `${Math.round(r.ms)} ms`,
    String(r.tentativas),
    r.falha ?? "—",
  ];
  for (const [k, texto] of celulas.entries()) {
    const td = document.createElement("td");
    td.textContent = texto;
    if (k === 1) td.className = r.erro ? "erro" : "ok";
    tr.append(td);
  }
  return tr;
}

function resumir(resultados, totalMs) {
  const ok = resultados.filter((r) => !r.erro).length;
  const tempos = resultados.map((r) => r.ms);
  const dados = [
    [`${ok}/${resultados.length}`, "deram certo"],
    [`${Math.round(percentil(tempos, 50))} ms`, "mediana (p50)"],
    [`${Math.round(percentil(tempos, 95))} ms`, "p95"],
    [String(resultados.reduce((s, r) => s + r.tentativas, 0)), "tentativas no total"],
    [`${(totalMs / 1000).toFixed(1)} s`, "tempo total"],
  ];
  const box = $("#resumo");
  box.replaceChildren(...dados.map(([v, t]) => {
    const d = document.createElement("div");
    const b = document.createElement("b");
    b.textContent = v;
    const s = document.createElement("span");
    s.textContent = t;
    d.append(b, s);
    return d;
  }));
}

async function disparar() {
  const botao = $("#disparar");
  const tipo = document.querySelector("input[name=cliente]:checked").value;
  const cliente = tipo === "resiliente" ? resiliente : ingenuo;
  circuito.falhasSeguidas = 0;
  circuito.abertoAte = 0;

  botao.disabled = true;
  const corpo = $("#tabela tbody");
  corpo.replaceChildren();
  $("#tabela").hidden = false;
  $("#resumo").textContent = "Rodando…";

  const resultados = new Array(TOTAL);
  const inicio = performance.now();
  let proxima = 0;
  async function trabalhador() {
    while (proxima < TOTAL) {
      const i = proxima++;
      resultados[i] = await cliente(`/api/tickets?page=${(i % 5) + 1}`);
    }
  }
  await Promise.all(Array.from({ length: PARALELO }, trabalhador));

  resultados.forEach((r, i) => corpo.append(linha(i, r)));
  resumir(resultados, performance.now() - inicio);
  botao.disabled = false;
}

carregarCenarios();
$("#disparar").addEventListener("click", disparar);
