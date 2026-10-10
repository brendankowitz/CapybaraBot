// Capybara service worker: push display only. There is deliberately NO fetch handler, so API responses are never cached
// (they are authenticated and sensitive), and NO call to the API from here: acting on an approval needs the device token,
// which only the page holds. Notification payloads are minimal and untrusted, so parse defensively and always notify.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

function parsePayload(event) {
  try {
    const data = event.data ? event.data.json() : null;
    return data && typeof data === 'object' ? data : {};
  } catch {
    return {};
  }
}

// Only same-origin paths are ever opened; anything else becomes the app root.
function safePath(value, fallback) {
  try {
    const url = new URL(typeof value === 'string' ? value : fallback, self.location.origin);
    return url.origin === self.location.origin ? url.pathname + url.search : fallback;
  } catch {
    return fallback;
  }
}

self.addEventListener('push', event => {
  const data = parsePayload(event);
  const actions = Array.isArray(data.actions)
    ? data.actions.slice(0, 2).filter(a => a && typeof a.action === 'string' && typeof a.title === 'string').map(a => ({ action: a.action.slice(0, 32), title: a.title.slice(0, 40) }))
    : [];
  event.waitUntil(self.registration.showNotification(typeof data.title === 'string' ? data.title.slice(0, 100) : 'Capybara alert', {
    body: typeof data.body === 'string' ? data.body.slice(0, 200) : 'Open Capybara for details',
    data: { url: safePath(data.url, '/') },
    actions,
  }));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const data = event.notification.data || {};
  // Approval action buttons open the approval in the app, where the device token is available; nothing is decided here.
  const target = event.action ? safePath(data.url, '/approvals') : safePath(data.url, '/');
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const open = windows.find(w => new URL(w.url).origin === self.location.origin);
    if (open) {
      try {
        await open.navigate(target);
      } catch {
        // navigate can be refused; fall back to a new window below.
        await self.clients.openWindow(target);
        return;
      }
      await open.focus();
      return;
    }
    await self.clients.openWindow(target);
  })());
});
