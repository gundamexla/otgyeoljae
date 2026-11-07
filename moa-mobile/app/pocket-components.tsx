import {useEffect,useRef,useState,type ReactNode} from 'react';
import type {ServerState} from '../lib/moa-api';

export function RecordPages({items,label,pageSize=3}:{items:ReactNode[];label:string;pageSize?:number}){
  const [page,setPage]=useState(0),section=useRef<HTMLElement>(null),lastPage=useRef(0);
  const pages=Math.max(1,Math.ceil(items.length/pageSize));
  const current=Math.min(page,pages-1);
  useEffect(()=>setPage(0),[items.length]);
  useEffect(()=>{if(lastPage.current!==current){lastPage.current=current;section.current?.scrollIntoView({block:"start"});}},[current]);
  return <section ref={section} className="record-pages" aria-label={label}>
    {items.slice(current*pageSize,(current+1)*pageSize)}
    {pages>1&&<nav className="record-pager" aria-label={label+' 페이지'}>
      <button disabled={current===0} onClick={()=>setPage(current-1)}>이전</button>
      <span aria-live="polite">{current+1} / {pages} 페이지</span>
      <button disabled={current===pages-1} onClick={()=>setPage(current+1)}>다음</button>
    </nav>}
  </section>;
}

export function exportDemoRecords(s:ServerState){
  // A reference export, not a credential or a wallet-restoration command.
  const data={format:'moa-demo-records',version:1,exportedAt:new Date().toISOString(),paymentMode:s.paymentMode,wallet:s.wallet,rules:s.rules,checks:s.checks,receipts:s.receipts,feedback:s.feedback.filter(f=>f.receiptId),ruleHistory:s.ruleHistory};
  const url=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json;charset=utf-8'}));
  const anchor=document.createElement('a');anchor.href=url;anchor.download='moa-demo-records-'+new Date().toISOString().slice(0,10)+'.json';anchor.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
