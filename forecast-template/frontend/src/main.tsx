import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConfigProvider } from 'antd';
import ruRU from 'antd/locale/ru_RU';
import { detectMode } from './api/client';
import App from './App';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 } },
});

detectMode().then((modeInfo) => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <ConfigProvider locale={ruRU}>
        <QueryClientProvider client={queryClient}>
          <App modeInfo={modeInfo} />
        </QueryClientProvider>
      </ConfigProvider>
    </StrictMode>,
  );
});
