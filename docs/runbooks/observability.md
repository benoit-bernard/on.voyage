# Runbook — observabilité

Couvre §17.1, NF-05 et T-009. Fichiers : `deploy/staging/observability/`. Le développement local utilise le tableau de bord Aspire ; le staging et la production utilisent cette pile auto-hébergée (le cahier des charges la place sous `infrastructure/observability/` ; elle vit ici pour rester avec le reste du déploiement).

## Architecture

```
services .NET ──OTLP gRPC :4317──▶ otel-collector ──traces──▶ Tempo   (7 jours)
(ServiceDefaults)                    │  transform/privacy ──logs────▶ Loki    (30 jours)
                                     └──────────────metrics─ :8889 ◀─ Prometheus (30 jours) ◀─ blackbox (sondes /health)
                                                                          Grafana (127.0.0.1:3000) lit les trois
```

- Chaque service exporte traces, métriques et journaux dès que `OTEL_EXPORTER_OTLP_ENDPOINT` est renseigné (`ServiceDefaults/Extensions.cs`) ; vide, rien n'est exporté. Le nom de service est `OTEL_SERVICE_NAME` (nom du service Compose) et l'environnement `deployment.environment=staging`.
- Aucune donnée voyageur : l'instrumentation ASP.NET Core et `HttpClient` des services remplace `url.query` et `url.full` (positions `lat`, `lon`…), et les catégories de journaux qui impriment l'URL sont plafonnées à `Warning`. Le collecteur ajoute une seconde barrière (`transform/privacy` : suppression de `url.query`, masquage des paramètres de position dans `url.full`, `http.url`, `http.target` et dans le corps des journaux). Caddy supprime aussi la requête de ses journaux d'accès.
- Aucune télémétrie vers un tiers : Grafana a ses signalements, la recherche de mises à jour et le catalogue de greffons désactivés ; Loki et Tempo n'envoient pas de statistiques d'usage.
- Seul Grafana est publié, sur `127.0.0.1:3000` du VPS : `ssh -L 3000:127.0.0.1:3000 deploy@<hôte>` puis http://localhost:3000 (utilisateur `admin`, mot de passe `GRAFANA_ADMIN_PASSWORD`). Pas d'accès anonyme, pas d'inscription.

## Démarrer la pile

Sur le VPS, après le déploiement du staging (même réseau `onvoyage-staging`) :

```bash
cd /opt/onvoyage
docker compose --env-file .env -f observability/docker-compose.yml up -d
# puis, dans .env : OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
docker compose up -d        # recrée les services avec l'export activé
```

Depuis GitHub : variables `STAGING_OBSERVABILITY=true` et `STAGING_OTLP_ENDPOINT=http://otel-collector:4317`, secret `STAGING_GRAFANA_ADMIN_PASSWORD`, puis relancer `deploy-staging`.

Vérifier : dans Grafana, dossier **ON.VOYAGE**, tableau de bord **ON.VOYAGE - overview** ; les sources Prometheus, Loki et Tempo sont provisionnées (aucune configuration manuelle). Avec la pile arrêtée ou injoignable, les services continuent de fonctionner : l'exporteur réessaie en silence.

## Tableau de bord

| Panneau | Requête / source | Lien avec une exigence |
| --- | --- | --- |
| Disponibilité sur 30 jours | `avg_over_time(probe_success{instance="http://gateway:8080/health"}[30d])` | NF-05 : 99,5 % |
| Sondes de santé | `probe_success` de chaque hôte (`/health`, blackbox toutes les 15 s) | NF-05 |
| Requêtes par seconde | `http_server_request_duration_seconds_count` par `service_name` | — |
| Latence P95 | `histogram_quantile(0.95, …_bucket)` ; seuil à 300 ms | NF-01 |
| Erreurs 5xx | même métrique, `http_response_status_code=~"5.."` | — |
| Coût et jetons Factory sur 24 h | `onvoyage_llm_cost_usd*`, `onvoyage_llm_tokens*` | §17.2, §8.10 |
| Mémoire | `dotnet_process_memory_working_set*` | — |
| Erreurs dans les journaux | Loki `{service_name=~".+"} \|~ "(?i)(error\|exception\|fail)"` | — |
| Traces en erreur | Tempo, TraceQL `{ status = error }` | — |

