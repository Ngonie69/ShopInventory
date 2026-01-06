// Shop Inventory Service Worker
const CACHE_NAME = 'shop-inventory-v1';
const OFFLINE_URL = '/offline.html';

// Assets to cache on install
const STATIC_ASSETS = [
    '/',
    '/offline.html',
    '/css/app.css',
    '/css/bootstrap/bootstrap.min.css',
    '/manifest.json',
    '/images/icons/icon-192x192.png',
    '/images/icons/icon-512x512.png'
];

// API endpoints that should be cached with network-first strategy
const API_CACHE_PATTERNS = [
    '/api/product',
    '/api/stock',
    '/api/price'
];

// Install event - cache static assets
self.addEventListener('install', (event) => {
    console.log('[SW] Install');
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => {
                console.log('[SW] Caching static assets');
                return cache.addAll(STATIC_ASSETS);
            })
            .then(() => self.skipWaiting())
            .catch((error) => {
                console.error('[SW] Cache install failed:', error);
            })
    );
});

// Activate event - clean up old caches
self.addEventListener('activate', (event) => {
    console.log('[SW] Activate');
    event.waitUntil(
        caches.keys()
            .then((cacheNames) => {
                return Promise.all(
                    cacheNames
                        .filter((name) => name !== CACHE_NAME)
                        .map((name) => {
                            console.log('[SW] Deleting old cache:', name);
                            return caches.delete(name);
                        })
                );
            })
            .then(() => self.clients.claim())
    );
});

// Fetch event - handle requests
self.addEventListener('fetch', (event) => {
    const { request } = event;
    const url = new URL(request.url);

    // Skip non-GET requests
    if (request.method !== 'GET') {
        return;
    }

    // Skip chrome-extension and other non-http(s) requests
    if (!url.protocol.startsWith('http')) {
        return;
    }

    // Handle API requests with network-first strategy
    if (isApiRequest(url.pathname)) {
        event.respondWith(networkFirstStrategy(request));
        return;
    }

    // Handle navigation requests
    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request)
                .catch(() => {
                    return caches.match(OFFLINE_URL);
                })
        );
        return;
    }

    // Handle static assets with cache-first strategy
    event.respondWith(cacheFirstStrategy(request));
});

// Network-first strategy for API requests
async function networkFirstStrategy(request) {
    try {
        const networkResponse = await fetch(request);

        // Cache successful GET responses
        if (networkResponse.ok) {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, networkResponse.clone());
        }

        return networkResponse;
    } catch (error) {
        console.log('[SW] Network failed, trying cache:', request.url);
        const cachedResponse = await caches.match(request);

        if (cachedResponse) {
            return cachedResponse;
        }

        // Return offline response for API requests
        return new Response(
            JSON.stringify({ error: 'Offline', message: 'You are currently offline' }),
            {
                status: 503,
                headers: { 'Content-Type': 'application/json' }
            }
        );
    }
}

// Cache-first strategy for static assets
async function cacheFirstStrategy(request) {
    const cachedResponse = await caches.match(request);

    if (cachedResponse) {
        return cachedResponse;
    }

    try {
        const networkResponse = await fetch(request);

        // Cache successful responses
        if (networkResponse.ok) {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, networkResponse.clone());
        }

        return networkResponse;
    } catch (error) {
        console.error('[SW] Fetch failed:', error);
        return new Response('Offline', { status: 503 });
    }
}

// Check if request is an API request
function isApiRequest(pathname) {
    return API_CACHE_PATTERNS.some(pattern => pathname.startsWith(pattern));
}

// Handle background sync for offline operations
self.addEventListener('sync', (event) => {
    console.log('[SW] Background sync:', event.tag);

    if (event.tag === 'sync-invoices') {
        event.waitUntil(syncOfflineInvoices());
    } else if (event.tag === 'sync-payments') {
        event.waitUntil(syncOfflinePayments());
    }
});

// Sync offline invoices
async function syncOfflineInvoices() {
    try {
        const db = await openDatabase();
        const offlineInvoices = await getOfflineData(db, 'invoices');

        for (const invoice of offlineInvoices) {
            try {
                const response = await fetch('/api/invoice', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(invoice.data)
                });

                if (response.ok) {
                    await removeOfflineData(db, 'invoices', invoice.id);
                    console.log('[SW] Invoice synced:', invoice.id);
                }
            } catch (error) {
                console.error('[SW] Failed to sync invoice:', error);
            }
        }
    } catch (error) {
        console.error('[SW] Sync failed:', error);
    }
}

// Sync offline payments
async function syncOfflinePayments() {
    // Similar to syncOfflineInvoices
    console.log('[SW] Syncing offline payments');
}

// IndexedDB helpers
function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open('ShopInventoryOffline', 1);

        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result);

        request.onupgradeneeded = (event) => {
            const db = event.target.result;
            if (!db.objectStoreNames.contains('invoices')) {
                db.createObjectStore('invoices', { keyPath: 'id', autoIncrement: true });
            }
            if (!db.objectStoreNames.contains('payments')) {
                db.createObjectStore('payments', { keyPath: 'id', autoIncrement: true });
            }
        };
    });
}

function getOfflineData(db, storeName) {
    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.getAll();

        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result);
    });
}

function removeOfflineData(db, storeName, id) {
    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, 'readwrite');
        const store = transaction.objectStore(storeName);
        const request = store.delete(id);

        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve();
    });
}

// Push notification handling
self.addEventListener('push', (event) => {
    console.log('[SW] Push received');

    let data = { title: 'Shop Inventory', body: 'New notification' };

    if (event.data) {
        try {
            data = event.data.json();
        } catch (e) {
            data.body = event.data.text();
        }
    }

    const options = {
        body: data.body,
        icon: '/images/icons/icon-192x192.png',
        badge: '/images/icons/icon-72x72.png',
        vibrate: [100, 50, 100],
        data: {
            dateOfArrival: Date.now(),
            url: data.url || '/'
        },
        actions: [
            { action: 'view', title: 'View' },
            { action: 'dismiss', title: 'Dismiss' }
        ]
    };

    event.waitUntil(
        self.registration.showNotification(data.title, options)
    );
});

// Notification click handling
self.addEventListener('notificationclick', (event) => {
    console.log('[SW] Notification clicked:', event.action);

    event.notification.close();

    if (event.action === 'dismiss') {
        return;
    }

    const url = event.notification.data?.url || '/';

    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then((clientList) => {
                // Focus existing window if available
                for (const client of clientList) {
                    if (client.url.includes(url) && 'focus' in client) {
                        return client.focus();
                    }
                }
                // Open new window
                if (clients.openWindow) {
                    return clients.openWindow(url);
                }
            })
    );
});

console.log('[SW] Service Worker loaded');
