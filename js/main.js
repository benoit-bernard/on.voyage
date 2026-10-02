/* ==========================================================================
   on.voyage — Main Script
   ========================================================================== */

document.addEventListener("DOMContentLoaded", () => {
    initDarkMode();
    initTabs();
    initVideoHero();
    initScrollAnimations();
    loadInstagramPhotos();
});

/* ---------- Dark Mode ---------- */
function initDarkMode() {
    const prefersDark =
        window.matchMedia &&
        window.matchMedia("(prefers-color-scheme: dark)").matches;

    if (prefersDark) {
        document.documentElement.classList.add("dark");
    }
}

/* ---------- Tab Navigation ---------- */
function initTabs() {
    const tabContainer = document.querySelector("[data-tabs]");
    if (!tabContainer) return;

    const buttons = tabContainer.querySelectorAll("[data-tab]");
    const sections = document.querySelectorAll("[data-tab-content]");

    buttons.forEach((btn) => {
        btn.addEventListener("click", () => {
            const target = btn.dataset.tab;

            buttons.forEach((b) => {
                b.classList.remove(
                    "bg-white",
                    "dark:bg-primary",
                    "text-slate-900",
                    "dark:text-background-dark",
                    "shadow-sm"
                );
                b.classList.add(
                    "text-slate-500",
                    "dark:text-slate-400",
                    "hover:text-slate-700",
                    "dark:hover:text-white"
                );
            });

            btn.classList.remove(
                "text-slate-500",
                "dark:text-slate-400",
                "hover:text-slate-700",
                "dark:hover:text-white"
            );
            btn.classList.add(
                "bg-white",
                "dark:bg-primary",
                "text-slate-900",
                "dark:text-background-dark",
                "shadow-sm"
            );

            sections.forEach((section) => {
                section.classList.toggle("hidden", section.dataset.tabContent !== target);
            });
        });
    });
}

/* ---------- Video Hero — Play on click ---------- */
function initVideoHero() {
    const hero = document.querySelector(".video-hero");
    if (!hero) return;

    hero.addEventListener("click", () => {
        const wrapper = hero.querySelector(".video-hero__iframe-wrapper");
        if (!wrapper) return;

        if (wrapper.classList.contains("is-active")) return;

        const videoId = hero.dataset.videoId;
        const iframe = document.createElement("iframe");
        iframe.src = `https://www.youtube.com/embed/${videoId}?autoplay=1&rel=0`;
        iframe.allow =
            "accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture";
        iframe.allowFullscreen = true;
        iframe.title = hero.dataset.videoTitle || "Video";

        wrapper.appendChild(iframe);
        wrapper.classList.add("is-active");
    });
}

/* ---------- Scroll Animations ---------- */
function initScrollAnimations() {
    const elements = document.querySelectorAll(".fade-in-up");
    if (!elements.length) return;

    const observer = new IntersectionObserver(
        (entries) => {
            entries.forEach((entry) => {
                if (entry.isIntersecting) {
                    entry.target.classList.add("is-visible");
                    observer.unobserve(entry.target);
                }
            });
        },
        { threshold: 0.15 }
    );

    elements.forEach((el) => observer.observe(el));
}

/* ---------- Instagram Photos via Cloudflare Worker ---------- */
async function loadInstagramPhotos() {
    const grid = document.getElementById("instagram-grid");
    if (!grid) return;

    /* -----------------------------------------------------------
       The Cloudflare Worker URL should be configured here.
       During development or if the worker is not yet deployed,
       we fall back to placeholder images.
       ----------------------------------------------------------- */
    const WORKER_URL = "https://instagram-worker.on-voyage.workers.dev/api/instagram";

    try {
        const response = await fetch(WORKER_URL);

        if (!response.ok) {
            throw new Error(`Worker responded with ${response.status}`);
        }

        const data = await response.json();
        const photos = data.photos || [];

        if (photos.length === 0) {
            throw new Error("No photos returned");
        }

        renderInstagramGrid(grid, photos.slice(0, 6));
    } catch {
        renderInstagramFallback(grid);
    }
}

function renderInstagramGrid(container, photos) {
    container.innerHTML = "";

    photos.forEach((photo, index) => {
        const item = document.createElement("a");
        item.href = photo.permalink || "https://www.instagram.com/benoit_bernard/";
        item.target = "_blank";
        item.rel = "noopener noreferrer";
        item.className = "instagram-grid__item bg-slate-200 dark:bg-slate-800";
        item.setAttribute("aria-label", photo.caption || `Photo Instagram ${index + 1}`);

        const img = document.createElement("img");
        img.src = photo.thumbnail_url || photo.media_url;
        img.alt = photo.caption
            ? photo.caption.substring(0, 80)
            : `Photo nature on.voyage ${index + 1}`;
        img.loading = "lazy";

        item.appendChild(img);
        container.appendChild(item);
    });
}

function renderInstagramFallback(container) {
    container.innerHTML = "";

    const placeholders = [
        "https://i.ytimg.com/vi/wZyGEQcArpc/maxresdefault.jpg",
        "https://i.ytimg.com/vi/wZyGEQcArpc/hqdefault.jpg",
        "https://i.ytimg.com/vi/wZyGEQcArpc/sddefault.jpg",
        "https://i.ytimg.com/vi/wZyGEQcArpc/maxresdefault.jpg",
        "https://i.ytimg.com/vi/wZyGEQcArpc/hqdefault.jpg",
        "https://i.ytimg.com/vi/wZyGEQcArpc/sddefault.jpg",
    ];

    placeholders.forEach((src, index) => {
        const item = document.createElement("a");
        item.href = "https://www.instagram.com/benoit_bernard/";
        item.target = "_blank";
        item.rel = "noopener noreferrer";
        item.className = "instagram-grid__item bg-slate-200 dark:bg-slate-800";

        const img = document.createElement("img");
        img.src = src;
        img.alt = `Photo nature on.voyage ${index + 1}`;
        img.loading = "lazy";

        item.appendChild(img);
        container.appendChild(item);
    });
}
