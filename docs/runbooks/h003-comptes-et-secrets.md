# Runbook H-003 — Comptes, secrets et staging

Tâche humaine H-003 (cahier des charges §24.2, §21) : ouvrir et configurer Apple Developer, Google Play Console, la clé de projet OpenAI, le VPS, le stockage et CDN UE, le SMTP UE, le DNS et les certificats de signature, puis en déposer les secrets dans GitHub. Durée estimée : 3 à 4 h de travail, mais **les vérifications d'Apple et de Google prennent des jours** : ouvrez ces deux comptes en premier. Elle débloque T-005, T-008 et T-009.

Documents liés : [DEPLOYMENT.md](../DEPLOYMENT.md) (mécanique du déploiement), [restore.md](restore.md) (clés de sauvegarde), [observability.md](observability.md), [Q-2026-10-01-resend.md](../questions/Q-2026-10-01-resend.md), [Q-2026-10-01-openai.md](../questions/Q-2026-10-01-openai.md), [stores/README.md](../stores/README.md).

Vérification automatisée à la fin : `deploy/check-secrets.sh` (section 6). Elle ne montre jamais une valeur.

## 0. Règles

- Aucune valeur secrète dans le dépôt, un ticket, une PR, un message ou un journal. Un gestionnaire de mots de passe (avec double authentification) est la source ; GitHub reçoit une copie.
- Double authentification sur **tous** les comptes. Comptes ouverts au nom du propriétaire du produit (ou de sa société), avec une adresse de contact dédiée (`contact@…`) et non une adresse personnelle liée à un seul poste.
- Régions : tout ce qui touche des données de voyageurs ou des sauvegardes reste **dans l'UE** (NF-04, §16). OpenAI ne reçoit jamais de donnée voyageur (`docs/PRIVACY.md`).
- Les valeurs des secrets GitHub ne peuvent contenir ni apostrophe, ni espace, ni `;`, ni `$` (le fichier `.env` du serveur les écrit entre apostrophes). Générer les secrets avec `openssl rand -hex 32`.
- Un secret par environnement : staging n'utilise jamais une clé de production (voir section 7).

## 1. Ordre conseillé

