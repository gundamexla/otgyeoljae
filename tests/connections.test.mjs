import test from 'node:test';
import assert from 'node:assert/strict';
import {validReceipt,receiptPath,externalReceiptUrl,VERCEL_URL} from '../public/moa-connections.js';
import {validate} from '../integrations/finance-receipt/receipt.js';
const receipt={id:'R-0123456789',quoteId:'example',outfit:'면접 바지',lines:[{id:'i-wash',name:'물세탁 바지',price:39000}],shipping:3000,total:42000,balanceBefore:130000,balanceAfter:88000,reserve:70000,simulated:true,status:'paid',at:'2026-11-07T08:00:00Z'};
test('confirmed receipt opens same-origin finance with an allowlisted return target',()=>{assert.ok(validReceipt(receipt));assert.equal(receiptPath(receipt,'desktop'),'/finance/?receipt=R-0123456789&from=desktop');assert.equal(receiptPath(receipt,'https://evil.invalid'),'/finance/?receipt=R-0123456789&from=mobile');});
test('short server receipt id becomes compatible with existing finance viewer',()=>{const link=new URL(externalReceiptUrl(receipt));const data=JSON.parse(new URLSearchParams(link.hash.slice(1)).get('moa'));assert.ok(validate(data));assert.equal(data.total,42000);assert.equal(data.balance,88000);assert.equal(data.id,'vercel-R-0123456789');assert.equal(externalReceiptUrl(receipt),externalReceiptUrl(receipt));});
for(const [name,patch]of [['unpaid',{status:'pending'}],['real payment',{simulated:false}],['wrong sum',{total:41000}],['wrong balance',{balanceAfter:99000}],['protected funds spent',{reserve:89000}],['bad identifier',{id:'<script>'}],['empty lines',{lines:[]}]])test('invalid receipt cannot navigate: '+name,()=>{assert.equal(validReceipt({...receipt,...patch}),false);assert.throws(()=>receiptPath({...receipt,...patch}));});
test('canonical QR destination is Vercel',()=>assert.equal(VERCEL_URL,'https://otgyeoljae.vercel.app/'));
