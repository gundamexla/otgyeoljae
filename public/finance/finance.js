import {FITTING_URL,FINANCE_HOME_URL,validReceipt,receiptPath,externalReceiptUrl} from '../moa-connections.js';
const $=id=>document.getElementById(id),won=n=>n.toLocaleString('ko-KR')+'원',params=new URLSearchParams(location.search);
const selected=params.get('receipt'),from=params.get('from')==='desktop'?'desktop':'mobile';
$('back').href=from==='desktop'?'/?view=desktop':'/mobile/';$('fitting').href=FITTING_URL;$('finance-home').href=FINANCE_HOME_URL;
function row(name,price){const div=document.createElement('div');div.className='row';const label=document.createElement('span'),value=document.createElement('b');label.textContent=name;value.textContent=won(price);div.append(label,value);return div;}
function display(r){$('receipt').hidden=false;$('outfit').textContent=r.outfit||'모의 구매';$('receipt-id').textContent=r.id;$('lines').replaceChildren(...r.lines.map(l=>row(l.name,l.price)));$('shipping').textContent=won(r.shipping);$('total').textContent=won(r.total);$('after').textContent=won(r.balanceAfter);$('receipt-time').textContent=Number.isFinite(Date.parse(r.at))?new Date(r.at).toLocaleString('ko-KR'):'';$('external-receipt').href=externalReceiptUrl(r);}
async function load(){
  $('retry').disabled=true;$('wallet').hidden=true;$('receipt').hidden=true;$('history').replaceChildren();
  try{const response=await fetch('/api/state',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(15000)});if(!response.ok)throw Error('서버 기록을 가져오지 못했어요.');const s=await response.json();
    if(s.paymentMode!=='simulation'||!s.wallet||!Array.isArray(s.receipts)||!Number.isSafeInteger(s.wallet.balance)||!Number.isSafeInteger(s.wallet.reserve))throw Error('서버 응답을 확인하지 못했어요.');
    const receipts=s.receipts.filter(validReceipt),r=selected?receipts.find(r=>r.id===selected):receipts[0];
    if(selected&&!r)throw Error('현재 서버 세션에서 이 영수증을 찾지 못했어요.');
    $('wallet').hidden=false;$('balance').textContent=won(s.wallet.balance);$('reserve').textContent=won(s.wallet.reserve);$('available').textContent=won(s.wallet.balance-s.wallet.reserve);
    $('status').textContent=r?'서버에서 모의 결제 완료와 현재 잔액을 확인했어요.':'아직 완료된 모의 결제가 없어요.';
    if(r)display(r);for(const receipt of receipts){const a=document.createElement('a'),label=document.createElement('span'),amount=document.createElement('b');a.href=receiptPath(receipt,from);label.textContent=receipt.outfit||receipt.lines.map(l=>l.name).join(' + ');amount.textContent=won(receipt.total);a.append(label,amount);$('history').append(a);}
    $('retry').hidden=true;
  }catch(error){let copy;try{copy=JSON.parse(sessionStorage.getItem('moa-confirmed-receipt:'+selected)||'null');}catch{}
    $('status').textContent=error.message+(validReceipt(copy)?' 아래는 결제 완료 때 이 브라우저에 보관한 응답 사본이에요. 현재 잔액은 재확인하지 못했어요.':' 같은 구매를 다시 실행하지 말고 서버 기록을 조회해 주세요.');
    if(validReceipt(copy))display(copy);$('retry').hidden=false;
  }finally{$('retry').disabled=false;}
}
$('retry').onclick=load;load();
