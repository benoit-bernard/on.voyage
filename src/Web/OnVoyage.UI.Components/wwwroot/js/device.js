// Browser position and screen wake lock for the PWA. Positions go to .NET and nowhere else.
let watchId = null;
let lock = null;

// Only answers when the permission was already granted (by the discovery button): opening the home page never triggers the prompt.
export async function currentPosition() {
    try {
        const status = navigator.permissions && (await navigator.permissions.query({ name: "geolocation" }));
        if (!status || status.state !== "granted") return null;
    } catch { return null; }
    return new Promise((resolve) => {
        if (!navigator.geolocation) return resolve(null);
        navigator.geolocation.getCurrentPosition(
            (p) => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
            () => resolve(null),
            { enableHighAccuracy: false, maximumAge: 60000, timeout: 8000 });
    });
}

// Resolves true once the first fix or the permission answer arrives; false when refused or unsupported.
export function startWatch(reference) {
    return new Promise((resolve) => {
        if (!navigator.geolocation) return resolve(false);
        let answered = false;
        watchId = navigator.geolocation.watchPosition(
            (p) => {
                if (!answered) { answered = true; resolve(true); }
                const c = p.coords;
                reference.invokeMethodAsync("OnFix", c.latitude, c.longitude, c.accuracy,
                    Number.isFinite(c.speed) ? c.speed : null, Number.isFinite(c.heading) ? c.heading : null, p.timestamp);
            },
            (e) => { if (!answered) { answered = true; resolve(false); } },
            { enableHighAccuracy: true, maximumAge: 1000, timeout: 20000 });
    });
}

export function stopWatch() {
    if (watchId !== null) navigator.geolocation.clearWatch(watchId);
    watchId = null;
}

export async function setAwake(awake) {
    try {
        if (awake && "wakeLock" in navigator && !lock) {
            lock = await navigator.wakeLock.request("screen");
            lock.addEventListener("release", () => { lock = null; });
        } else if (!awake && lock) {
            await lock.release();
            lock = null;
        }
    } catch { /* the browser may refuse (battery saver); discovery still works with the screen free to dim */ }
}
