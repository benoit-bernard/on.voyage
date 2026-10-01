// Browser audio for the PWA (the phone apps use the platform player). One <audio> element, driven from .NET.
let audio = null;
let dotnet = null;

export function init(reference) {
    dotnet = reference;
    audio = new Audio();
    audio.preload = "auto";
    audio.addEventListener("timeupdate", () => dotnet.invokeMethodAsync("OnPosition", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0));
    audio.addEventListener("ended", () => dotnet.invokeMethodAsync("OnEnded"));
    audio.addEventListener("error", () => dotnet.invokeMethodAsync("OnFailed", audio.error ? audio.error.message || "audio error" : "audio error"));

    // Lock-screen and headset buttons where the browser offers them.
    if ("mediaSession" in navigator) {
        navigator.mediaSession.setActionHandler("play", () => dotnet.invokeMethodAsync("OnMediaKey", "play"));
        navigator.mediaSession.setActionHandler("pause", () => dotnet.invokeMethodAsync("OnMediaKey", "pause"));
        navigator.mediaSession.setActionHandler("seekbackward", () => dotnet.invokeMethodAsync("OnMediaKey", "back"));
        navigator.mediaSession.setActionHandler("seekforward", () => dotnet.invokeMethodAsync("OnMediaKey", "forward"));
    }
}

export async function play(url, speed, title) {
    audio.src = url;
    audio.playbackRate = speed;
    audio.defaultPlaybackRate = speed;
    if ("mediaSession" in navigator) {
        navigator.mediaSession.metadata = new MediaMetadata({ title: title, artist: "ON.VOYAGE" });
    }
    await audio.play();
}

export function pause() { audio.pause(); }
export async function resume() { await audio.play(); }
export function seek(seconds) { audio.currentTime = seconds; }
export function setSpeed(speed) { audio.playbackRate = speed; audio.defaultPlaybackRate = speed; }
export function stop() { audio.pause(); audio.removeAttribute("src"); audio.load(); }
