import {defineConfig} from 'vite';
import react from '@vitejs/plugin-react';
import {fileURLToPath} from 'node:url';
import {cpSync,mkdirSync} from 'node:fs';
const root=fileURLToPath(new URL('./web',import.meta.url));
const output=fileURLToPath(new URL('../public/mobile',import.meta.url));
export default defineConfig({root,base:'/mobile/',publicDir:false,plugins:[react(),{name:'moa-character-assets',closeBundle(){const dest=fileURLToPath(new URL('../public/moa',import.meta.url));mkdirSync(dest,{recursive:true});cpSync(fileURLToPath(new URL('./public/moa',import.meta.url)),dest,{recursive:true});}}],build:{outDir:output,emptyOutDir:true},server:{host:'127.0.0.1',port:5174,proxy:{'/api':'http://127.0.0.1:4401','/moa':'http://127.0.0.1:4401'}}});
