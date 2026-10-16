// Probes what a Chrome does with a profile another Chrome version wrote, the way the OpenWA gateway
// opens one: headless, through OpenWA's own puppeteer, with its production launch arguments.
// Found 2026-10-16: Chrome for Testing 146 deletes every IndexedDB that Google Chrome 155 wrote
// ("unknown schema"), WhatsApp Web's login included; localStorage, cookies and service workers survive.
// See docs/operations/whatsapp-production.md, "The gateway's Chrome".
//
//   node Probe-ChromeProfileDowngrade.js <OpenWA root> <newer chrome.exe> <short profile dir> write w
//   copy the profile, then:
//   node Probe-ChromeProfileDowngrade.js <OpenWA root> <older chrome.exe> <same profile dir> read r
//
// Keep the profile path short: under a deep folder Chrome cannot create its files (MAX_PATH) and
// IndexedDB fails to open for that reason instead. Needs web.whatsapp.com reachable for 'write'.
const [, , openwaRoot, chromeExe, profileDir, mode, label] = process.argv;
const path = require('path');
const fs = require('fs');
const http = require('http');
const puppeteer = require(path.join(openwaRoot, 'node_modules', 'puppeteer'));

const page_html = `<!doctype html><title>t</title><script>
window.writeAll = async () => {
  localStorage.setItem('k', 'written-by-' + navigator.userAgent.match(/Chrome\\/([\\d.]+)/)[1]);
  document.cookie = 'c=persist; max-age=31536000; path=/';
  await new Promise((res, rej) => { const r = indexedDB.open('probe', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('s');
    r.onsuccess = () => { const tx = r.result.transaction('s', 'readwrite'); tx.objectStore('s').put('idb-value', 'key'); tx.oncomplete = () => { r.result.close(); res(); }; tx.onerror = rej; };
    r.onerror = rej; });
  await navigator.serviceWorker.register('/sw.js'); await navigator.serviceWorker.ready;
  return 'ok';
};
window.readAll = async () => {
  const out = { localStorage: localStorage.getItem('k'), cookie: document.cookie };
  const dbs = await indexedDB.databases(); out.idbDatabases = dbs.map(d => d.name + '@' + d.version);
  out.idb = dbs.some(d => d.name === 'probe') ? await new Promise((res) => { const r = indexedDB.open('probe');
    r.onsuccess = () => { try { const g = r.result.transaction('s').objectStore('s').get('key'); g.onsuccess = () => res(g.result); g.onerror = () => res('get-error'); } catch (e) { res('tx-error ' + e.message); } };
    r.onerror = () => res('open-error ' + (r.error && r.error.message)); }) : null;
  out.serviceWorkers = (await navigator.serviceWorker.getRegistrations()).length;
  return out;
};
</script>`;

const server = http.createServer((req, res) => {
  if (req.url === '/sw.js') { res.writeHead(200, { 'content-type': 'text/javascript' }); return res.end('self.addEventListener("fetch",()=>{})'); }
  res.writeHead(200, { 'content-type': 'text/html' }); res.end(page_html);
});

(async () => {
  await new Promise(r => server.listen(47913, '127.0.0.1', r));
  const browser = await puppeteer.launch({
    executablePath: chromeExe, userDataDir: profileDir, headless: true, dumpio: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage', '--disable-gpu'],
  });
  const version = await browser.version();
  const result = { label, mode, version };
  try {
    const page = await browser.newPage();
    await page.goto('http://127.0.0.1:47913/', { waitUntil: 'load' });
    if (mode === 'write') {
      result.write = await page.evaluate(() => window.writeAll());
      await page.goto('https://web.whatsapp.com/', { waitUntil: 'domcontentloaded', timeout: 60000 });
      await new Promise(r => setTimeout(r, 25000));
    }
    result.localhost = await page.evaluate(() => window.readAll()).catch(async () => {
      await page.goto('http://127.0.0.1:47913/', { waitUntil: 'load' }); return page.evaluate(() => window.readAll());
    });
    // WhatsApp's origin, with its scripts kept off: answer the document request ourselves.
    const wa = await browser.newPage();
    await wa.setRequestInterception(true);
    wa.on('request', r => r.url().startsWith('https://web.whatsapp.com/') && r.resourceType() === 'document'
      ? r.respond({ status: 200, contentType: 'text/html', body: '<!doctype html><title>stub</title>' })
      : r.abort());
    await wa.goto('https://web.whatsapp.com/', { waitUntil: 'load' });
    result.whatsapp = await wa.evaluate(async () => ({
      idb: (await indexedDB.databases()).map(d => d.name + '@' + d.version).sort(),
      localStorageKeys: Object.keys(localStorage).sort(),
    }));
  } finally {
    await browser.close();
    server.close();
  }
  result.lastVersionFile = fs.existsSync(path.join(profileDir, 'Last Version')) ? fs.readFileSync(path.join(profileDir, 'Last Version'), 'utf8') : null;
  result.profileEntries = fs.readdirSync(path.dirname(profileDir)).filter(n => n.startsWith(path.basename(profileDir)));
  console.log('RESULT ' + JSON.stringify(result, null, 1));
})().catch(e => { console.error('FAILED', e); process.exit(1); });
