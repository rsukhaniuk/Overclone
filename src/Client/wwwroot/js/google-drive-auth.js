// Browser API glue only: optional GIS consent and Google Picker. No Drive REST,
// archive logic, storage, logging, token URLs, refresh or startup side effects.
const scope = "https://www.googleapis.com/auth/drive.file";
const loads = new Map();
let generation = 0;
let active = null;
export function disconnect() {
    generation++;
    active?.finish({ code: "cancelled" });
}
// Cancellation settles the public gesture immediately, but an unresolved loader
// still owns the slot until callback/error/deadline. It cannot start another
// Google action or clear a later attempt's cleanup handler.
function attempt(work) {
    if (active) return Promise.resolve({ code: "busy" });
    const owned = {
        started: generation,
        done: false,
        loading: true,
        cleanup: () => {},
    };
    const reply = new Promise((resolve) => {
        owned.finish = (value) => {
            if (owned.done) return;
            owned.done = true;
            owned.cleanup();
            if (!owned.loading && active === owned) active = null;
            resolve(value);
        };
    });
    active = owned;
    void (async () => {
        try {
            await work(owned, reply);
        } catch {
            owned.loading = false;
            if (current(owned)) owned.finish({ code: "configuration" });
        } finally {
            owned.cleanup();
            if (active === owned) active = null;
        }
    })();
    return reply;
}
function current(owned) {
    if (owned.done) return false;
    if (owned.started !== generation) {
        owned.finish({ code: "cancelled" });
        return false;
    }
    if (!navigator.onLine) {
        owned.finish({ code: "offline" });
        return false;
    }
    return true;
}
function script(url) {
    if (loads.has(url)) return loads.get(url);
    const pending = new Promise((resolve, reject) => {
        const element = document.createElement("script");
        let settled = false;
        const finish = (ok) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            element.onload = element.onerror = null;
            if (ok) resolve();
            else {
                element.remove();
                loads.delete(url);
                reject(new Error("optional-library"));
            }
        };
        const timer = setTimeout(() => finish(false), 20000);
        element.async = true;
        element.src = url; // fixed public library URL, never user input or credentials
        element.onload = () => finish(true);
        element.onerror = () => finish(false);
        document.head.appendChild(element);
    });
    loads.set(url, pending);
    return pending;
}
export async function connect(clientId) {
    if (!navigator.onLine) return { code: "offline" };
    if (
        typeof clientId !== "string" ||
        clientId.length > 256 ||
        !/^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$/.test(clientId)
    )
        return { code: "configuration" };
    return attempt(async (owned, reply) => {
        await script("https://accounts.google.com/gsi/client");
        owned.loading = false;
        // Even a cached, fulfilled library promise yields: recheck before GIS.
        if (!current(owned)) return;
        let client;
        const timer = setTimeout(() => owned.finish({ code: "popup" }), 60000);
        owned.cleanup = () => {
            clearTimeout(timer);
            client = null;
        };
        client = google.accounts.oauth2.initTokenClient({
            client_id: clientId,
            scope,
            include_granted_scopes: false,
            callback: (response) => {
                if (!current(owned)) return;
                if (
                    !response ||
                    typeof response !== "object" ||
                    Array.isArray(response)
                ) {
                    owned.finish({ code: "configuration" });
                    return;
                }
                if (response.error) {
                    owned.finish({ code: "denied" });
                    return;
                }
                const token = response.access_token;
                const expiresIn =
                    typeof response.expires_in === "string" ||
                    typeof response.expires_in === "number"
                        ? Number(response.expires_in)
                        : NaN;
                if (
                    typeof token !== "string" ||
                    token.length < 1 ||
                    token.length > 8192 ||
                    !Number.isInteger(expiresIn) ||
                    expiresIn < 1 ||
                    expiresIn > 3600 ||
                    response.scope !== scope
                ) {
                    owned.finish({ code: "configuration" });
                    return;
                }
                owned.finish({ code: "ready", token, scope, expiresIn });
            },
            error_callback: () => {
                if (current(owned)) owned.finish({ code: "popup" });
            },
        });
        // Only this explicit method requests a token. No hidden sign-in, renewal,
        // account hints, profile scopes, or refresh tokens. GIS owns its popup.
        if (current(owned)) client.requestAccessToken({ prompt: "consent" });
        await reply;
    });
}
export async function chooseZip(apiKey, projectNumber, accessToken) {
    if (!navigator.onLine) {
        accessToken = null;
        return { code: "offline" };
    }
    if (
        typeof apiKey !== "string" ||
        !/^[A-Za-z0-9_-]{1,256}$/.test(apiKey) ||
        typeof projectNumber !== "string" ||
        !/^[0-9]{1,30}$/.test(projectNumber) ||
        typeof accessToken !== "string" ||
        accessToken.length < 1 ||
        accessToken.length > 8192
    ) {
        accessToken = null;
        return { code: "configuration" };
    }
    if (active) {
        accessToken = null;
        return { code: "busy" };
    }
    return attempt(async (owned, reply) => {
        // Installed before any await: disconnect removes this attempt's token
        // reference even while a Google loader is still unresolved.
        let picker;
        let timer;
        owned.cleanup = () => {
            clearTimeout(timer);
            accessToken = null;
            if (picker) {
                try {
                    picker.setVisible(false);
                    picker.dispose();
                } catch {
                    /* optional library cleanup */
                }
                picker = null;
            }
        };
        await script("https://apis.google.com/js/api.js");
        owned.loading = false;
        if (!current(owned)) return;
        owned.loading = true;
        await googleLoad();
        owned.loading = false;
        if (!current(owned)) return;
        timer = setTimeout(() => owned.finish({ code: "cancelled" }), 120000);
        const view = new google.picker.DocsView(google.picker.ViewId.DOCS)
            .setIncludeFolders(false)
            .setSelectFolderEnabled(false)
            .setMimeTypes("application/zip,application/x-zip-compressed");
        picker = new google.picker.PickerBuilder()
            .setDeveloperKey(apiKey)
            .setAppId(projectNumber)
            .setOAuthToken(accessToken)
            .setOrigin(window.location.origin)
            .addView(view)
            .setCallback((data) => {
                if (!current(owned)) return;
                if (!data || typeof data !== "object" || Array.isArray(data)) {
                    owned.finish({ code: "configuration" });
                    return;
                }
                if (data.action === google.picker.Action.CANCEL) {
                    owned.finish({ code: "cancelled" });
                    return;
                }
                if (data.action !== google.picker.Action.PICKED) return;
                const files = data.docs;
                if (!Array.isArray(files) || files.length !== 1) {
                    owned.finish({ code: "configuration" });
                    return;
                }
                const file = files[0];
                if (
                    !file ||
                    typeof file !== "object" ||
                    typeof file.id !== "string" ||
                    !/^[A-Za-z0-9_-]{1,256}$/.test(file.id) ||
                    typeof file.name !== "string" ||
                    file.name.length > 256 ||
                    !/\.zip$/i.test(file.name)
                ) {
                    owned.finish({ code: "configuration" });
                    return;
                }
                owned.finish({ code: "ready", id: file.id, name: file.name });
            })
            .build();
        accessToken = null;
        // A synchronous build callback can cancel before returning its object.
        // The attempt's finally disposes that late object, never a later Picker.
        if (!current(owned)) return;
        picker.setVisible(true);
        await reply;
    });
}
function googleLoad() {
    return new Promise((resolve, reject) => {
        let settled = false;
        const finish = (ok) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            if (ok) resolve();
            else reject(new Error("optional-library"));
        };
        // Own the deadline as well as gapi's documented timeout. A loader that
        // never invokes any callback cannot leave the operation slot stuck.
        const timer = setTimeout(() => finish(false), 20000);
        try {
            gapi.load("picker", {
                callback: () => finish(true),
                onerror: () => finish(false),
                timeout: 20000,
                ontimeout: () => finish(false),
            });
        } catch {
            finish(false);
        }
    });
}
