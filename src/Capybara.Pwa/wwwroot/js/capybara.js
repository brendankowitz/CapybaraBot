// Browser interop for the Capybara PWA. Blazor owns all HTTP calls; these helpers only touch browser APIs.

window.capybaraStorage = {
  get: key => localStorage.getItem(key),
  set: (key, value) => localStorage.setItem(key, value),
};

window.capybaraDevice = {
  // Desktop-only actions (kill switch release, lifecycle, connection edits) are also enforced by the server; this only
  // decides whether to offer them.
  isDesktop: () => matchMedia('(pointer:fine) and (min-width: 900px)').matches,
};

// Reports whether the PWA has focus so the server suppresses push while it is in use (FR-CLI-07).
window.capybaraPresence = (() => {
  let handlers = null;
  return {
    start: ref => {
      if (handlers) {
        return;
      }
      const send = () => ref.invokeMethodAsync('SetFocused', document.visibilityState === 'visible' && document.hasFocus()).catch(() => {});
      handlers = [
        [document, 'visibilitychange', send],
        [window, 'focus', send],
        [window, 'blur', send],
      ];
      handlers.forEach(([target, name, fn]) => target.addEventListener(name, fn));
      send();
    },
    stop: () => {
      (handlers || []).forEach(([target, name, fn]) => target.removeEventListener(name, fn));
      handlers = null;
    },
  };
})();

// Web Push (FR-CLI-06). iOS needs 16.4+ and a Home Screen install; elsewhere the page points at the ntfy fallback.
window.capybaraPush = (() => {
  const toKey = base64Url => {
    const padding = '='.repeat((4 - (base64Url.length % 4)) % 4);
    const raw = atob((base64Url + padding).replace(/-/g, '+').replace(/_/g, '/'));
    return Uint8Array.from(raw, c => c.charCodeAt(0));
  };
  const registration = async () => (navigator.serviceWorker ? navigator.serviceWorker.ready : null);

  return {
    isSupported: () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window,

    permission: () => ('Notification' in window ? Notification.permission : 'unsupported'),

    // Subscribes with the gateway's VAPID public key and returns the subscription in the shape the server stores:
    // { endpoint, keys: { p256dh, auth } }. Returns null when permission is refused or push is unavailable.
    subscribe: async vapidPublicKey => {
      const reg = await registration();
      if (!reg || !('PushManager' in window)) {
        return null;
      }
      if ((await Notification.requestPermission()) !== 'granted') {
        return null;
      }
      const existing = await reg.pushManager.getSubscription();
      const subscription = existing || await reg.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: toKey(vapidPublicKey),
      });
      const json = subscription.toJSON();
      return { endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } };
    },

    // Removes the browser subscription and returns its endpoint so the server can drop it too.
    unsubscribe: async () => {
      const reg = await registration();
      const subscription = reg ? await reg.pushManager.getSubscription() : null;
      if (!subscription) {
        return null;
      }
      const endpoint = subscription.endpoint;
      await subscription.unsubscribe();
      return endpoint;
    },

    current: async () => {
      const reg = await registration();
      const subscription = reg ? await reg.pushManager.getSubscription() : null;
      return subscription ? subscription.endpoint : null;
    },
  };
})();

window.capybaraDownload = {
  fromStream: async (fileName, streamReference) => {
    const buffer = await streamReference.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: 'application/zip' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  },
};
