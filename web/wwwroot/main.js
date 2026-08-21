import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

// The splash text is position:absolute, so browsers paint it above the plain (non-positioned)
// <canvas> Avalonia inserts into #out, regardless of DOM order - it would otherwise cover a
// perfectly working app forever. Hide it as soon as Avalonia adds anything to #out.
const out = document.getElementById("out");
if (out) {
    const observer = new MutationObserver(() => {
        if (out.children.length > 1) {
            const splash = out.querySelector(".kawapaint-splash");
            if (splash) splash.style.display = "none";
            observer.disconnect();
        }
    });
    observer.observe(out, { childList: true });
}

try {
    const dotnetRuntime = await dotnet
        .withDiagnosticTracing(false)
        .withApplicationArgumentsFromQuery()
        .create();

    const config = dotnetRuntime.getConfig();

    await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (err) {
    console.error("KawaPaint failed to start:", err);
    if (out) {
        out.innerHTML = '<pre style="color:#f66;padding:16px;white-space:pre-wrap;font-family:monospace">' +
            (err && err.stack ? err.stack : String(err)) + '</pre>';
    }
}
