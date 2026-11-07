// Development-only proxy so the SPA can call the API same-origin without CORS.
// The backend has no launch profile, so `dotnet run --no-launch-profile` listens on Kestrel's
// default http://localhost:5000 (verified 2026-10-20 against the running API through this proxy).
// Override it with PULSERESERVE_API_URL if the API runs elsewhere.
const target = process.env.PULSERESERVE_API_URL ?? 'http://localhost:5000';

export default {
  '/api': {
    target,
    secure: false,
    changeOrigin: false,
    logLevel: 'warn',
  },
  // The real-time hub (SignalR). `ws: true` lets the WebSocket upgrade through. `changeOrigin` stays false on purpose: the hub
  // refuses a browser whose Origin differs from the Host it arrives for, and the dev server's own host is what the browser sent.
  '/hubs': {
    target,
    secure: false,
    changeOrigin: false,
    ws: true,
    logLevel: 'warn',
  },
};
