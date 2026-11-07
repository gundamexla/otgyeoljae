import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, cp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { apiStatusLabel, requestStatusLabel } from '../public/api-status.js';

const root = fileURLToPath(new URL('../', import.meta.url));
async function app(t, options = {}) {
  // Copy only source/fixtures, never .env, private photos, or chat history.
  const dir = await mkdtemp(path.join(tmpdir(), 'otgyeoljae-regression-'));
  for (const file of ['server.mjs','package.json','public','data']) await cp(path.join(root,file), path.join(dir,file), {recursive:true});
  const env = { ...process.env, PORT:'0', HOST:'127.0.0.1' };
  for (const name of ['OBA_API_KEY','OPENAI_API_KEY','NAVER_CLIENT_ID','NAVER_CLIENT_SECRET','NODE_OPTIONS']) delete env[name];
  if (options.skt) env.OBA_API_KEY = 'test-only-skt';
  if (options.openai) env.OPENAI_API_KEY = 'test-only-openai';
  env.MOCK_SKT = options.skt || 'success'; env.MOCK_OPENAI = options.openai || 'success';
  const child = spawn(process.execPath, ['--import', pathToFileURL(path.join(root,'tests/fixtures/mock-provider.mjs')).href, 'server.mjs'], {cwd:dir,env,windowsHide:true,stdio:['ignore','pipe','pipe']});
  t.after(async () => {
    if (child.exitCode === null) { const done = once(child,'exit'); child.kill(); await done; }
    // mkdtemp is a dedicated test directory under the OS temp directory.
    assert.equal(path.dirname(dir), path.resolve(tmpdir()));
    assert(path.basename(dir).startsWith('otgyeoljae-regression-'));
    await rm(dir,{recursive:true,force:true});
  });
  const base = await new Promise((resolve,reject) => {
    let output = '', errors = '';
    const timer = setTimeout(() => reject(new Error('Server startup timeout: '+errors)),8000);
    child.stderr.on('data', b => errors += b);
    child.once('error', e => { clearTimeout(timer); reject(e); });
    child.once('exit', c => { clearTimeout(timer); reject(new Error(`Server exited ${c}: ${errors}`)); });
    child.stdout.on('data', b => { output += b; const m = output.match(/http:\/\/localhost:(\d+)/); if (m) {clearTimeout(timer);resolve(`http://127.0.0.1:${m[1]}`);} });
  });
  const request = async (route, body) => {
    const r = await fetch(base+route, body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    return {status:r.status,data:await r.json()};
  };
  return {
    request,
    state:async () => (await request('/api/state')).data,
    wedding:async () => (await request('/api/chat',{text:'이번 주말 결혼식 가는데 뭐 입지'})).data.cards.find(c=>c.kind==='proposal').quote,
    moaQuote:async () => (await request('/api/moa/quote',{title:'면접 착장',lines:[{name:'이지케어 슬랙스 차콜',price:39000,category:'하의'}]})).data,
  };
}

test('반려 후 승인 차단, 반복 반려와 없는 견적 처리', async t => {
  const a=await app(t), q=await a.wedding(), before=await a.state();
  assert.equal((await a.request('/api/approve',{quoteId:q.id,action:'reject'})).status,200);
  assert.equal((await a.request('/api/approve',{quoteId:q.id,action:'reject'})).status,200);
  assert.equal((await a.request('/api/approve',{quoteId:q.id,idemKey:'blocked'})).status,409);
  assert.equal((await a.request('/api/alternative',{quoteId:q.id})).status,409);
  assert.equal((await a.request('/api/approve',{quoteId:'constructor',action:'reject'})).status,404);
  const after=await a.state(); assert.deepEqual(after.receipts,[]); assert.deepEqual(after.wallet,before.wallet); assert.deepEqual(after.budget,before.budget);
});

test('동시 중복 승인과 완료 후 재시도는 영수증 하나, 완료 후 반려 불가', async t => {
  const a=await app(t), q=await a.moaQuote();
  const responses=await Promise.all(['once','once','new-key'].map(idemKey=>a.request('/api/approve',{quoteId:q.id,idemKey})));
  assert(responses.every(r=>r.status===200));
  assert.equal(new Set(responses.map(r=>r.data.receipt.id)).size,1);
  assert.equal((await a.request('/api/approve',{quoteId:q.id,action:'reject'})).status,409);
  const s=await a.state(); assert.equal(s.receipts.length,1); assert.equal(s.wallet.balance,88000); assert.equal(s.budget.spent,92000);
});

test('다른 견적의 멱등키와 알 수 없는 action은 결제하지 않음', async t => {
  const a=await app(t), q=await a.moaQuote(), q2=await a.moaQuote();
  await a.request('/api/approve',{quoteId:q.id,idemKey:'same'});
  assert.equal((await a.request('/api/approve',{quoteId:q2.id,idemKey:'same'})).status,409);
  assert.equal((await a.request('/api/approve',{quoteId:q2.id,action:'typo'})).status,400);
  assert.equal((await a.state()).receipts.length,1);
});

test('대안 선택은 83,000원 새 견적으로 전환, 이전 견적 무효, 승인 전 미결제', async t => {
  const a=await app(t), q=await a.wedding(); assert.equal(q.total,190000);
  const switched=await a.request('/api/alternative',{quoteId:q.id}); assert.equal(switched.status,200);
  const alt=switched.data.quote; assert.notEqual(alt.id,q.id); assert.equal(alt.replaces,q.id); assert.equal(alt.total,83000); assert.match(alt.outfit,/하객룩 B/); assert.equal(alt.status,'pending');
  assert.equal((await a.state()).receipts.length,0);
  assert.equal((await a.request('/api/approve',{quoteId:q.id})).status,409);
  assert.equal((await a.request('/api/approve',{quoteId:q.id,action:'reject'})).status,409);
  const paid=await a.request('/api/approve',{quoteId:alt.id,idemKey:'alt'});
  assert.equal(paid.data.receipt.total,83000); assert.match(paid.data.receipt.outfit,/하객룩 B/);
  assert.equal((await a.request('/api/alternative',{quoteId:alt.id})).status,409);
});

test('대안은 변경 시점의 규칙·예산을 재계산하고 연속 클릭으로 중복 생성하지 않음', async t => {
  const a=await app(t), q=await a.wedding(), other=await a.moaQuote();
  await a.request('/api/approve',{quoteId:other.id,idemKey:'other'});
  await a.request('/api/rules',{approvalAmount:50000});
  const results=await Promise.all([a.request('/api/alternative',{quoteId:q.id}),a.request('/api/alternative',{quoteId:q.id})]);
  assert.deepEqual(results.map(r=>r.status).sort(),[200,409]);
  const alt=results.find(r=>r.status===200).data.quote;
  assert.equal(alt.remaining,208000); assert.equal(alt.decision,'approve'); assert(alt.why.some(w=>w.includes('50,000')));
  assert.equal((await a.request('/api/alternative',{quoteId:alt.id})).status,400);
  assert.equal((await a.state()).receipts.length,1);
});

test('팀원 면접 시나리오 42,000원과 사용자 적용 피드백 유지', async t => {
  const a=await app(t), start=await a.request('/api/moa/start',{text:'면접 옷'});
  assert.equal(start.data.usable,60000); assert.equal(start.data.execution.status,'not_configured');
  const q=await a.moaQuote(); assert.equal(q.total,42000); assert.equal(q.after,88000); assert.equal(q.extra,18000);
  await a.request('/api/approve',{quoteId:q.id,idemKey:'moa'});
  await a.request('/api/moa/feedback',{item:'바지',note:'허리 불편',rule:'허리 치수 확인',apply:true});
  const s=await a.state(); assert.equal(s.wallet.balance,88000); assert(s.checks.includes('허리 치수 확인'));
});

test('키 없음, 키 설정·미호출, 실제 호출 성공은 별도 상태', async t => {
  const a=await app(t); assert.match(apiStatusLabel((await a.state()).llm),/키 없음.*아직 호출하지 않음/);
  const noKey=await a.request('/api/moa/start',{text:'면접 옷'}); assert.match(requestStatusLabel(noKey.data.execution),/키 없음 · 샘플 응답/);
  const b=await app(t,{skt:'success'}); assert.match(apiStatusLabel((await b.state()).llm),/키 설정됨.*아직 호출하지 않음/);
  const ok=await b.request('/api/moa/start',{text:'면접 옷'});
  assert.equal(ok.data.provider,'SKT A.X K2'); assert.equal(ok.data.execution.status,'success');
  assert.match(apiStatusLabel((await b.state()).llm),/SKT A.X K2 호출 성공/);
});

test('SKT 실패 후 OpenAI 성공은 실제 대체 제공자로 표시', async t => {
  const a=await app(t,{skt:'http',openai:'success'});
  const r=await a.request('/api/chat',{text:'결혼식 옷'});
  assert.equal(r.data.provider,'OpenAI'); assert.deepEqual(r.data.execution.attempts.map(x=>x.status),['error','success']);
  assert.match(requestStatusLabel(r.data.execution),/OpenAI 대체 호출 성공 \(SKT A.X K2 실패\)/);
  assert.match(apiStatusLabel((await a.state()).llm),/OpenAI 대체 호출 성공/);
});

for (const mode of ['http','timeout','network','malformed','invalid']) {
  test(`${mode} 응답은 연결 성공으로 표시하지 않고 샘플로 전환`, async t => {
    const a=await app(t,{skt:mode}), r=await a.request('/api/moa/start',{text:'면접 옷'});
    assert.equal(r.status,200); assert.equal(r.data.provider,null); assert.equal(r.data.execution.status,'fallback');
    assert.match(requestStatusLabel(r.data.execution),/API 호출 실패 · 샘플 응답/);
    const s=await a.state(); assert.match(apiStatusLabel(s.llm),/API 호출 실패/);
    assert(!JSON.stringify(s).includes('DO_NOT_EXPOSE_PROVIDER_BODY')); assert(!JSON.stringify(s).includes('test-only-skt'));
  });
}

test('양쪽 제공자가 실패하면 성공 제공자 없이 샘플 응답', async t => {
  const a=await app(t,{skt:'http',openai:'http'}), r=await a.request('/api/chat',{text:'결혼식 옷'});
  assert.equal(r.data.execution.attempts.length,2); assert.equal(r.data.execution.status,'fallback'); assert.equal(r.data.provider,null);
});

test('이전 요청이 늦게 성공해도 최근 실패 상태를 덮어쓰지 않음', async t => {
  const a=await app(t,{skt:'race'});
  const slow=a.request('/api/moa/start',{text:'slow-success'});
  for (let i=0;i<50;i++) { if ((await a.state()).llm.lastRequest?.status==='running') break; await new Promise(r=>setTimeout(r,5)); }
  const fast=await a.request('/api/moa/start',{text:'new-failure'});
  assert.equal((await slow).data.execution.status,'success'); assert.equal(fast.data.execution.status,'fallback');
  const last=(await a.state()).llm.lastRequest; assert.equal(last.id,fast.data.execution.id); assert.equal(last.status,'fallback');
});

test('화면 모듈 스크립트 문법 검사', async () => {
  const html=await readFile(path.join(root,'public/index.html'),'utf8');
  const script=html.match(/<script type="module">([\s\S]*?)<\/script>/)[1];
  const child=spawn(process.execPath,['--input-type=module','--check'],{stdio:['pipe','ignore','pipe'],windowsHide:true});
  let stderr=''; child.stderr.on('data', b=>stderr+=b); child.stdin.end(script);
  const [code]=await once(child,'exit'); assert.equal(code,0,stderr);
});
