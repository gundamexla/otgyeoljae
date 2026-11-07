// 옷결재 (OBA.RUN 2nd, SKT 트랙) - 의존성 없는 Node 서버
// 판정은 규칙 엔진(judge), LLM(SKT A.X K2)은 의도 해석과 문장 작성만 담당한다.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
loadEnv(path.join(ROOT, '.env'));
const PORT = Number(process.env.PORT || 4400);
const TODAY = '2026-11-07 (토)';

const read = (f) => JSON.parse(fs.readFileSync(path.join(ROOT, 'data', f), 'utf8'));
const calendar = read('calendar.json');
const closet = read('closet.json');
const outfits = read('outfits.json');
const catalog = read('products.json');

// ---- 상태 (데모용 메모리 저장) ----
const state = {
  rules: {
    approvalAmount: 100000, // 주문 총액이 이 금액 이상이면 사람 결재
    nonRefundableNeedsApproval: true,
    duplicateSuggestSkip: true,
    categoryMode: { 아우터: 'auto', 상의: 'auto', 하의: 'auto', 신발: 'auto', 잡화: 'auto' },
  },
  budget: { monthly: 300000, spent: 50000 },
  quotes: {},
  receipts: [],
  log: [],
  ruleHistory: [],
  idem: new Set(),
};
const won = (n) => n.toLocaleString('ko-KR') + '원';
const log = (type, text) => state.log.unshift({ at: new Date().toISOString(), type, text });

