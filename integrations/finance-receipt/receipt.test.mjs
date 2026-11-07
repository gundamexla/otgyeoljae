import test from 'node:test';
import assert from 'node:assert/strict';
import {validate} from './receipt.js';
const r={version:1,id:'0123456789abcdef0123456789abcdef',item:'시연 바지',price:39000,shipping:3000,total:42000,balance:88000,reserve:70000,demo:true};
test('valid demo receipt has correct total and protected living expenses',()=>assert.equal(validate(r),true));
for(const [name,change] of [['wrong sum',{total:41000}],['negative price',{price:-1}],['fractional amount',{shipping:3000.5}],['spent living expenses',{balance:69999}],['not a demo',{demo:false}],['unbounded identifier',{id:'x'.repeat(100)}],['missing item',{item:''}]])test('reject '+name,()=>assert.equal(validate({...r,...change}),false));
