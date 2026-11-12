// KawaPaint - recovery snapshots in the browser (see BrowserRecoveryStore.cs).
//
// The page has no filesystem, so a snapshot of the open document goes to IndexedDB, which has
// room for images where localStorage does not. Snapshots are small records in "meta" and the
// project itself (base64) in "data", so listing them never loads an image.
//
// Several tabs can have KawaPaint open at once. Each tab holds a Web Lock named after itself for
// as long as it lives; a snapshot whose tab no longer holds its lock is a leftover from a tab
// that was closed, crashed or reloaded, and only those are offered back.

const DATABASE = "kawapaint-recovery";
const META = "meta";
const DATA = "data";

const tabId = globalThis.crypto && globalThis.crypto.randomUUID
    ? globalThis.crypto.randomUUID()
    : Date.now().toString(36) + Math.random().toString(36).slice(2);
const lockName = tab => "kawapaint-tab-" + tab;

const locks = globalThis.navigator && globalThis.navigator.locks;
if (locks) {
    // Never resolves: the lock is released by the browser when the tab goes away.
    locks.request(lockName(tabId), () => new Promise(() => { })).catch(() => { });
}

function open() {
    return new Promise((resolve, reject) => {
        const request = globalThis.indexedDB.open(DATABASE, 1);
        request.onupgradeneeded = () => {
            request.result.createObjectStore(META);
            request.result.createObjectStore(DATA);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error("The recovery database is blocked by another tab."));
    });
}

// Runs `work` in one transaction over both stores and resolves with what it returned once the
// transaction has committed.
async function transact(mode, work) {
    const database = await open();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction([META, DATA], mode);
            const result = work(transaction.objectStore(META), transaction.objectStore(DATA));
            transaction.oncomplete = () => resolve(result.value);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error || new Error("The recovery transaction was aborted."));
        });
    } finally {
        database.close();
    }
}

// Wraps a request so its result can be read after the transaction completes.
function later(request) {
    const holder = { value: undefined };
    if (request) request.onsuccess = () => { holder.value = request.result; };
    return holder;
}

async function liveTabs() {
    if (!locks || !locks.query) return null;   // cannot tell which tabs are alive
    const state = await locks.query();
    return new Set((state.held || []).map(lock => lock.name));
}

let unsaved = false;
globalThis.addEventListener("beforeunload", event => {
    if (!unsaved) return;
    // Makes the browser ask before the tab is closed or reloaded with unsaved work in it.
    event.preventDefault();
    event.returnValue = "";
});

export const recovery = {
    save: (sessionId, data) => transact("readwrite", (meta, store) => {
        meta.put({ tab: tabId, written: Date.now() }, sessionId);
        return later(store.put(data, sessionId));
    }).then(() => { }),

    // JSON array of { id, written } for the snapshots whose tab is gone.
    leftovers: async () => {
        const held = await liveTabs();
        const [keys, records] = await transact("readonly", meta => {
            const keysRequest = later(meta.getAllKeys());
            const recordsRequest = later(meta.getAll());
            return { get value() { return [keysRequest.value || [], recordsRequest.value || []]; } };
        });
        const found = [];
        keys.forEach((id, index) => {
            const record = records[index] || {};
            if (record.tab === tabId) return;
            if (held && held.has(lockName(record.tab))) return;
            found.push({ id: String(id), written: record.written || 0 });
        });
        return JSON.stringify(found);
    },

    // The project as base64, or null when it is gone.
    load: sessionId => transact("readonly", (meta, store) => later(store.get(sessionId)))
        .then(data => typeof data === "string" ? data : null),

    discard: sessionId => transact("readwrite", (meta, store) => {
        meta.delete(sessionId);
        return later(store.delete(sessionId));
    }).then(() => { }),

    setUnsaved: value => { unsaved = !!value; }
};
