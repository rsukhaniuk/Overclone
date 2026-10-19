// Browser API glue only: optional GIS consent and Google Picker. No Drive REST,
// archive logic, storage, logging, token URLs, refresh or startup side effects.
const scope = "https://www.googleapis.com/auth/drive.file";
const loads = new Map();
let generation = 0;
let stop = null;
export function disconnect() {
    generation++;
    if (stop) stop();
    stop = null;
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
            else { element.remove(); loads.delete(url); reject(new Error("optional-library")); }
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
    const started = generation;
    if (!navigator.onLine) return { code: "offline" };
    if (typeof clientId !== "string" || clientId.length > 256 ||
        !/^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$/.test(clientId)) return { code: "configuration" };
    try { await script("https://accounts.google.com/gsi/client"); }
    catch { return { code: "configuration" }; }
    if (started !== generation) return { code: "cancelled" };
    return new Promise(resolve => {
        let done = false;
        let client;
        const finish = (reply) => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            client = null;
            stop = null;
            resolve(reply);
        };
        const timer = setTimeout(() => finish({ code: "popup" }), 60000);
        stop = () => finish({ code: "cancelled" });
        try {
            client = google.accounts.oauth2.initTokenClient({
                client_id: clientId, scope, include_granted_scopes: false,
                callback: response => {
                    if (done) return;
                    if (!response || typeof response !== "object" || Array.isArray(response)) {
                        finish({ code: "configuration" }); return;
                    }
                    if (response.error) { finish({ code: "denied" }); return; }
                    const token = response.access_token;
                    const expiresIn = typeof response.expires_in === "string" || typeof response.expires_in === "number"
                        ? Number(response.expires_in) : NaN;
                    if (typeof token !== "string" || token.length < 1 || token.length > 8192 ||
                        !Number.isInteger(expiresIn) || expiresIn < 1 || expiresIn > 3600 || response.scope !== scope) {
                        finish({ code: "configuration" }); return;
                    }
                    finish({ code: "ready", token, scope, expiresIn });
                },
                error_callback: () => finish({ code: "popup" })
            });
            // Only this explicitly invoked method requests a token. No hidden sign-in,
            // automatic renewal, account hints, profile scopes, or refresh tokens.
            client.requestAccessToken({ prompt: "consent" });
        } catch { finish({ code: "configuration" }); }
    });
}
export async function chooseZip(apiKey, projectNumber, accessToken) {
    const started = generation;
    if (!navigator.onLine) { accessToken = null; return { code: "offline" }; }
    if (typeof apiKey !== "string" || !/^[A-Za-z0-9_-]{1,256}$/.test(apiKey) ||
        typeof projectNumber !== "string" || !/^[0-9]{1,30}$/.test(projectNumber) ||
        typeof accessToken !== "string" || accessToken.length < 1 || accessToken.length > 8192) {
        accessToken = null;
        return { code: "configuration" };
    }
    stop = () => { accessToken = null; };
    try {
        await script("https://apis.google.com/js/api.js");
        await new Promise((resolve, reject) => googleLoad(resolve, reject));
    } catch { accessToken = null; stop = null; return { code: "configuration" }; }
    if (started !== generation) { accessToken = null; return { code: "cancelled" }; }
    return new Promise(resolve => {
        let done = false;
        let picker;
        const finish = reply => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            if (picker) {
                try { picker.setVisible(false); picker.dispose(); } catch { /* optional library cleanup */ }
                picker = null;
            }
            accessToken = null;
            stop = null;
            resolve(reply);
        };
        const timer = setTimeout(() => finish({ code: "cancelled" }), 120000);
        stop = () => finish({ code: "cancelled" });
        try {
            const view = new google.picker.DocsView(google.picker.ViewId.DOCS)
                .setIncludeFolders(false).setSelectFolderEnabled(false)
                .setMimeTypes("application/zip,application/x-zip-compressed");
            picker = new google.picker.PickerBuilder()
                .setDeveloperKey(apiKey).setAppId(projectNumber).setOAuthToken(accessToken)
                .setOrigin(window.location.origin).addView(view)
                .setCallback(data => {
                    if (done) return;
                    if (!data || typeof data !== "object" || Array.isArray(data)) {
                        finish({ code: "configuration" }); return;
                    }
                    if (data.action === google.picker.Action.CANCEL) { finish({ code: "cancelled" }); return; }
                    if (data.action !== google.picker.Action.PICKED) return;
                    const files = data.docs;
                    if (!Array.isArray(files) || files.length !== 1) { finish({ code: "configuration" }); return; }
                    const file = files[0];
                    if (!file || typeof file !== "object" || typeof file.id !== "string" || !/^[A-Za-z0-9_-]{1,256}$/.test(file.id) ||
                        typeof file.name !== "string" || file.name.length > 256 || !/\.zip$/i.test(file.name)) {
                        finish({ code: "configuration" }); return;
                    }
                    finish({ code: "ready", id: file.id, name: file.name });
                }).build();
            accessToken = null;
            // A callback/cancellation can occur during build. Dispose the late
            // object instead of displaying a cancelled Picker.
            if (done || started !== generation) {
                try { picker.setVisible(false); picker.dispose(); } catch { /* optional library cleanup */ }
                picker = null;
                return;
            }
            picker.setVisible(true);
        } catch { finish({ code: "configuration" }); }
    });
}
function googleLoad(resolve, reject) {
    gapi.load("picker", { callback: resolve, onerror: reject, timeout: 20000, ontimeout: reject });
}
