import {createRoot} from 'react-dom/client';
import Home from '../app/page';
import '../app/globals.css';
const setHeight=()=>{const v=window.visualViewport;const keyboard=v&&v.scale===1&&v.height<window.innerHeight-80;if(keyboard)document.documentElement.style.setProperty('--app-height',Math.round(v.height)+'px');else document.documentElement.style.removeProperty('--app-height');};
setHeight();window.addEventListener('resize',setHeight);window.visualViewport?.addEventListener('resize',setHeight);
createRoot(document.getElementById('root')!).render(<Home/>);
