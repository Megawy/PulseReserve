import { InjectionToken, inject } from '@angular/core';

/** The small part of a hub connection the live service needs. Tests provide a fake; production wraps SignalR. */
export interface LiveConnectionLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T = void>(method: string, ...args: unknown[]): Promise<T>;
  on(method: string, handler: (body: unknown) => void): void;
  /** Called once when the connection is gone, whatever the reason (server abort, network, `stop()`). */
  onClose(handler: (error?: Error) => void): void;
}

export type LiveConnectionFactory = () => LiveConnectionLike;

/** Path of the hub. Same-origin, like the API: the dev server proxies `/hubs` (proxy.conf.mjs), including WebSockets. */
export const LIVE_HUB_URL = new InjectionToken<string>('LIVE_HUB_URL', {
  providedIn: 'root',
  factory: () => '/hubs/live',
});

/**
 * Production factory: SignalR over WebSockets, falling back to server-sent events and long polling where the network does not allow
 * them. The library is imported on first use so it is not part of the first page's bundle. Automatic reconnect is OFF on
 * purpose: reconnecting is the live service's job, because it must re-identify, re-subscribe and ask pages to re-read.
 * Stateful reconnect is not used (the server never offers it).
 */
export const LIVE_CONNECTION_FACTORY = new InjectionToken<LiveConnectionFactory>(
  'LIVE_CONNECTION_FACTORY',
  {
    providedIn: 'root',
    factory: () => {
      const url = inject(LIVE_HUB_URL);
      return () => createSignalRConnection(url);
    },
  },
);

function createSignalRConnection(url: string): LiveConnectionLike {
  // Created lazily so the module is only fetched when a connection is first started.
  let connection: import('@microsoft/signalr').HubConnection | null = null;
  const handlers: [string, (body: unknown) => void][] = [];
  let closeHandler: ((error?: Error) => void) | null = null;

  return {
    async start() {
      const signalr = await import('@microsoft/signalr');
      connection = new signalr.HubConnectionBuilder()
        .withUrl(url)
        .configureLogging(signalr.LogLevel.None) // nothing about connections or messages goes to the console
        .build();
      for (const [method, handler] of handlers) {
        connection.on(method, handler);
      }
      connection.onclose((error) => closeHandler?.(error));
      await connection.start();
    },
    async stop() {
      await connection?.stop();
    },
    invoke<T>(method: string, ...args: unknown[]): Promise<T> {
      if (!connection) {
        return Promise.reject(new Error('The live connection is not started.'));
      }
      return connection.invoke<T>(method, ...args);
    },
    on(method, handler) {
      handlers.push([method, handler]);
      connection?.on(method, handler);
    },
    onClose(handler) {
      closeHandler = handler;
    },
  };
}