// ---- LLM: SKT A.X K2 (OpenAI 호환 Chat Completions), 없으면 OpenAI 폴백 ----
async function llm(messages, json = false) {
  const tries = [];
  if (process.env.OBA_API_KEY) tries.push({ name: 'SKT A.X K2', url: 'https://agent-os-biz.adot.ai/oba/v1/chat/completions', key: process.env.OBA_API_KEY, model: process.env.OBA_MODEL || 'skt/a.ll/A.X-K2-ADOT' });
  if (process.env.OPENAI_API_KEY) tries.push({ name: 'OpenAI', url: 'https://api.openai.com/v1/chat/completions', key: process.env.OPENAI_API_KEY, model: process.env.OPENAI_MODEL || 'gpt-4o-mini' });
  for (const t of tries) {
    try {
      const body = { model: t.model, messages, temperature: 0.4 };
      if (json) body.response_format = { type: 'json_object' };
      const r = await fetch(t.url, { method: 'POST', headers: { Authorization: `Bearer ${t.key}`, 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(25000) });
      if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
      const j = await r.json();
      const content = j.choices?.[0]?.message?.content ?? '';
      return { provider: t.name, content: json ? JSON.parse(content) : content };
    } catch (e) {
      log('llm-error', `${t.name} 실패: ${String(e.message).slice(0, 120)}`);
    }
  }
  return null;
}

const context = () => `오늘은 ${TODAY}. 사용자 캘린더: ${JSON.stringify(calendar)}. 보유 옷장: ${closet.map((c) => `${c.color} ${c.name}(${c.category})`).join(', ')}.`;

// ---- 네이버 쇼핑 검색 (키 있으면 실제 상품, 없으면 샘플 카탈로그) ----
async function matchProduct(item) {
  const sample = catalog.find((p) => p.id === item.productId);
  if (process.env.NAVER_CLIENT_ID && process.env.NAVER_CLIENT_SECRET) {
    try {
      const u = 'https://openapi.naver.com/v1/search/shop.json?display=1&sort=sim&query=' + encodeURIComponent(item.keyword);
      const r = await fetch(u, { headers: { 'X-Naver-Client-Id': process.env.NAVER_CLIENT_ID, 'X-Naver-Client-Secret': process.env.NAVER_CLIENT_SECRET }, signal: AbortSignal.timeout(6000) });
      const j = await r.json();
      const it = j.items?.[0];
      if (it) return { ...sample, name: it.title.replace(/<[^>]+>/g, ''), price: Number(it.lprice), seller: it.mallName, link: it.link, image: it.image, source: '네이버 쇼핑' };
    } catch (e) { log('api-error', '네이버 쇼핑 실패, 샘플 사용'); }
  }
  return { ...sample, source: '샘플 가격' };
}

// ---- 규칙 엔진: 판정은 여기서만 ----
function judge(lines) {
  const rules = state.rules;
  const kept = [], skipped = [];
  for (const l of lines) {
    const dup = rules.duplicateSuggestSkip && closet.find((c) => c.category === l.category && c.color === l.color && c.type === l.type);
    if (dup) skipped.push({ ...l, reason: `이미 ${dup.color} ${dup.name}이(가) 옷장에 있어요` });
    else kept.push(l);
  }
  const shipping = kept.length ? 3000 : 0;
  const total = kept.reduce((s, l) => s + l.price, 0) + shipping;
  const remaining = state.budget.monthly - state.budget.spent;
  const why = [];
  if (total >= rules.approvalAmount) why.push(`총액 ${won(total)}이 결재 기준 ${won(rules.approvalAmount)} 이상`);
  if (rules.nonRefundableNeedsApproval) kept.filter((l) => !l.refundable).forEach((l) => why.push(`${l.name}은(는) 환불 불가 상품`));
  kept.filter((l) => rules.categoryMode[l.category] === 'approve').forEach((l) => why.push(`${l.category}은(는) 결재 카테고리로 지정됨 (후회 체크 반영)`));
  if (total > remaining) why.push(`이번 달 남은 의류 예산 ${won(remaining)} 초과`);
  const decision = kept.length === 0 ? 'skip' : why.length ? 'approve' : 'auto';
  return { kept, skipped, shipping, total, remaining, why, decision };
}

async function buildOutfit(o) {
  const lines = [];
  for (const it of o.items) {
    if (it.owned) continue;
    const p = await matchProduct(it);
    lines.push({ ...p, category: it.category, color: it.color, type: it.type });
  }
  return { outfit: o, ...judge(lines) };
}

// ---- 요청 처리 ----
async function handleChat(text) {
  log('request', `사용자: ${text}`);
  // 1) 의도 해석 (LLM, JSON)
  const sys = `${context()}\n너는 쇼핑 비서의 의도 해석기다. 사용자의 말을 JSON으로만 구조화해라. 키: intent("outfit"|"restock"|"other"), occasion(문자열), date(YYYY-MM-DD 또는 null, 캘린더 근거로), dress_code(문자열), budget_hint(숫자 또는 null), evidence(근거 배열, 각 항목은 {"source":"캘린더|옷장|대화","text":"..."}). 추측한 값은 evidence에 "추정"이라고 적어라.`;
  const parsed = await llm([{ role: 'system', content: sys }, { role: 'user', content: text }], true);
  let intent = parsed?.content || null;
  if (!intent) intent = /양말|떨어/.test(text) ? { intent: 'restock' } : { intent: 'outfit', occasion: '결혼식 하객', date: '2026-11-08', evidence: [{ source: '캘린더', text: '11/8(일) 12:00 대학 동기 결혼식' }] };
  if (!intent.intent) intent.intent = /양말|떨어/.test(text) ? 'restock' : 'outfit';

  // 2) 상품 연결과 규칙 판정
  if (intent.intent === 'restock') {
    const o = outfits.find((x) => x.tag === 'restock');
    const r = await buildOutfit(o);
    const quote = makeQuote(r, intent);
    if (r.decision === 'auto') {
      const receipt = checkout(quote.id, 'auto-' + quote.id, '자동 처리');
      const reply = await compose(text, r, 'auto');
      return { reply, intent, provider: parsed?.provider, cards: [{ kind: 'receipt', receipt, quote }] };
    }
    return { reply: await compose(text, r, r.decision), intent, provider: parsed?.provider, cards: [{ kind: 'approval', quote }] };
  }

  const cands = outfits.filter((o) => o.tag === 'wedding');
  const results = [];
  for (const o of cands) results.push(await buildOutfit(o));
  const main = results[0];
  const alt = results[1];
  const quote = makeQuote(main, intent, alt);
  const cards = [{ kind: 'proposal', quote }];
  if (main.skipped.length) cards.push({ kind: 'skip', items: main.skipped });
  if (main.decision === 'approve') cards.push({ kind: 'approval', quote });
  else if (main.decision === 'auto') cards.push({ kind: 'receipt', receipt: checkout(quote.id, 'auto-' + quote.id, '자동 처리'), quote });
  const reply = await compose(text, main, main.decision, alt);
  return { reply, intent, provider: parsed?.provider, cards };
}

function makeQuote(r, intent, alt) {
  const id = crypto.randomUUID().slice(0, 8);
  const q = { id, outfit: r.outfit.name, lines: r.kept, skipped: r.skipped, shipping: r.shipping, total: r.total, remaining: r.remaining, why: r.why, decision: r.decision, evidence: intent?.evidence || [], alternative: alt ? { name: alt.outfit.name, total: alt.total, decision: alt.decision, why: alt.why } : null, status: 'pending', createdAt: new Date().toISOString() };
  state.quotes[id] = q;
  log('judge', `${r.outfit.name}: ${r.decision === 'approve' ? '결재 올림' : r.decision === 'auto' ? '자동 처리' : '구매 불필요'} (${r.why.join(' / ') || '문턱 안'})`);
  return q;
}

async function compose(userText, r, decision, alt) {
  const facts = { decision, outfit: r.outfit.name, total: r.total, why: r.why, skipped: r.skipped.map((s) => `${s.name}: ${s.reason}`), alternative: alt ? `${alt.outfit.name} ${alt.total}원 (참고용 대안, 처리하지 않음)` : null };
  const sys = `${context()}\n너는 친근한 캐릭터 쇼핑 비서 '볼티'다. 아래 판정 결과(facts)를 바꾸지 말고, 2~3문장 반말로 짧게 전해라. 판정은 규칙이 이미 정했다. decision이 approve면 '아직 결제 안 했고 네 결재를 기다린다'고 이유와 함께 말해라. auto면 처리했다고 말해라. skipped가 있으면 '안 사는 게 이득'이라고 말려라. alternative는 참고용으로만 언급하고 처리했다고 말하지 마라. 숫자는 facts 그대로만 써라.`;
  // 상태를 바꾸는 문장(결제했다/승인됐다)은 서버가 확인한 사실만 쓴다. LLM 문장은 사용하지 않고 템플릿으로 고정.
  void sys; void facts;
  if (decision === 'auto') return `늘 사던 걸로 ${won(r.total)}에 처리했어. 영수증 남겨둘게.`;
  return `${r.outfit.name}으로 골라봤어. 아직 결제는 안 했어. ${r.why.join(', ')}라서 네 결재가 필요해.${r.skipped.length ? '\n그리고 ' + r.skipped.map((s) => `${s.name}는 ${s.reason.replace('있어요', '있어')}`).join(', ') + '. 그건 안 사는 게 이득이라 빼두었어.' : ''}${alt ? `\n참고로 ${alt.outfit.name}(${won(alt.total)})은 문턱 안이라 바로 처리할 수 있는 대안이야.` : ''}`;
}

function checkout(quoteId, idemKey, by) {
  const q = state.quotes[quoteId];
  if (!q) throw new Error('견적을 찾을 수 없어요');
  if (state.idem.has(idemKey) || q.status === 'paid') return state.receipts.find((x) => x.quoteId === quoteId);
  state.idem.add(idemKey);
  q.status = 'paid';
  const receipt = { id: 'R-' + crypto.randomUUID().slice(0, 6).toUpperCase(), quoteId, outfit: q.outfit, lines: q.lines.map((l) => ({ name: l.name, price: l.price, category: l.category, seller: l.seller })), total: q.total, by, at: new Date().toISOString(), simulated: true, regret: null };
  state.receipts.unshift(receipt);
  state.budget.spent += q.total;
  log('pay', `${by}: ${q.outfit} ${won(q.total)} 시뮬레이션 결제 (${receipt.id})`);
  return receipt;
}

function regret(receiptId, lineName, answer) {
  const rc = state.receipts.find((r) => r.id === receiptId);
  const line = rc?.lines.find((l) => l.name === lineName);
  if (!line) throw new Error('구매 내역을 찾을 수 없어요');
  line.regret = answer;
  log('regret', `후회 체크: ${line.name} → ${answer === 'bad' ? '별로' : '잘 샀어'}`);
  let change = null;
  if (answer === 'bad' && state.rules.categoryMode[line.category] !== 'approve') {
    const before = state.rules.categoryMode[line.category];
    state.rules.categoryMode[line.category] = 'approve';
    change = { at: new Date().toISOString(), rule: `${line.category} 카테고리`, before: before === 'auto' ? '자동 처리' : '결재', after: '결재', reason: `${line.name} 구매 후회` };
    state.ruleHistory.unshift(change);
    log('rule', `문턱 변경: ${change.rule} ${change.before} → ${change.after} (${change.reason})`);
  }
  return { change };
}

// ---- HTTP ----
const send = (res, code, obj) => { res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' }); res.end(JSON.stringify(obj)); };
const body = (req) => new Promise((ok) => { let d = ''; req.on('data', (c) => (d += c)); req.on('end', () => { try { ok(JSON.parse(d || '{}')); } catch { ok({}); } }); });

http.createServer(async (req, res) => {
  try {
    const u = new URL(req.url, 'http://x');
    if (req.method === 'POST' && u.pathname === '/api/chat') return send(res, 200, await handleChat(String((await body(req)).text || '').slice(0, 500)));
    if (req.method === 'POST' && u.pathname === '/api/approve') { const b = await body(req); const q = state.quotes[b.quoteId]; if (b.action === 'reject') { if (q) q.status = 'rejected'; log('reject', `결재 반려: ${q?.outfit}`); return send(res, 200, { ok: true, rejected: true }); } return send(res, 200, { receipt: checkout(b.quoteId, b.idemKey || crypto.randomUUID(), '사용자 결재 승인') }); }
    if (req.method === 'POST' && u.pathname === '/api/regret') { const b = await body(req); return send(res, 200, regret(b.receiptId, b.line, b.answer)); }
    if (req.method === 'POST' && u.pathname === '/api/rules') { const b = await body(req); if (b.approvalAmount) state.rules.approvalAmount = Number(b.approvalAmount); if (b.categoryMode) Object.assign(state.rules.categoryMode, b.categoryMode); log('rule', '사용자가 문턱 설정 변경'); return send(res, 200, state.rules); }
    if (u.pathname === '/api/state') return send(res, 200, { rules: state.rules, budget: state.budget, receipts: state.receipts, log: state.log.slice(0, 60), ruleHistory: state.ruleHistory, llm: { skt: !!process.env.OBA_API_KEY, openai: !!process.env.OPENAI_API_KEY, naver: !!process.env.NAVER_CLIENT_ID } });
    const file = path.join(ROOT, 'public', u.pathname === '/' ? 'index.html' : path.normalize(u.pathname).replace(/^([\\/])+/, ''));
    if (!file.startsWith(path.join(ROOT, 'public')) || !fs.existsSync(file)) { res.writeHead(404); return res.end('not found'); }
    const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.png': 'image/png', '.svg': 'image/svg+xml' };
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
    fs.createReadStream(file).pipe(res);
  } catch (e) {
    send(res, 500, { error: e.message });
  }
}).listen(PORT, () => console.log(`옷결재: http://localhost:${PORT}  (SKT A.X K2: ${process.env.OBA_API_KEY ? 'on' : 'off'}, OpenAI: ${process.env.OPENAI_API_KEY ? 'on' : 'off'}, 네이버: ${process.env.NAVER_CLIENT_ID ? 'on' : 'off'})`));

function loadEnv(p) {
  if (!fs.existsSync(p)) return;
  for (const line of fs.readFileSync(p, 'utf8').split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
    if (m && !process.env[m[1]]) process.env[m[1]] = m[2].replace(/^['"]|['"]$/g, '');
  }
}