| # | Compte | Délai | Pourquoi dans cet ordre |
| --- | --- | --- | --- |
| 1 | Apple Developer Program | jours (vérification d'identité ; pour une organisation, numéro D-U-N-S) | le plus long ; TestFlight en dépend |
| 2 | Google Play Console | jours (vérification d'identité) ; un compte personnel récent doit passer par un test fermé avant la production : à confirmer dans la console | idem |
| 3 | Nom de domaine et DNS | minutes à 48 h de propagation | requis par le VPS (certificats), Resend et la PWA |
| 4 | VPS UE | 30 min | requis par le déploiement |
| 5 | Resend (SMTP UE) | 30 min + propagation DNS | requis par la connexion par code |
| 6 | OpenAI (projet et plafond) | 15 min | requis par Factory |
| 7 | Stockage objet UE (+ CDN) | 30 min | tuiles de carte, copie des sauvegardes |
| 8 | Clés de signature Android et iOS | 1 h, après 1 et 2 | pour `mobile.yml` |
| 9 | Dépôt GitHub : environnement `staging`, secrets, variables | 30 min | tout ce qui précède |

## 2. Checklists par compte

### 2.1 Apple Developer

- [ ] S'inscrire au Apple Developer Program (99 USD par an) avec l'identifiant Apple du propriétaire ; **individu** (plus rapide) ou **organisation** (D-U-N-S requis, nom légal affiché dans l'App Store). Ce choix est visible publiquement : décider avant.
- [ ] Activer la double authentification de l'identifiant Apple.
- [ ] Certificates, Identifiers & Profiles › Identifiers : créer l'App ID `voyage.on.app` (c'est l'`ApplicationId` de `src/Mobile/OnVoyage.App/OnVoyage.App.csproj`), capacités : *Background Modes* (audio, localisation) selon `docs/stores/permissions.md`.
- [ ] Certificates : créer un certificat **Apple Distribution** (demande de signature générée sur un Mac, trousseau), l'exporter en `.p12` avec un mot de passe.
- [ ] Profiles : créer un profil de provisionnement **App Store** pour `voyage.on.app`, le télécharger (`.mobileprovision`).
- [ ] App Store Connect : créer l'app (nom, langue principale, bundle `voyage.on.app`, SKU) ; renseigner la politique de confidentialité d'après `docs/stores/apple-privacy-label.md`.
- [ ] App Store Connect › Utilisateurs et accès › Intégrations › clé d'API (rôle *App Manager*) : noter **Key ID** et **Issuer ID**, télécharger la clé `.p8` (**une seule fois**).
- [ ] Encoder en base64 pour GitHub : `base64 -i certificat.p12 | pbcopy` (macOS) ; idem pour le profil.
- Secrets GitHub correspondants (noms proposés, voir 3.3) : `IOS_CERTIFICATE_P12_BASE64`, `IOS_CERTIFICATE_PASSWORD`, `IOS_PROVISIONING_PROFILE_BASE64`, `APP_STORE_CONNECT_KEY_ID`, `APP_STORE_CONNECT_ISSUER_ID`, `APP_STORE_CONNECT_PRIVATE_KEY`.

### 2.2 Google Play Console

- [ ] Créer le compte développeur (frais d'inscription uniques, 25 USD), vérifier l'identité (personne ou organisation) ; double authentification.
- [ ] Créer l'application (nom ON.VOYAGE, langue par défaut, gratuite/payante, déclarations) avec le nom de paquet `voyage.on.app`.
- [ ] Activer **Play App Signing** (Google détient la clé de signature, vous gardez une clé d'**upload**).
- [ ] Créer la clé d'upload (une fois, à conserver hors dépôt, en deux exemplaires) :
  `keytool -genkeypair -v -keystore onvoyage-upload.jks -alias onvoyage-upload -keyalg RSA -keysize 4096 -validity 10000`
  puis `base64 -w0 onvoyage-upload.jks` pour le secret.
- [ ] Remplir « Sécurité des données » d'après `docs/stores/google-data-safety.md`, classification d'âge d'après `docs/stores/age-rating.md`.
- [ ] Console › Paramètres › Accès à l'API : lier un projet Google Cloud, créer un **compte de service** avec le droit de publier sur la piste de test interne, télécharger sa clé JSON.
- [ ] Créer la piste de **test interne** et y ajouter vos testeurs (adresses e-mail).
- Secrets GitHub correspondants (noms proposés) : `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`, `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`.

### 2.3 Clé de projet OpenAI (Factory uniquement)

- [ ] Compte OpenAI Platform, **projet dédié** « onvoyage-staging » (un autre pour la production).
- [ ] Limites de dépense du projet : budget mensuel **dur** (à choisir ; l'amorçage de Marseille plafonne déjà une exécution à `Bootstrap:BudgetUsd` = 5 USD par défaut, [bootstrap-marseille.md](bootstrap-marseille.md)) et alertes à 50 % et 80 %. Une clé sans plafond est refusée par cette checklist.
- [ ] Restreindre le projet aux modèles utilisés (extraction, rédaction, vérification, classification, voix `gpt-4o-mini-tts`).
- [ ] Créer une clé de projet (jamais une clé de compte), la ranger dans le gestionnaire de mots de passe.
- [ ] Contrôler les réglages de données du compte (pas d'entraînement sur vos données, rétention) : questions ouvertes de `Q-2026-10-01-openai.md` à cocher.
- [ ] Choisir les modèles : noms dans `STAGING_LLM_EXTRACTOR_MODEL`, `STAGING_LLM_WRITER_MODEL`, `STAGING_LLM_VERIFIER_MODEL`, `STAGING_LLM_CLASSIFIER_MODEL`. Les tarifs (`Factory__Llm__Pricing__<modèle>__InputPerMillion`…) restent à câbler, voir section 8.
- Sans clé, mettre `STAGING_LLM_PROVIDER=disabled` : le staging démarre, Factory ne produit rien de nouveau (les histoires du snapshot sont publiées sans audio).

### 2.4 VPS en UE

- [ ] Louer un VPS dans l'UE (≥ 4 vCPU, 16 Go de RAM, 100 Go de disque ; chez un hébergeur européen de votre choix — Hetzner, Scaleway ou OVHcloud conviennent) sous Ubuntu LTS ; pas d'hébergeur hors UE.
- [ ] Pare-feu (de l'hébergeur et du système) : entrant **22, 80, 443/tcp et 443/udp** seulement.
- [ ] Installer Docker Engine et le plugin Compose.
- [ ] Créer l'utilisateur `deploy` (groupe `docker`, **sans sudo**), désactiver la connexion par mot de passe SSH.
- [ ] Générer une clé SSH **dédiée au déploiement**, sur votre poste : `ssh-keygen -t ed25519 -f ~/.ssh/onvoyage_staging_deploy -C onvoyage-staging-deploy -N ""` ; la publique va dans `~deploy/.ssh/authorized_keys`, la privée dans `STAGING_SSH_PRIVATE_KEY`.
- [ ] Relever l'empreinte du serveur depuis un poste de confiance : `ssh-keyscan -p 22 <hôte>` → `STAGING_SSH_KNOWN_HOSTS` (épinglée : le workflow n'accepte pas de première connexion non vérifiée).
- [ ] Créer `/opt/onvoyage` (ou le chemin de `STAGING_DEPLOY_PATH`), propriété de `deploy`.
- [ ] Sauvegardes : générer la paire age **hors du serveur** (`age-keygen -o onvoyage-backup.key`), publier la ligne `# public key: age1…` dans `STAGING_BACKUP_AGE_RECIPIENT`, garder la privée en **deux exemplaires** hors serveur ([restore.md](restore.md)).

### 2.5 DNS

- [ ] Domaine de staging, par exemple `staging.on.voyage` : enregistrements `A` (et `AAAA`) pour **trois noms** vers l'IP du VPS : `<domaine>`, `admin.<domaine>` et `api.<domaine>` (Caddy ne peut pas obtenir de certificat pour un nom absent du DNS ; sans `api.`, retirer son bloc du `Caddyfile`).
- [ ] TTL court (300 s) pendant la mise en place.
- [ ] Enregistrements de vérification du domaine d'envoi de Resend (SPF, DKIM, et DMARC recommandé) : voir 2.6.
- [ ] Optionnel : enregistrement `CAA` limitant les certificats à l'autorité utilisée par Caddy.
- [ ] Production (T-010) : le domaine `on.voyage` et `api.on.voyage` (adresse figée dans `MauiProgram.cs` pour les builds Release) ; hors périmètre ici.

### 2.6 E-mail : Resend, région UE

- [ ] Créer le compte Resend, **région UE**, ajouter le domaine d'envoi (ex. `on.voyage` ou `mail.staging…`) et publier SPF et DKIM chez le DNS ; DMARC en `p=none` pour commencer.
- [ ] Clé d'API avec la seule permission d'**envoi**, limitée à ce domaine → `STAGING_RESEND_API_KEY` (préfixe `re_`).
- [ ] Adresse d'expédition → `STAGING_EMAIL_FROM` (ex. `ON.VOYAGE <connexion@on.voyage>`) ; le domaine doit être vérifié.
- [ ] Signer le DPA de Resend, l'ajouter au registre des sous-traitants, confirmer la durée de conservation des journaux d'envoi ([Q-2026-10-01-resend.md](../questions/Q-2026-10-01-resend.md)).
- [ ] Un SMTP UE quelconque ne convient pas sans changement de code : l'implémentation est `ResendEmailSender` (ADR-0005).

### 2.7 Stockage objet et CDN en UE

Aujourd'hui, le dépôt n'a besoin que d'**une URL publique** pour la carte (`STAGING_MAP_TILES_URL`) ; l'audio est encore sur un volume du VPS (`media`).

- [ ] Compartiment (bucket) public en lecture dans l'UE pour le fichier `.pmtiles` : requêtes **HTTP Range** autorisées, CORS `GET, HEAD` depuis `https://<domaine>` avec l'en-tête `Range` autorisé.
- [ ] Mettre l'URL https du fichier dans la variable `STAGING_MAP_TILES_URL` (variable de **dépôt** : elle est lue par le job de construction des images, qui n'a pas d'environnement).
- [ ] Deuxième compartiment, **privé**, dans un autre site ou chez un autre hébergeur de l'UE, pour copier `/backups` (déjà chiffré par age) : l'outil de copie (`rclone`) et ses clés restent à câbler (section 8).
- [ ] CDN : facultatif en staging ; en production, un CDN européen devant le compartiment public. Aucune ressource tierce n'est chargée par l'app ou le web (règle de vie privée) : le CDN sert vos propres fichiers.

### 2.8 Dépôt GitHub

- [ ] Settings › Environments › **New environment : `staging`** (le workflow l'utilise : `environment: name: staging`). Option : relecteur requis pour la production plus tard.
- [ ] Actions › General › Workflow permissions : lecture par défaut ; le workflow demande `packages: write` lui-même.
- [ ] Packages (GHCR) : après le premier déploiement, vérifier que les paquets `onvoyage/*` sont rattachés au dépôt (le serveur se connecte avec le jeton du workflow).
- [ ] Créer les secrets et variables de la section 3 (commandes en 5).

## 3. Noms exacts attendus par le dépôt

Source : `.github/workflows/deploy-staging.yml`, `deploy/staging/docker-compose.yml`, `deploy/staging/observability/docker-compose.yml`, `deploy/staging/.env.example`, `src/Aspire/OnVoyage.AppHost/AppHost.cs`. `deploy/check-secrets.sh` embarque la même liste.

### 3.1 Secrets GitHub (environnement `staging`)

| Nom | Obligatoire | Contenu | Lu par | Comment le produire |
| --- | --- | --- | --- | --- |
| `STAGING_SSH_HOST` | oui | nom DNS ou IP du VPS | étape « Prepare SSH » | de l'hébergeur |
| `STAGING_SSH_USER` | oui | `deploy` | idem | 2.4 |
| `STAGING_SSH_PRIVATE_KEY` | oui | clé privée SSH de déploiement (texte complet, en-têtes inclus) | idem | `ssh-keygen`, 2.4 |
| `STAGING_SSH_KNOWN_HOSTS` | oui | sortie de `ssh-keyscan` | idem | 2.4 |
| `STAGING_POSTGRES_PASSWORD` | oui | mot de passe PostgreSQL (hexadécimal, pas de `;`) | `.env` → `POSTGRES_PASSWORD` | `openssl rand -hex 32` |
| `STAGING_JWT_SECRET` | oui | clé HS256 partagée, 32 octets minimum | `.env` → `JWT_SECRET` → `Auth__JwtSecret` | `openssl rand -hex 32` |
| `STAGING_BOOTSTRAP_ADMIN_EMAIL` | oui | adresse qui reçoit le rôle `admin` à la connexion | `.env` → `BOOTSTRAP_ADMIN_EMAIL` | votre adresse |
| `STAGING_RESEND_API_KEY` | oui | clé d'API Resend (`re_…`) | `.env` → `RESEND_API_KEY` | 2.6 |
| `STAGING_OPENAI_API_KEY` | oui, sauf `STAGING_LLM_PROVIDER=disabled` | clé de projet OpenAI (`sk-…`) | `.env` → `OPENAI_API_KEY` | 2.3 |
| `STAGING_GRAFANA_ADMIN_PASSWORD` | si `STAGING_OBSERVABILITY=true` | mot de passe Grafana | `.env` → `GRAFANA_ADMIN_PASSWORD` | `openssl rand -hex 16` |

`GITHUB_TOKEN` est fourni par GitHub : ne pas le créer.

### 3.2 Variables GitHub

| Nom | Obligatoire | Contenu | Niveau |
| --- | --- | --- | --- |
| `STAGING_DOMAIN` | oui | nom de domaine sans schéma, ex. `staging.on.voyage` | environnement `staging` |
| `STAGING_ACME_EMAIL` | oui | contact de l'autorité de certification | environnement |
| `STAGING_BACKUP_AGE_RECIPIENT` | oui | clé **publique** age (`age1…`) | environnement |
| `STAGING_LLM_EXTRACTOR_MODEL`, `STAGING_LLM_WRITER_MODEL`, `STAGING_LLM_VERIFIER_MODEL`, `STAGING_LLM_CLASSIFIER_MODEL` | oui, sauf fournisseur `disabled` | identifiants de modèles | environnement |
| `STAGING_LLM_PROVIDER` | non | `openai` (défaut) ou `disabled` | environnement |
| `STAGING_EMAIL_FROM` | non (recommandé) | expéditeur des codes | environnement |
| `STAGING_MAP_TILES_URL` | non | URL https du `.pmtiles` | **dépôt** (lue par le job d'images, sans environnement) |
| `STAGING_DEPLOY_PATH` | non | défaut `/opt/onvoyage` | environnement |
| `STAGING_SSH_PORT` | non | défaut `22` | environnement |
| `STAGING_OBSERVABILITY` | non | `true` pour démarrer la pile d'observabilité | environnement |
| `STAGING_OTLP_ENDPOINT` | non | `http://otel-collector:4317` quand la pile tourne | environnement |

Le workflow écrit aussi dans `.env` `IMAGE_PREFIX`, `IMAGE_TAG` et le jeton de registre : rien à créer. `MIGRATE_ON_START`, `CATALOG_SEED_DEMO_DATA`, `BACKUP_RETENTION_DAYS`, `BACKUP_HOUR_UTC`, `BACKUP_BASEBACKUP_WEEKDAY`, `METRICS_RETENTION`, `POSTGRES_DB`, `POSTGRES_USER` ont des valeurs par défaut dans le compose et ne sont pas écrites par le workflow (pour les changer : modifier le workflow).

### 3.3 Signature des applications (noms **proposés**)

`.github/workflows/mobile.yml` ne lit encore aucun secret (« Signing (H-003): add … once the Play Console account exists »). Pour que la tâche qui câblera la signature n'ait rien à renommer, créez ces secrets de **dépôt** avec ces noms ; `check-secrets.sh --with-mobile` les vérifie. À confirmer au moment de l'écriture du workflow.

| Nom | Contenu |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | clé d'upload `.jks` en base64 |
| `ANDROID_KEYSTORE_PASSWORD` | mot de passe du keystore |
| `ANDROID_KEY_ALIAS` | alias (`onvoyage-upload`) |
| `ANDROID_KEY_PASSWORD` | mot de passe de la clé |
| `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` | clé JSON du compte de service Play |
| `IOS_CERTIFICATE_P12_BASE64` | certificat Apple Distribution `.p12` en base64 |
| `IOS_CERTIFICATE_PASSWORD` | mot de passe du `.p12` |
| `IOS_PROVISIONING_PROFILE_BASE64` | profil App Store en base64 |
| `APP_STORE_CONNECT_KEY_ID`, `APP_STORE_CONNECT_ISSUER_ID`, `APP_STORE_CONNECT_PRIVATE_KEY` | clé d'API App Store Connect (TestFlight) |

### 3.4 Paramètres Aspire (développement local, pour mémoire)

`dotnet run --project src/Aspire/OnVoyage.AppHost` n'exige **aucun** secret : la clé JWT est générée et conservée dans les secrets utilisateur de l'AppHost, le code de connexion s'écrit dans le journal, Factory tourne hors ligne. En mode publication (`aspire publish`), `AppHost.cs` demande les paramètres `resend-api-key`, `openai-api-key` (secrets) et `llm-extractor-model`, `llm-writer-model`, `llm-verifier-model`, `llm-classifier-model` ; pour le staging réel on n'utilise pas Aspire mais `deploy/staging/docker-compose.yml`, qui reçoit les valeurs ci-dessus.

## 4. Procédure complète

1. Comptes Apple et Google ouverts (2.1, 2.2) ; en attendant la vérification, faire la suite.
2. DNS et VPS (2.5, 2.4).
3. Resend (2.6), OpenAI (2.3), stockage (2.7).
4. Créer l'environnement `staging` et les secrets (section 5).
5. `deploy/check-secrets.sh --github` : tout doit être « OK ».
6. Actions › `deploy-staging` › *Run workflow*. Le test de fumée final (`deploy/staging/smoke-test.sh`) valide `/health`, la configuration, une session anonyme, le catalogue, la PWA et le back-office.
7. Se connecter au back-office `https://admin.<domaine>` avec `STAGING_BOOTSTRAP_ADMIN_EMAIL` (le code arrive par Resend).
8. Importer Marseille : `POST /api/factory/v1/admin/snapshot-imports {"destination":"marseille"}` (jeton `admin`).

## 5. Déposer les secrets (sans les afficher)

```bash
# Secrets de l'environnement staging (la valeur vient d'un fichier ou de l'entrée standard, jamais de la ligne de commande)
gh secret set STAGING_JWT_SECRET --env staging --body "$(openssl rand -hex 32)"
gh secret set STAGING_POSTGRES_PASSWORD --env staging --body "$(openssl rand -hex 32)"
gh secret set STAGING_SSH_PRIVATE_KEY --env staging < ~/.ssh/onvoyage_staging_deploy
ssh-keyscan -p 22 <hôte> | gh secret set STAGING_SSH_KNOWN_HOSTS --env staging
gh secret set STAGING_SSH_HOST --env staging          # saisie interactive
gh secret set STAGING_SSH_USER --env staging
gh secret set STAGING_BOOTSTRAP_ADMIN_EMAIL --env staging
gh secret set STAGING_RESEND_API_KEY --env staging
gh secret set STAGING_OPENAI_API_KEY --env staging

# Variables
gh variable set STAGING_DOMAIN --env staging --body staging.on.voyage
gh variable set STAGING_ACME_EMAIL --env staging --body ops@on.voyage
gh variable set STAGING_BACKUP_AGE_RECIPIENT --env staging --body age1...
gh variable set STAGING_LLM_EXTRACTOR_MODEL --env staging --body <modèle>      # idem WRITER, VERIFIER, CLASSIFIER
gh variable set STAGING_MAP_TILES_URL --body https://.../marseille.pmtiles    # niveau DÉPÔT (pas --env)
```

Pour générer un secret que vous n'avez pas besoin de relire, `openssl rand -hex 32` directement dans `gh secret set` le garde hors de l'historique du shell. Les secrets déjà posés ne se relisent pas dans GitHub : gardez-les dans le gestionnaire de mots de passe.

## 6. Vérifier : `deploy/check-secrets.sh`

```bash
deploy/check-secrets.sh --github                      # secrets et variables présents sur GitHub (noms seulement)
deploy/check-secrets.sh --env-file ~/secrets/staging.env   # fichier KEY=VALUE hors dépôt : présence ET format (longueur, préfixe, caractères interdits)
STAGING_JWT_SECRET=... deploy/check-secrets.sh        # variables d'environnement
deploy/check-secrets.sh --mode server --env-file /opt/onvoyage/.env   # le .env du VPS (noms du compose)
```

Options : `--with-observability`, `--with-mobile`, `--provider-disabled`, `--repo owner/nom`. Code de sortie 1 si un nom obligatoire manque ou si un format est invalide. Aucune valeur n'est affichée, même en erreur. Tests : `bash deploy/check-secrets.test.sh`.

Ce que le script ne peut pas vérifier : qu'une clé fonctionne réellement (Resend, OpenAI), que le plafond OpenAI est posé, que le DNS pointe vers le VPS. Le premier déploiement et son test de fumée tranchent.

## 7. Ce que « staging » veut dire pour ON.VOYAGE

- **Ce que c'est** : un seul VPS européen qui fait tourner, via `deploy/staging/docker-compose.yml`, exactement les images qui iront en production, avec un `.env` à lui, son domaine, sa base PostgreSQL et ses sauvegardes. Déploiement **automatique à chaque fusion sur `main`** qui touche `src/`, `prompts/` ou `deploy/` (et à la demande, avec retour arrière par `image_tag`). Jamais de développement directement en production : la production (T-010) se déploie à la main depuis une étiquette `vX.Y.Z` après validation ici.
- **Les données** : jeu de test ou copie anonymisée, **jamais de données de voyageurs réels**. Contenu de départ : le snapshot de Marseille (`data-pipeline/marseille`, importé par l'API admin) et, si voulu, `CATALOG_SEED_DEMO_DATA=true` (ne pas cumuler les deux). Les comptes sont les vôtres et ceux de vos testeurs. Les journaux d'accès Caddy suppriment requête, `Authorization`, cookies et tronquent l'IP.
- **Ce qui est réel** : les e-mails de connexion partent vraiment (Resend) : n'utilisez que des adresses de testeurs. L'appel OpenAI est réel si une clé est posée : plafond bas, projet dédié. Les certificats HTTPS sont de vrais certificats.
- **Ce qui ne l'est pas** : pas de trafic public (en-tête `X-Robots-Tag: noindex, nofollow`) ; disponibilité de 99,5 % non garantie (NF-05 ne vaut qu'en production) ; pas de stockage objet ni de CDN pour l'audio tant que le volume `media` suffit.
- **Ce que le mobile en fait** : les builds Release appellent `https://api.on.voyage/` (adresse figée dans `MauiProgram.cs`). Le staging répond sous `api.<domaine>` pour que ce nom existe, mais un build mobile ne vise le staging qu'après changement de cette adresse. Pour tester avec un téléphone : la PWA du staging (même origine que l'API) ou un build Debug.
- **Séparation des secrets** : staging et production ont des secrets distincts (`STAGING_*` / `PRODUCTION_*`, T-010), des projets OpenAI distincts, des clés SSH distinctes et des clés age distinctes. Une fuite du staging ne doit rien ouvrir en production.
- **Environnements du cahier (§21)** : `development` = votre poste (Aspire) ; `staging` = ce VPS ; `production` = un second VPS UE, manuel.

## 8. Écarts constatés entre le dépôt et cette checklist (à traiter par une tâche agent)

- **Signature mobile non câblée** : `mobile.yml` ne lit aucun secret ; les noms de la section 3.3 sont une proposition.
- **Tarifs OpenAI** : `Factory__Llm__Pricing__<modèle>__…` (runbook de bootstrap) n'est pas dans `docker-compose.yml` ni dans le workflow : sans tarifs, le coût est enregistré à 0 et l'amorçage plafonné refuse de démarrer (sauf `AllowUnpriced`). Variables à ajouter au compose avant le premier lot réel ; le plafond de dépense côté OpenAI (2.3) reste la garde de dernier recours.
- **Clé YouTube** (`YouTube:ApiKey`, recherche de vidéos par l'éditeur) : pas de variable dans le compose.
- **Copie hors site des sauvegardes** : à configurer (`DEPLOYMENT.md`, « Sauvegardes et observabilité »).
- **Services Creators, Insights, Web.Public** : absents du compose (blocs commentés). Les secrets OAuth Instagram/YouTube/TikTok (H-008) n'ont pas encore de nom.
- **Production** : `deploy-production.yml` et les secrets `PRODUCTION_*` sont à écrire (T-010).
- **`STAGING_MAP_TILES_URL`** : `DEPLOYMENT.md` la classe dans l'environnement `staging`, mais elle est lue par le job d'images qui n'a pas d'environnement : créez-la au niveau du dépôt (ce runbook et le script le signalent).
- **`docs/DEPLOYMENT.md` § « Préparer le VPS »** renvoie à H-003 : ce runbook en est la version détaillée.

## 9. Définition de « terminé »

- Apple Developer et Google Play Console : comptes **vérifiés**, app créée, clés de signature produites et sauvegardées en deux exemplaires.
- `deploy/check-secrets.sh --github --with-mobile` : aucune ligne `MANQUANT` ni `INVALIDE`.
- Un déploiement `deploy-staging` vert, `https://<domaine>/health` répond, connexion au back-office par code e-mail réussie.
- Plafond de dépense OpenAI posé (capture ou vérification dans la console).
- DPA Resend signé et consigné ; clé privée age rangée en deux exemplaires.
