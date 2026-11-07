import type {Receipt} from '../moa-mobile/lib/moa-api';
export const FITTING_URL:string;
export const FINANCE_HOME_URL:string;
export const VERCEL_URL:string;
export function validReceipt(value:unknown):value is Receipt;
export function receiptPath(receipt:Receipt,from?:string):string;
export function openFinanceReceipt(receipt:Receipt,from?:string):void;
export function externalReceiptUrl(receipt:Receipt):string;
