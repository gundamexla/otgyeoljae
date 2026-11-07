export const FITTING_URL='https://oba-fitting-hackathon.jrnanmari.chatgpt.site/';
export const FINANCE_HOME_URL='https://moa-finance-hackathon.jrnanmari.chatgpt.site/';
export const VERCEL_URL='https://otgyeoljae.vercel.app/';
export function validReceipt(r){
  return !!r&&r.simulated===true&&r.status==='paid'&&typeof r.id==='string'&&/^R-[A-F0-9]{10}$/.test(r.id)&&
    Array.isArray(r.lines)&&r.lines.length>0&&r.lines.length<=30&&r.lines.every(l=>typeof l.name==='string'&&l.name.length>0&&l.name.length<=200&&Number.isSafeInteger(l.price)&&l.price>=0)&&
    ['shipping','total','balanceBefore','balanceAfter','reserve'].every(k=>Number.isSafeInteger(r[k])&&r[k]>=0&&r[k]<=100000000)&&
    r.total>0&&r.lines.reduce((n,l)=>n+l.price,0)+r.shipping===r.total&&r.balanceBefore-r.total===r.balanceAfter&&r.balanceAfter>=r.reserve;
}
export function receiptPath(r,from='mobile'){
  if(!validReceipt(r))throw new Error('서버가 확인한 모의 영수증 형식이 아니에요.');
  return '/finance/?receipt='+encodeURIComponent(r.id)+'&from='+(from==='desktop'?'desktop':'mobile');
}
export function openFinanceReceipt(r,from='mobile'){
  const path=receiptPath(r,from);
  // A recovery copy, never an authority to execute or restore a wallet.
  try{sessionStorage.setItem('moa-confirmed-receipt:'+r.id,JSON.stringify(r));}catch{}
  window.location.assign(path);
}
export function externalReceiptUrl(r){
  if(!validReceipt(r))throw new Error('확인된 모의 결제만 전달할 수 있어요.');
  const value={version:1,id:'vercel-'+r.id,item:r.lines.map(l=>l.name).join(' + ').slice(0,120),price:r.total-r.shipping,shipping:r.shipping,total:r.total,balance:r.balanceAfter,reserve:r.reserve,demo:true};
  return FINANCE_HOME_URL+'receipt.html#moa='+encodeURIComponent(JSON.stringify(value));
}
