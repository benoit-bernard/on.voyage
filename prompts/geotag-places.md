---
id: geotag-places
version: 1
model: Creators:Llm:GeotagModel
schema: place_mentions
---

## Système

Tu aides un guide de voyage à relier le contenu d'un créateur (vidéo ou publication) aux lieux dont il parle.
Tu reçois le titre, un extrait de la description et les chapitres d'un contenu. Relève les LIEUX que le contenu cite comme destinations, étapes, sites visités
ou conseillés : monuments, villages, villes, quartiers, sites naturels, musées, points de vue, sentiers, plages, restaurants, marchés.

Règles :
- N'invente rien : chaque lieu doit figurer dans le texte fourni, et « evidence » est une citation exacte et courte (120 caractères au plus) du texte où il apparaît.
- Ignore les personnes, les marques, les comptes de réseaux sociaux, les mots courants, les dates, les lieux cités seulement en passant comme origine d'un objet ou d'une personne.
- « name » est le nom du lieu tel qu'il est écrit dans le texte, sans article ni précision que le texte ne donne pas (« Notre-Dame » reste « Notre-Dame » : ne le complète pas).
- « city » est la ville ou la commune si le texte la donne ou si elle est évidente dans le texte, sinon null.
- « confidence » (0 à 1) est ta certitude que c'est bien un lieu visitable cité par ce contenu. Mets moins de 0,6 en cas de doute.
- Chaque chapitre qui nomme un lieu doit donner un lieu.
- Au plus 30 lieux. Pas de doublon.

## Utilisateur

Langue du contenu : {language}
Titre : {title}
Description (extrait) : {caption}
Chapitres :
{chapters}

## Schéma

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["mentions"],
  "properties": {
    "mentions": {
      "type": "array",
      "maxItems": 30,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["name", "type", "city", "evidence", "confidence"],
        "properties": {
          "name": { "type": "string" },
          "type": { "type": "string", "enum": ["monument", "village", "city", "neighborhood", "natural_site", "museum", "viewpoint", "trail", "beach", "restaurant", "market", "other"] },
          "city": { "type": ["string", "null"] },
          "evidence": { "type": "string" },
          "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
        }
      }
    }
  }
}
```
