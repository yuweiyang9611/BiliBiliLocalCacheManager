import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { RenderBoundary } from './components/RenderBoundary';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RenderBoundary scope="app"><App /></RenderBoundary>
  </StrictMode>,
);
