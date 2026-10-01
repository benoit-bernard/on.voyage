# ADR-0006 — Protection du Gateway : débit, robots, vie privée des traces (fin de T-003)

- Statut : accepté. Complète T-003 (le routage YARP et la validation JWT existaient depuis ADR-0005).

## Décision
- **Réglages pilotés par Platform** : `EdgeConfigRefresher` lit `GET /api/platform/v1/config?scope=edge` toutes les `security.edge_config_refresh_seconds` (60 s) avec un jeton interne de 5 minutes (rôle `internal`, `InternalTokens`). En cas d'échec il garde la dernière valeur ; avant la première réponse, ce sont les valeurs de l'annexe E codées dans `EdgeSettings.Defaults` (un test les compare au fichier d'amorçage de Platform). Des valeurs invalides venant de Platform sont ignorées champ par champ (elles ne peuvent pas affaiblir la protection).
- **Robots d'IA** : `security.blocked_user_agents`, comparaison insensible à la casse par sous-chaîne, 403 `blocked_client` sur tous les chemins (y compris `/api`), avant toute authentification. Une absence de User-Agent n'est pas bloquée.
- **Limitation de débit (SEC-04)** : fenêtre glissante d'une minute, par adresse client (300/min) et, une fois authentifié, par voyageur (120/min). Le limiteur passe après l'authentification mais avant l'autorisation, donc les jetons invalides sont aussi limités par adresse. 429 `rate_limited` avec `Retry-After`. `/health` et `/alive` sont exemptés. La valeur de la limite fait partie de la clé de partition : un nouveau réglage s'applique aux nouvelles fenêtres sans redémarrage.
- **Adresse client derrière un proxy** : `X-Forwarded-For` n'est pris en compte que si `Gateway:TrustForwardedHeaders=true` (fixé par l'AppHost hors mode exécution local), faute de quoi n'importe qui choisirait son propre seau. Le proxy de tête doit donc être le seul accès au Gateway.
- **Vie privée des traces et journaux (§17.1)** : `ConfigurePrivacyLogging` (ServiceDefaults, tous les hôtes) plafonne à Warning les catégories qui impriment l'URL complète (`Microsoft.AspNetCore.Hosting.Diagnostics`, `Yarp…HttpForwarder`, `System.Net.Http.HttpClient`, `HttpLogging`), en code et non en configuration. Les spans ASP.NET Core et HttpClient vident `url.query` et réduisent `url.full` au chemin.

## Preuve
`OnVoyage.Gateway.Tests` (20 tests) : JWT invalide, falsifié, expiré ou absent → 401 ; `GPTBot` → 403 ; dépassement → 429 ; repli sur les valeurs par défaut sans Platform ; et un test qui capture tous les spans et journaux du processus pendant une requête `?lat=…&lon=…` et vérifie qu'aucune coordonnée n'y figure. Ce dernier a été vérifié par mutation : sans le filtre de journaux, le journal YARP `Proxying to …?lat=43.29517&lon=…` fait échouer le test (il fuyait réellement avant ce changement) ; sans la purge de `url.query`, il échoue aussi.

## Limites
- Les compteurs de débit sont en mémoire, par instance du Gateway (une seule instance au MVP-0, §9.7). Plusieurs instances demanderaient un limiteur partagé.
- Les limites « 10 signalements par jour » et « 5 OTP par heure » sont appliquées par les services concernés (OTP : Platform ; signalements : Factory, à venir).
- Les journaux d'accès du proxy de tête (Caddy/Traefik) peuvent contenir la requête complète : à désactiver ou masquer lors du déploiement (T-005, T-009).
