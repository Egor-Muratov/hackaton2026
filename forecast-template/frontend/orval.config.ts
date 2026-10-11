// Генерация клиента из контракта: npm run gen:api
// Пока используется рукописный src/api/client.ts (сеть медленная, orval мог не поставиться).
// Сгенерированное ложится в src/api/generated/ и может постепенно заменить types.ts.
export default {
  forecast: {
    input: '../docs/contracts/public-api.yaml',
    output: {
      target: 'src/api/generated/forecast.ts',
      schemas: 'src/api/generated/model',
      client: 'react-query',
      httpClient: 'fetch',
      mode: 'split',
      clean: true,
    },
  },
};
