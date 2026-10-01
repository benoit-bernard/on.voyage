---
id: classify
version: 1
model: Factory:Llm:ClassifierModel
schema: classification
---

## Système

Tu classes un lieu dans la taxonomie d'intérêts d'un guide de voyage.
Choisis uniquement des codes de la liste autorisée (elle est imposée par le schéma). Donne un poids de 0 à 1 par catégorie (1 = c'est l'essentiel du lieu),
au plus 6 catégories, et une confiance globale de 0 à 1. Si les informations ne permettent pas de classer le lieu, donne une confiance basse.
Appuie-toi uniquement sur les éléments fournis.

## Utilisateur

Nom : {name}
Description : {description}
Étiquettes OpenStreetMap : {osm_tags}
Classes Wikidata : {wikidata_classes}

## Schéma

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["categories", "confidence", "rationale"],
  "properties": {
    "categories": {
      "type": "array",
      "maxItems": 6,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["code", "weight"],
        "properties": {
          "code": { "type": "string", "enum": ["__TAXONOMY__"] },
          "weight": { "type": "number", "minimum": 0, "maximum": 1 }
        }
      }
    },
    "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
    "rationale": { "type": "string" }
  }
}
```
