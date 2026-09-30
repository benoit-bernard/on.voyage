# ADR-0002 — Tranche verticale initiale du MVP-0

- Livré : T-001 (squelette, tests d'architecture), T-002 (AppHost, ServiceDefaults), T-003 (Gateway YARP, sans JWT ni limitation de débit), T-102 partiel (Catalog : destination, lieux, fiche), moteur de recommandation (§6.6–6.8, sans CF), accueil « Pour vous » avec cohorte témoin, onboarding par catégories, envies, PWA, app MAUI.
- Écarts assumés : Catalog lit un jeu de données Marseille en mémoire (15 lieux, textes courts, sans audio) à la place de PostgreSQL/PostGIS/EF Core (T-101) ; le classement tourne sur l'appareil (le profil ne quitte pas le téléphone) au lieu du service Discovery ; onboarding par catégories (F-02 écran 2) sans extraits audio. Les textes du jeu de données sont à relire avant toute publication.
- Suite logique : T-101 (EF Core + PostGIS), T-006/T-007 (Wolverine persistant, Platform), T-004 (identité), Factory (contenu audio), carte, Web.Public.