Les noms de métriques Prometheus dérivent de la conversion OpenTelemetry → Prometheus du collecteur ; les panneaux Factory et mémoire utilisent des expressions régulières sur le nom (`__name__=~…`) pour tolérer le suffixe d'unité. **À valider sur le premier staging réel** : les métriques métier de §17.2 autres que `onvoyage.llm.*` (latence des recommandations, etc.) n'existent pas encore dans le code (seules `onvoyage.llm.cost_usd` et `onvoyage.llm.tokens` sont définies) et n'ont donc pas de panneau.

## Alertes (Prometheus, `alerts.yml`)

| Alerte | Condition | Gravité |
| --- | --- | --- |
| `ServiceDown` | une sonde `/health` échoue pendant 3 min | page |
| `MonthlyAvailabilityBelowTarget` | disponibilité du Gateway sur 30 jours < 99,5 % | avertissement |
| `HighServerErrorRatio` | plus de 5 % de réponses 5xx pendant 10 min, par service | avertissement |
| `SlowRequests` | P95 > 300 ms pendant 15 min, par service | avertissement |

Aucun Alertmanager n'est installé : les alertes sont visibles dans Prometheus (`/alerts`) et Grafana. L'envoi d'un message (e-mail, mobile) est à décider ; un seul destinataire interne suffit. Une sonde externe sur `https://<domaine>/health` (service d'uptime hébergé en UE) complète la mesure de disponibilité en couvrant le DNS, le TLS et Caddy, que la sonde interne ne voit pas.

## Que faire quand…

### Service down
1. `docker compose ps` puis `docker compose logs --tail=100 <service>` sur le VPS.
2. Cause fréquente après un déploiement : migration échouée (le service redémarre en boucle) ou variable manquante (`Auth:JwtSecret`, `Factory:Llm:*` quand le fournisseur est `openai`). Corriger `.env` ou revenir à l'image précédente (workflow `deploy-staging` avec `image_tag`).
3. Base inaccessible : `docker compose ps postgres`, espace disque (`df -h`, volume `postgres-data` — surveiller aussi `wal-incoming` si l'archivage est en retard).

### Latence ou erreurs en hausse
Dans Grafana, ouvrir « Traces en erreur » ou Explore → Tempo, filtrer par `resource.service.name`, suivre la trace du Gateway vers le service ; le `traceId` figure dans les Problem Details renvoyés au client. Les journaux correspondants s'ouvrent depuis la trace (liens Tempo → Loki provisionnés).

### Coût Factory qui dérive
Panneau « Factory: coût » ; comparer avec les prix configurés (`Factory:Llm:Pricing:<modèle>:…`) et le budget du lot (§8.10).

## Rétention et vie privée

| Donnée | Durée | Justification |
| --- | --- | --- |
| Journaux (Loki) | 30 jours | « journaux serveur » du registre (§16.2) |
| Traces (Tempo) | 7 jours | aucune donnée personnelle ; durée de diagnostic |
| Métriques (Prometheus) | 30 jours (`METRICS_RETENTION`) | agrégats |

Règle de revue : **jamais** d'e-mail, de jeton ni de coordonnée dans un journal ou une étiquette de métrique. Si un panneau Loki ou Tempo montre une position, c'est un incident de vie privée : purger les données de la période, corriger l'instrumentation, ajouter un test.

## Limites

- Ces fichiers n'ont pas été démarrés dans cette tranche (pas de démon Docker) : `docker compose config` valide le compose ; les configurations du collecteur, de Prometheus, de Loki, de Tempo et de Grafana, et le tableau de bord, ont été écrits d'après la documentation des versions épinglées et doivent être confirmés au premier démarrage (`docker compose logs otel-collector loki tempo` ne doit contenir aucune erreur de configuration).
- Les versions d'images sont épinglées (collecteur 0.116.1, Prometheus 3.1.0, Loki 3.3.2, Tempo 2.7.0, Grafana 11.4.0, blackbox 0.25.0) ; mise à jour mensuelle avec le reste des dépendances (SEC-12).
