import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@fontsource-variable/inter';
import App from './App';
import { AuthProvider } from './auth/AuthContext';
import { registerKamsoraChartTheme } from './charts/kamsoraTheme';
import './styles.css';

registerKamsoraChartTheme();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Auto-refresh aggregations every 15s so the dashboard feels live.
      refetchInterval: 15_000,
      refetchOnWindowFocus: true,
      retry: 1,
      staleTime: 5_000,
    },
  },
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <App />
        </QueryClientProvider>
      </AuthProvider>
    </BrowserRouter>
  </React.StrictMode>,
);
