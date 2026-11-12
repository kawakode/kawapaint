// KawaPaint - browser smoke test for the published web build.
//
//   node smoke.mjs <path to the published wwwroot>
//
// The .NET test suite cannot see what only exists in a browser: the WebAssembly start-up, and
// the JavaScript side of autosave (IndexedDB, Web Locks, the "unsaved changes" prompt). This
// loads the real page in Chromium and checks those end to end.

import { chromium } from 'playwright';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(process.argv[2] ?? '.');
const shots = path.resolve('screenshots');
fs.mkdirSync(shots, { recursive: true });

const types = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
    '.json': 'application/json', '.wasm': 'application/wasm', '.ico': 'image/x-icon', '.png': 'image/png'
};
const server = http.createServer((request, response) => {
    const name = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const file = path.join(root, name === '/' ? 'index.html' : name);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
        response.writeHead(404).end();
        return;
    }
    response.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
    fs.createReadStream(file).pipe(response);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;

const failures = [];
const check = (condition, message) => {
    console.log(`${condition ? 'PASS' : 'FAIL'} - ${message}`);
    if (!condition) failures.push(message);
};

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const pageErrors = [];

async function openApp(label) {
    const page = await context.newPage();
    page.on('pageerror', error => pageErrors.push(`${label}: ${error.message}`));
    page.on('console', message => { if (message.type() === 'error') console.log(`  [${label} console] ${message.text()}`); });
    await page.goto(url);
    // Avalonia adds its canvas beside the splash once the runtime is up (see main.js).
    await page.waitForFunction(() => document.getElementById('out').children.length > 1, null, { timeout: 180_000 });
    await page.waitForTimeout(4000);   // first layout and paint of the app itself
    return page;
}

// Session ids of the snapshots in IndexedDB, read the way recovery.js stores them.
const snapshots = page => page.evaluate(() => new Promise((resolve, reject) => {
    const request = indexedDB.open('kawapaint-recovery', 1);
    request.onupgradeneeded = () => { request.result.createObjectStore('meta'); request.result.createObjectStore('data'); };
    request.onerror = () => reject(request.error);
    request.onsuccess = () => {
        const database = request.result;
        const keys = database.transaction('meta', 'readonly').objectStore('meta').getAllKeys();
        keys.onsuccess = () => { database.close(); resolve(keys.result.map(String)); };
        keys.onerror = () => { database.close(); reject(keys.error); };
    };
}));

async function until(read, accept, timeout = 30_000) {
    const deadline = Date.now() + timeout;
    let value = await read();
    while (!accept(value) && Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 500));
        value = await read();
    }
    return value;
}

try {
    // ---- the app starts ---------------------------------------------------
    const first = await openApp('first visit');
    await first.screenshot({ path: path.join(shots, '1-started.png') });
    check(pageErrors.length === 0, 'the app starts without a page error');
    check((await snapshots(first)).length === 0, 'a fresh visit has no recovery snapshot');

    // ---- a stroke makes the document unsaved, and hiding the tab snapshots it
    await first.mouse.move(600, 420);
    await first.mouse.down();
    await first.mouse.move(700, 470, { steps: 12 });
    await first.mouse.up();
    await first.waitForTimeout(500);
    await first.screenshot({ path: path.join(shots, '2-after-stroke.png') });

    await first.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { get: () => 'hidden', configurable: true });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    const saved = await until(() => snapshots(first), ids => ids.length === 1);
    check(saved.length === 1, 'hiding the tab after drawing writes a recovery snapshot');

    // ---- the page asks before it is closed with unsaved work --------------
    let asked = false;
    first.on('dialog', dialog => { asked = asked || dialog.type() === 'beforeunload'; dialog.accept(); });
    await first.close({ runBeforeUnload: true });
    await new Promise(resolve => setTimeout(resolve, 1500));
    check(asked, 'closing the tab with unsaved work asks first');

    // ---- the next visit offers the work back and restores it --------------
    const second = await openApp('second visit');
    await second.screenshot({ path: path.join(shots, '3-recovery-prompt.png') });
    check((await snapshots(second)).join() === saved.join(), 'the snapshot is still there on the next visit');

    // Restore is the prompt's default button. Restoring re-saves the document under the new
    // session and lets go of the old snapshot, so the stored id changes.
    await second.keyboard.press('Enter');
    const restored = await until(() => snapshots(second), ids => ids.length === 1 && ids[0] !== saved[0]);
    await second.screenshot({ path: path.join(shots, '4-restored.png') });
    check(restored.length === 1 && restored[0] !== saved[0], 'Restore loads the snapshot and re-saves it for the new session');
    check(pageErrors.length === 0, `no page error during the run${pageErrors.length ? ': ' + pageErrors.join(' | ') : ''}`);
} catch (error) {
    failures.push(`the run stopped: ${error.message}`);
    console.error(error);
} finally {
    await browser.close();
    server.close();
}

if (failures.length > 0) {
    console.error(`\n${failures.length} check(s) failed:\n - ${failures.join('\n - ')}`);
    process.exit(1);
}
console.log('\nWEB SMOKE OK');
