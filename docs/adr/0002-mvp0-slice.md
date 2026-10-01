# ADR-0002 — Tranche verticale initiale du MVP-0

- Livré : T-004 (identité, ADR-0005), T-006 (messagerie Wolverine), T-007 (Platform), T-001 (squelette, tests d'architecture), T-002 (AppHost, ServiceDefaults), T-003 (Gateway YARP, sans JWT ni limitation de débit), T-102 partiel (Catalog : destination, lieux, fiche), moteur de recommandation (§6.6–6.8, sans CF), accueil « Pour vous » avec cohorte témoin, onboarding par catégories, envies, PWA, app MAUI.
- Écarts assumés : Catalog sert 15 lieux de démonstration (textes courts, sans audio) ; la persistance PostgreSQL/PostGIS est faite depuis (ADR-0003) ; le classement tourne sur l'appareil (le profil ne quitte pas le téléphone) au lieu du service Discovery ; onboarding par catégories (F-02 écran 2) sans extraits audio. Les textes du jeu de données sont à relire avant toute publication.
- Suite logique : T-101 (EF Core + PostGIS), T-006/T-007 (Wolverine persistant, Platform), T-004 (identité), Factory (contenu audio), carte, Web.Public.
