/* ==========================================================================
   Cloudflare Worker — Instagram Media Proxy for on.voyage
   ==========================================================================
   
   Ce worker se connecte à l'API Instagram Graph (via un token long-lived)
   et renvoie les dernières photos au format JSON.

   ── Prérequis ──────────────────────────────────────────────────────────────
   1. Créer une app Facebook : https://developers.facebook.com/apps/
   2. Ajouter le produit « Instagram Basic Display API »
   3. Générer un token utilisateur (short-lived), puis l'échanger contre
      un long-lived token (valide 60 jours).
   4. Stocker le token en secret Cloudflare :
        wrangler secret put INSTAGRAM_ACCESS_TOKEN
   5. Mettre en place un cron (ou un second Worker scheduled) pour
      renouveler le token avant expiration.

   ── Endpoint exposé ────────────────────────────────────────────────────────
   GET /api/instagram
     → { photos: [ { id, media_type, media_url, thumbnail_url, permalink, caption, timestamp } ] }

   ========================================================================== */

const INSTAGRAM_API = "https://graph.instagram.com";

export default {
    async fetch(request, env) {
        const url = new URL(request.url);

        /* ── CORS ──────────────────────────────────────────────────────── */
        const origin = request.headers.get("Origin") || "";
        const allowedOrigins = (env.ALLOWED_ORIGINS || "").split(",").map((s) => s.trim());
        const corsOrigin = allowedOrigins.includes(origin) ? origin : allowedOrigins[0] || "*";

        const corsHeaders = {
            "Access-Control-Allow-Origin": corsOrigin,
            "Access-Control-Allow-Methods": "GET, OPTIONS",
            "Access-Control-Allow-Headers": "Content-Type",
            "Access-Control-Max-Age": "86400",
        };

        if (request.method === "OPTIONS") {
            return new Response(null, { status: 204, headers: corsHeaders });
        }

        /* ── Routing ───────────────────────────────────────────────────── */
        if (url.pathname === "/api/instagram" && request.method === "GET") {
            return handleInstagramRequest(env, corsHeaders);
        }

        return new Response(JSON.stringify({ error: "Not found" }), {
            status: 404,
            headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
    },

    /* ── Scheduled — Rafraîchissement automatique du token ─────────── */
    async scheduled(event, env) {
        await refreshLongLivedToken(env);
    },
};

/* --------------------------------------------------------------------------
   Handler — Récupérer les dernières photos Instagram
   -------------------------------------------------------------------------- */
async function handleInstagramRequest(env, corsHeaders) {
    const token = env.INSTAGRAM_ACCESS_TOKEN;

    if (!token) {
        return jsonResponse(
            { error: "Instagram access token is not configured." },
            500,
            corsHeaders
        );
    }

    const count = parseInt(env.PHOTO_COUNT, 10) || 6;
    const fields = "id,media_type,media_url,thumbnail_url,permalink,caption,timestamp";

    try {
        const apiUrl =
            `${INSTAGRAM_API}/me/media?fields=${fields}&limit=${count}&access_token=${token}`;

        const response = await fetch(apiUrl, {
            cf: { cacheTtl: 3600, cacheEverything: true },
        });

        if (!response.ok) {
            const errorBody = await response.text();
            console.error("Instagram API error:", response.status, errorBody);
            return jsonResponse(
                { error: "Instagram API error", status: response.status },
                502,
                corsHeaders
            );
        }

        const data = await response.json();

        /* Filtrer pour ne garder que les images (et carrousels) */
        const photos = (data.data || [])
            .filter((item) => item.media_type === "IMAGE" || item.media_type === "CAROUSEL_ALBUM")
            .slice(0, count)
            .map((item) => ({
                id: item.id,
                media_type: item.media_type,
                media_url: item.media_url,
                thumbnail_url: item.thumbnail_url || item.media_url,
                permalink: item.permalink,
                caption: item.caption || "",
                timestamp: item.timestamp,
            }));

        return jsonResponse(
            { photos, count: photos.length },
            200,
            corsHeaders,
            3600 /* cache 1h */
        );
    } catch (err) {
        console.error("Worker error:", err);
        return jsonResponse(
            { error: "Internal server error" },
            500,
            corsHeaders
        );
    }
}

/* --------------------------------------------------------------------------
   Rafraîchir le long-lived token (à appeler via un cron trigger)
   -------------------------------------------------------------------------- */
async function refreshLongLivedToken(env) {
    const currentToken = env.INSTAGRAM_ACCESS_TOKEN;
    if (!currentToken) {
        console.error("No token to refresh");
        return;
    }

    try {
        const url =
            `${INSTAGRAM_API}/refresh_access_token?grant_type=ig_refresh_token&access_token=${currentToken}`;
        const response = await fetch(url);
        const data = await response.json();

        if (data.access_token) {
            console.log(
                "Token refreshed successfully. Expires in",
                data.expires_in,
                "seconds."
            );
            /* Note : Pour persister le nouveau token, il faut utiliser
               l'API Cloudflare ou un KV namespace. Avec les secrets,
               le renouvellement nécessite un redéploiement ou un KV. */
        } else {
            console.error("Token refresh failed:", JSON.stringify(data));
        }
    } catch (err) {
        console.error("Token refresh error:", err);
    }
}

/* --------------------------------------------------------------------------
   Utilitaire JSON Response
   -------------------------------------------------------------------------- */
function jsonResponse(body, status, corsHeaders, cacheTtl = 0) {
    const headers = {
        ...corsHeaders,
        "Content-Type": "application/json; charset=utf-8",
    };

    if (cacheTtl > 0) {
        headers["Cache-Control"] = `public, max-age=${cacheTtl}`;
    }

    return new Response(JSON.stringify(body), { status, headers });
}
