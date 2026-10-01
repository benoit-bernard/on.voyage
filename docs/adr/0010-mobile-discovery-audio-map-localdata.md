# ADR-0010 — App : moteur de déclenchement, audio, mode découverte, carte, données locales (T-610, T-607, T-611, T-605, T-602)

- Statut : accepté. Couvre le cœur de l'expérience MVP-0 côté appareil. Hors périmètre : localisation en arrière-plan (T-612), service Discovery et candidats personnalisés (T-501–506), analytics vers Insights (T-801), hors-ligne complet (T-620).

## Moteur de déclenchement (T-610)
- `OnVoyage.App.Core/Discovery/TriggerEngine` est du C# pur, piloté **uniquement par les horodatages des points** : aucune horloge système, aucun timer. Les 6 traces GPX du §14.5 sont rejouées sur une horloge virtuelle, les résultats sont approuvés (`Approved/*.approved.json`, régénérables avec `UPDATE_APPROVED=1`).
- Toutes les valeurs ⚙️ viennent de `TriggerSettings` (défauts = `default-config.json` de Platform ; `FromJson` lit la configuration distante `trigger`).
- Ajout au cahier : la raison `WarmingUp`. Tant que le mode n'est pas établi (moins de 3 échantillons de vitesse), rien n'est déclenché, sinon un premier point en mode marche sans cône choisirait un lieu derrière l'utilisateur.
- Les traces GPX sont **synthétiques** (`data-pipeline/gpx/generate_synthetic.py`), pas les traces réelles de H-001. `min_trigger_score` (0,45) reste à recalibrer sur les vraies.

## Audio (T-607) et mode découverte (T-611)
- `AudioPlaybackController` (App.Core) possède la file (1 en cours + 1 en attente), les vitesses 1 / 1,25 / 1,5, ±10 s, la reprise après interruption si < 30 s, l'avis « voix générée par intelligence artificielle » (une fois), les événements analytics sans coordonnées. Le lecteur est un port `IAudioPlayer` : `MediaElementAudioPlayer` (MAUI, `CommunityToolkit.Maui.MediaElement`) et `BrowserAudioPlayer` (PWA, `<audio>`).
- `DiscoveryModeController` relie position → moteur → audio. Les candidats viennent du **Catalog** (`GET pois` avec histoire, parties audio, `fragile`), car le service Discovery n'existe pas encore ; le score personnel sera précalculé côté serveur plus tard (§14.5).
- Écart : la règle « reprendre si l'interruption dure moins de 30 s » n'est implémentée que pour la notification d'interruption iOS ; sur Android on s'en remet à la gestion du focus audio d'ExoPlayer.
- `Media:PublicBaseUrl` du Catalog doit être l'adresse **publique** du Gateway : l'app lit les MP3 à ces adresses.

## Carte (T-605)
- `MapView.razor` + `wwwroot/js/map.js` : MapLibre GL JS 6 + protocole PMTiles + fond `@protomaps/basemaps` (clair/sombre selon le système). Bibliothèques **embarquées** dans `wwwroot/lib` (versions et licences : `lib/README.md`), aucun CDN, `tile.openstreetmap.org` jamais appelé.
- L'attribution « © OpenStreetMap contributors · Protomaps » est un élément fixe de la page, hors de la bibliothèque : aucun contrôle ne peut la replier.
- Filtres (catégories, moins fréquentés ≤ 2, avec audio, sauvegardés) et GeoJSON sont du C# testé (`MapFeatures`). Pastilles colorées par catégorie plutôt que des icônes : le jeu de sprites n'existe pas encore.
- Adresse du fichier : `Map:TilesUrl` (PWA : `wwwroot/appsettings.json`). Vide, la page « Carte » indique que la carte n'est pas configurée. **Les polices (glyphes) doivent être ajoutées** dans `wwwroot/fonts/` (téléchargement impossible dans l'environnement de développement, voir `lib/README.md`).
- Vérifié : tests bUnit avec interop JS simulée ; test de fumée dans Chromium (modules chargés, carte construite, aucune requête externe) qui a trouvé un import nu `fflate` corrigé. **Non vérifié** : rendu des tuiles (aucun fichier PMTiles de Marseille avant T-307/T-617), interception hors ligne (`WebResourceRequested`, T-620).

## Données locales (T-602)
- Projet `OnVoyage.App.LocalData` (hôte MAUI uniquement, pas la PWA) : `user.db` (EF Core SQLite), file d'envoi persistante, lecteur de pack.
- `user.db` : réglages (session, vecteur, verrous… en JSON sous une clé), dates de récit (30 jours), visites, envies, cache de réponses, file d'envoi. Rappels et impressions viendront avec leurs fonctions. Le schéma est créé par `EnsureCreated` ; **adopter les migrations EF avant le premier changement de schéma publié**.
- `SyncOutbox` : lots de 20 éléments ou 60 s, back-off 5 s ×2 jusqu'à 1 h, reprise après redémarrage, idempotence par `ClientEventId` (un doublon n'est pas mis en file). `HoldingSyncTransport` garde tout en file tant que l'endpoint d'événements de la plateforme n'existe pas (T-801).
- `PackReader` : vérifie taille et SHA-256 de chaque fichier du manifeste **avant** d'ouvrir `pack.db` en lecture seule ; R*Tree pour les lieux proches, FTS5 (sans accents, préfixes, saisie jamais lue comme syntaxe) pour la recherche. `PackSchema` est le sous-ensemble du §14.6 lu par l'app ; le constructeur de packs (T-307) devra le produire.

## Vérification
Les projets MAUI ne se compilent pas ici (charges de travail absentes) : `MediaElementAudioPlayer`, `MauiLocationSource`, `MainPage.xaml`, le manifeste Android et le branchement de `user.db` ne sont vérifiés que par la CI `mobile.yml`. Lecture écran verrouillé et comportement GPS réel : à essayer sur appareil.
