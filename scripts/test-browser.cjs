const assert = require("node:assert/strict");
const { chromium } = require("playwright");

(async () => {
    const base = process.env.APP_URL || "http://app:8080";
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on("pageerror", (error) => errors.push(error.message));
        // First startup may still be restoring NuGet packages and building.
        let healthy = false;
        for (let attempt = 0; attempt < 90; attempt++) {
            try {
                const response = await page.request.get(`${base}/api/health`, {
                    timeout: 2000,
                });
                if (response.ok()) {
                    healthy = true;
                    break;
                }
            } catch {
                /* Server is still starting. */
            }
            await new Promise((resolve) => setTimeout(resolve, 1000));
        }
        assert.ok(healthy, "ASP.NET Core did not become healthy");
        await page.goto(`${base}/smoke`);
        await page
            .getByRole("status")
            .filter({ hasText: "C++ and Rust WebAssembly workers ready." })
            .waitFor({ timeout: 60000 });
        for (const language of ["C++", "Rust"]) {
            for (const [text, words] of [
                ["one two\nthree", 3],
                ["Привіт світе", 2],
                ["", 0],
            ]) {
                await page.getByLabel("Document", { exact: true }).fill(text);
                await page
                    .getByRole("button", {
                        name: `Process with ${language}`,
                        exact: true,
                    })
                    .click();
                await page
                    .getByRole("status")
                    .filter({ hasText: `${language} counted ${words} words.` })
                    .waitFor();
            }
        }
        assert.deepEqual(
            errors,
            [],
            "Browser reported uncaught JavaScript errors",
        );
        console.log(
            "Browser integration passed: Blazor → workers → C++ and Rust WASM, including UTF-8 and empty input.",
        );
    } finally {
        await browser.close();
    }
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
