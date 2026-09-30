// Offline-first shell. API calls (/api/) are never cached: catalog data always comes from the Gateway.
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'onvoyage-shell-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.svg$/, /\.webmanifest$/, /\.blat$/, /\.dat$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

async function onInstall(event) {
  self.skipWaiting();
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
  await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate(event) {
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys.filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName).map(key => caches.delete(key)));
}

async function onFetch(event) {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.pathname.startsWith('/api/')) {
    return fetch(event.request);
  }
  const shouldServeIndexHtml = event.request.mode === 'navigate';
  const request = shouldServeIndexHtml ? 'index.html' : event.request;
  const cache = await caches.open(cacheName);
  return (await cache.match(request)) || fetch(event.request);
}
