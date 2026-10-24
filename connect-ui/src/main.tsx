import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

// Trial looks, dev server only (see style-lab/lab.ts).
if (import.meta.env.DEV) void import('./style-lab/lab');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);