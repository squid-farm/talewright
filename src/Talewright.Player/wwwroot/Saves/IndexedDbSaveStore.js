function storageError(error) {
    const messages = {
        QuotaExceededError: "There is not enough browser storage to save this game. Free some space and try again.",
        SecurityError: "Browser storage is blocked. Allow this site to store saved games and try again.",
        ConstraintError: "A save with this name already exists. Refresh saved games and confirm overwrite to replace it.",
        AbortError: "The save operation was interrupted. Your previous save has not been replaced. Try again.",
    };
    return new Error(messages[error?.name] ?? "Browser storage could not complete the save operation. Check your storage settings and try again.", { cause: error });
}

async function open() {
    if (!globalThis.indexedDB) {
        throw new Error("IndexedDB is unavailable. Enable browser storage to save this story.");
    }
    return await new Promise((resolve, reject) => {
        const request = indexedDB.open("talewright-saves", 1);
        let rejected = false;
        request.onupgradeneeded = () => {
            const store = request.result.createObjectStore("saves", { keyPath: ["storyId", "slot"] });
            store.createIndex("storyId", "storyId");
        };
        request.onerror = () => reject(storageError(request.error));
        request.onblocked = () => {
            rejected = true;
            reject(new Error("Save storage is blocked by another open player. Close it and try again."));
        };
        request.onsuccess = () => {
            if (rejected) {
                request.result.close();
            } else {
                resolve(request.result);
            }
        };
    });
}

async function execute(mode, operation) {
    let database;
    try {
        database = await open();
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction("saves", mode);
            const request = operation(transaction.objectStore("saves"));
            transaction.oncomplete = () => resolve(request.result);
            transaction.onerror = () => reject(storageError(transaction.error ?? request.error));
            transaction.onabort = () => reject(storageError(transaction.error ?? request.error));
        });
    } catch (error) {
        if (error instanceof DOMException) {
            throw storageError(error);
        }
        throw error;
    } finally {
        database?.close();
    }
}

export async function list(storyId) {
    const keys = await execute("readonly", store => store.index("storyId").getAllKeys(IDBKeyRange.only(storyId)));
    return keys.map(key => key[1]).sort();
}

export async function read(storyId, slot) {
    const record = await execute("readonly", store => store.get([storyId, slot]));
    if (!record) {
        throw new Error(`Save '${slot}' does not exist.`);
    }
    if (!(record.data instanceof Uint8Array) || record.data.length === 0 || record.data.length > 2 * 1024 * 1024) {
        throw new Error(`Save '${slot}' contains invalid or oversized data.`);
    }
    return record.data;
}

export async function write(storyId, slot, data, overwrite) {
    if (!(data instanceof Uint8Array) || data.length === 0 || data.length > 2 * 1024 * 1024) {
        throw new Error("This saved game is empty or too large to store (maximum 2 MiB). Try another save.");
    }
    const record = { storyId, slot, data };
    await execute("readwrite", store => overwrite ? store.put(record) : store.add(record));
}
