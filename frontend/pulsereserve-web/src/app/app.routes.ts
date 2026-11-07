import { Routes } from '@angular/router';
import { adminGuard } from './core/auth/admin.guard';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Events | PulseReserve',
    loadComponent: () => import('./features/events/event-list/event-list').then((m) => m.EventList),
  },
  {
    path: 'events/:id',
    title: 'Event | PulseReserve',
    loadComponent: () =>
      import('./features/events/event-detail/event-detail').then((m) => m.EventDetail),
  },
  {
    path: 'account/orders',
    title: 'My orders | PulseReserve',
    canActivate: [authGuard],
    loadComponent: () => import('./features/account/order-list').then((m) => m.OrderList),
  },
  {
    path: 'account/orders/:id',
    title: 'Order | PulseReserve',
    canActivate: [authGuard],
    loadComponent: () => import('./features/account/order-detail').then((m) => m.OrderDetail),
  },
  {
    path: 'admin/payments',
    canActivate: [adminGuard],
    loadComponent: () =>
      import('./features/admin/payments/admin-layout').then((m) => m.AdminLayout),
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'Payment operations | PulseReserve',
        loadComponent: () => import('./features/admin/payments/overview').then((m) => m.Overview),
      },
      {
        path: 'reconciliation',
        title: 'Reconciliation | PulseReserve',
        loadComponent: () => import('./features/admin/payments/tasks').then((m) => m.Tasks),
      },
      {
        path: 'refunds',
        title: 'Refunds | PulseReserve',
        loadComponent: () => import('./features/admin/payments/refunds').then((m) => m.Refunds),
      },
      {
        path: 'exceptions',
        title: 'Payment exceptions | PulseReserve',
        loadComponent: () =>
          import('./features/admin/payments/exceptions').then((m) => m.Exceptions),
      },
      {
        path: 'anomalies',
        title: 'Payment anomalies | PulseReserve',
        loadComponent: () => import('./features/admin/payments/anomalies').then((m) => m.Anomalies),
      },
      {
        path: 'history',
        title: 'Financial history | PulseReserve',
        loadComponent: () => import('./features/admin/payments/history').then((m) => m.History),
      },
      {
        path: 'outbox',
        title: 'Outbox | PulseReserve',
        loadComponent: () => import('./features/admin/payments/outbox').then((m) => m.Outbox),
      },
      {
        path: 'audit',
        title: 'Audit log | PulseReserve',
        loadComponent: () => import('./features/admin/payments/audit').then((m) => m.Audit),
      },
    ],
  },
  {
    path: 'access-denied',
    title: 'Access denied | PulseReserve',
    loadComponent: () =>
      import('./features/access-denied/access-denied').then((m) => m.AccessDenied),
  },
  {
    path: 'login',
    title: 'Sign in | PulseReserve',
    loadComponent: () => import('./features/auth/login').then((m) => m.Login),
  },
  {
    path: 'register',
    title: 'Create account | PulseReserve',
    loadComponent: () => import('./features/auth/register').then((m) => m.Register),
  },
  {
    path: '**',
    title: 'Page not found | PulseReserve',
    loadComponent: () => import('./features/not-found/not-found').then((m) => m.NotFound),
  },
];
