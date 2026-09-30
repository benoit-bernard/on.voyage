# ADR-0001 — PWA par Blazor WebAssembly partageant la RCL

- Statut : accepté (demande du propriétaire produit : Android, iPhone **et** PWA web).
- Contexte : le cahier des charges prévoit un site public SSR (`Web.Public`, SEO) mais pas de PWA installable. La PWA doit offrir la même expérience que l'app.
- Décision : `OnVoyage.Web.Pwa`, Blazor WebAssembly autonome, monte `OnVoyageRoutes` de `OnVoyage.UI.Components` (la même RCL que MAUI). Le profil est stocké en `localStorage` (appareil seulement). Le service worker met en cache le shell ; `/api/` n'est jamais mis en cache.
- Conséquences : pas de dépendance hors stack (Blazor WASM fait partie du SDK .NET). `Web.Public` (SSR, SEO) reste à faire ; la PWA ne le remplace pas. Seul JavaScript : le service worker et son enregistrement.
