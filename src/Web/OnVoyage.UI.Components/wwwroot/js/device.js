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

// ---- Reading a story aloud with the browser's own voice (Web Speech API), for the stories published without audio.
// The text never leaves the device: only voices that run locally (localService) are used, because a network voice (some "Google" voices of
// Chrome) would send the text to a third party. One piece of text at a time; .NET cuts the story into pieces and drives pause and speed.
let speaking = null;

function localVoice(lang) {
    const wanted = (lang || "fr").toLowerCase();
    return window.speechSynthesis.getVoices().find((v) => v.localService && v.lang.toLowerCase().startsWith(wanted)) || null;
}

// Resolves true when the browser has a local voice for the language; waits a moment for the list to load (Chrome loads it late).
export function localVoiceAvailable(lang) {
    return new Promise((resolve) => {
        if (!("speechSynthesis" in window) || typeof SpeechSynthesisUtterance !== "function") return resolve(false);
        if (window.speechSynthesis.getVoices().length > 0) return resolve(localVoice(lang) !== null);
        const timer = setTimeout(() => resolve(localVoice(lang) !== null), 1500);
        window.speechSynthesis.addEventListener("voiceschanged", () => { clearTimeout(timer); resolve(localVoice(lang) !== null); }, { once: true });
    });
}

// Resolves "ended" when the piece was read to the end, "cancelled" when cancelSpeech() or a newer piece stopped it; rejects on a real error.
export function speak(text, lang, rate) {
    cancelSpeech();
    return new Promise((resolve, reject) => {
        const voice = localVoice(lang);
        if (!voice) return reject(new Error("no local voice for " + lang));
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.voice = voice;
        utterance.lang = voice.lang;
        utterance.rate = rate;
        const mine = { resolve };
        speaking = mine;
        utterance.onend = () => { if (speaking === mine) { speaking = null; resolve("ended"); } };
        utterance.onerror = (event) => {
            if (speaking !== mine) return;
            speaking = null;
            if (event.error === "canceled" || event.error === "interrupted") resolve("cancelled");
            else reject(new Error(event.error));
        };
        // Chrome drops a piece spoken in the same tick as a cancel().
        setTimeout(() => { if (speaking === mine) window.speechSynthesis.speak(utterance); }, 0);
    });
}

export function cancelSpeech() {
    if (speaking) {
        const previous = speaking;
        speaking = null;
        previous.resolve("cancelled");
    }
    if ("speechSynthesis" in window) window.speechSynthesis.cancel();
}
