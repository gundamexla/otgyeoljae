const key='moa-purchase-receipts-v1',won=n=>n.toLocaleString('ko-KR')+'원';
export function validate(r){return !!r&&r.version===1&&r.demo===true&&typeof r.id==='string'&&/^[a-zA-Z0-9-]{16,50}$/.test(r.id)&&typeof r.item==='string'&&r.item.length>0&&r.item.length<=120&&['price','shipping','total','balance','reserve'].every(k=>Number.isSafeInteger(r[k])&&r[k]>=0&&r[k]<=10000000)&&r.price+r.shipping===r.total&&r.total>0&&r.balance>=r.reserve;}
if(typeof document!=='undefined'){
const el=id=>document.getElementById(id);let records=[];
try{const saved=JSON.parse(localStorage.getItem(key)||'[]');if(Array.isArray(saved))records=saved.filter(validate).slice(0,99);}catch{}
try{const raw=new URLSearchParams(location.hash.slice(1)).get('moa');let receipt=raw?JSON.parse(raw):records[0];if(!validate(receipt))throw Error('표시할 결제 내역이 없어요. 모아에서 모의 결제를 완료한 뒤 열어 주세요.');
const old=records.find(r=>r.id===receipt.id);if(old&&JSON.stringify(old)!==JSON.stringify(receipt))throw Error('같은 기록 번호의 내용이 달라 저장하지 않았어요.');
if(!old){records.unshift(receipt);try{localStorage.setItem(key,JSON.stringify(records));}catch{el('error').textContent='저장 공간 제한으로 이번 영수증을 저장하지 못했어요.';}}
el('content').hidden=false;for(const k of ['balance','reserve','price','shipping','total'])el(k).textContent=won(receipt[k]);el('available').textContent=won(receipt.balance-receipt.reserve);el('item').textContent=receipt.item;el('id').textContent='기록 번호 '+receipt.id;
const title=document.createElement('h3');title.textContent='이 브라우저의 모의 구매 기록';el('history').append(title);for(const r of records.slice(0,10)){const row=document.createElement('div');row.className='entry';row.textContent=r.item+' · '+won(r.total);el('history').append(row);}
}catch(e){el('error').textContent=e.message;}}
