const button = document.getElementById("refresh-release");
const status = document.getElementById("refresh-status");
const workers = navigator.serviceWorker;
const scope = new URL("/", location.href).href;
const script = new URL("/service-worker.js", location.href).href;

if (workers) button.disabled = false;
else status.textContent = "This browser does not provide access to service workers.";

async function publishedVersion() {
    // The query bypasses old navigation caches. Read the SDK manifest as data.
    const response = await fetch("/service-worker-assets.js?release-recovery=2", {
        cache: "no-store", mode: "same-origin", redirect: "error",
        signal: AbortSignal.timeout(10000),
    });
    if (!response.ok) throw new Error("The release manifest is unavailable.");
    const manifest = /^self\.assetsManifest\s*=\s*\{\s*"version"\s*:\s*"([A-Za-z0-9+/_=-]{1,128})"/
        .exec(await response.text());
    if (!manifest) throw new Error("The release manifest has no valid build number.");
    return manifest[1];
}

function ask(worker, version, activate) {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => finish(null), 2000);
        function finish(result) {
            clearTimeout(timer);
            channel.port1.close();
            channel.port2.close();
            resolve(result);
        }
        channel.port1.onmessage = event => finish(event.data);
        try { worker.postMessage({ type: "release-recovery", version, activate }, [channel.port2]); }
        catch { finish(null); }
    });
}

async function waitForPublishedWorker(registration, version) {
    const deadline = Date.now() + 120000;
    let checkedController, requestedWorker;
    while (Date.now() < deadline) {
        const controller = workers.controller;
        if (controller?.state === "activated" && controller.scriptURL === script && controller !== checkedController) {
            checkedController = controller;
            if (await ask(controller, version, false) === "current") return "current";
        }

        const waiting = registration.waiting;
        if (waiting?.scriptURL === script && waiting !== requestedWorker) {
            requestedWorker = waiting;
            const result = await ask(waiting, version, true);
            if (result === "other-windows") return result;
            if (result === "activating") status.textContent = `Activating build ${version}…`;
        }
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    throw new Error(`Build ${version} did not take control. Reopen this refresh page and try again.`);
}

button.addEventListener("click", async () => {
    button.disabled = true;
    status.textContent = "Checking the published build…";
    try {
        const version = await publishedVersion();
        const existing = await workers.getRegistration(scope);
        if (existing && (existing.scope !== scope ||
            [existing.active, existing.waiting, existing.installing].some(worker => worker && worker.scriptURL !== script))) {
            throw new Error("The registered worker does not match Overclone’s worker.");
        }

        status.textContent = `Preparing build ${version}…`;
        const registration = await workers.register(script, { scope, updateViaCache: "none" });
        await registration.update();
        if (await waitForPublishedWorker(registration, version) === "other-windows") {
            status.textContent = "Close the other Overclone tabs or app windows, then click Open current build again.";
            return;
        }

        status.textContent = `Build ${version} is active. Opening Overclone…`;
        location.replace(scope);
    } catch (error) {
        status.textContent = error.message || "Could not refresh Overclone.";
    } finally {
        button.disabled = false;
    }
});
