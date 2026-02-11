# Cloudflare Worker — Instagram Media Proxy

Ce Worker sert de proxy entre le site on.voyage et l'API Instagram Graph.

## Prérequis

1. **Compte Facebook Developer** — [developers.facebook.com](https://developers.facebook.com/apps/)
2. **Application Facebook** avec le produit « Instagram Basic Display API »
3. **Node.js ≥ 18** et **Wrangler CLI** installé globalement ou via npx

## Configuration

### 1. Obtenir un Access Token Instagram

1. Dans la console Facebook Developer, aller dans **Instagram Basic Display** → **User Token Generator**
2. Cliquer sur **Generate Token** pour votre compte `@benoit_bernard`
3. Copier le **short-lived token**
4. L'échanger contre un **long-lived token** (60 jours) :

```bash
curl -s "https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret=YOUR_APP_SECRET&access_token=SHORT_LIVED_TOKEN"
```

### 2. Configurer le secret Wrangler

```bash
cd worker
npm install
wrangler secret put INSTAGRAM_ACCESS_TOKEN
# Coller le long-lived token quand demandé
```

### 3. Déployer

```bash
wrangler deploy
```

Le Worker sera accessible à : `https://instagram-worker.on-voyage.workers.dev/api/instagram`

### 4. Renouvellement automatique du token

Ajouter un **Cron Trigger** dans `wrangler.toml` pour renouveler le token toutes les 4 semaines :

```toml
[triggers]
crons = ["0 0 1,15 * *"]  # 1er et 15 de chaque mois
```

## API

### `GET /api/instagram`

Retourne les 6 dernières photos Instagram.

**Réponse :**
```json
{
  "photos": [
    {
      "id": "...",
      "media_type": "IMAGE",
      "media_url": "https://...",
      "thumbnail_url": "https://...",
      "permalink": "https://www.instagram.com/p/...",
      "caption": "...",
      "timestamp": "2024-01-01T12:00:00+0000"
    }
  ],
  "count": 6
}
```

## Développement local

```bash
cd worker
npm install
wrangler dev
```

Le Worker sera disponible sur `http://localhost:8787`.
