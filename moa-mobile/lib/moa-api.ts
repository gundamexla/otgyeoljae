export type Product = {id:string; name:string; price:number; category:string; source?:string; seller?:string; care?:string; waist?:string; reason?:string};
export type Quote = {id:string; outfit:string; productIds:string[]; lines:Product[]; shipping:number; total:number; after:number; reserve:number; extra:number; decision:'auto'|'approve'|'blocked'|'skip'; status:string; why:string[]; reuse:string[]; skipped:Product[]; evidence:{source:string;text:string}[]; alternative?:{id:string;name:string;total:number}; createdAt:string};
export type Receipt = {id:string;quoteId:string;outfit:string;lines:Product[];shipping:number;total:number;balanceBefore:number;balanceAfter:number;reserve:number;by:string;at:string;simulated:boolean;status:string};
export type Feedback = {id:string;receiptId?:string;item:string;note:string;applied?:boolean};
export type ServerState = {sessionRevision?:string;wallet:{balance:number;reserve:number};rules:{approvalAmount:number;categoryMode:Record<string,'auto'|'approve'>};quotes:Quote[];receipts:Receipt[];feedback:Feedback[];checks:string[];ruleHistory:{at:string;rule:string;before:unknown;after:unknown;reason:string}[];log:{at:string;text:string}[];closet:{name:string;color:string}[];llm:{skt:boolean;lastRequest:null|{status:string;provider?:string;finishedAt?:string}};paymentMode:string;persistence:string};
export type Result = {reply?:string;quote?:Quote;quotes?:Quote[];receipt?:Receipt|null;rejected?:boolean;resolved?:boolean;feedback?:Feedback;proposed?:string[];change?:unknown};
export class ApiError extends Error {
  constructor(message:string,public uncertain=false,public code?:string){super(message);this.name='ApiError';}
}
export async function api<T>(url:string,body?:Record<string,unknown>,signal?:AbortSignal,sessionRevision?:string):Promise<T>{
  const timeout=new AbortController();const timer=setTimeout(()=>timeout.abort(),15000);
  const abort=()=>timeout.abort();signal?.addEventListener('abort',abort,{once:true});
  if(signal?.aborted)timeout.abort();
  try{
    const response=await fetch(url,{method:body?'POST':'GET',credentials:'same-origin',cache:'no-store',headers:body?{'Content-Type':'application/json',...(sessionRevision?{'X-Moa-Session':sessionRevision}:{})}:undefined,body:body?JSON.stringify(body):undefined,signal:timeout.signal});
    let data;try{data=await response.json();}catch{throw new ApiError('서버 응답을 읽지 못했어요. 처리 결과를 먼저 확인해 주세요.',true);}
    if(!response.ok||data.error)throw new ApiError(typeof data.error==='string'?data.error:'서버 요청에 실패했어요.',response.status>=500,data.code);
    return data as T;
  }catch(error){if(error instanceof ApiError)throw error;throw new ApiError('서버에 연결하지 못했어요. 입력은 남겨 두었어요.',true);}
  finally{clearTimeout(timer);signal?.removeEventListener('abort',abort);}
}
export async function getState(signal?:AbortSignal){
  const s=await api<ServerState>('/api/state',undefined,signal);
  if(!s.wallet||!Number.isSafeInteger(s.wallet.balance)||!Number.isSafeInteger(s.wallet.reserve)||!Array.isArray(s.quotes)||!Array.isArray(s.receipts)||!s.rules)throw new ApiError('구매 상태 응답이 올바르지 않아요. 실행을 멈췄어요.',true);
  if(typeof s.sessionRevision!=='string'||!s.sessionRevision)throw new ApiError('서버 세션 확인 기능이 없어 실행을 차단했어요. 서버 업데이트를 확인해 주세요.');
  if(s.paymentMode!=='simulation')throw new ApiError('모의 결제 서버가 아니어서 실행을 차단했어요.');
  return s;
}

