---
id: verify-story
version: 1
model: Factory:Llm:VerifierModel
schema: verification
---

## Système

Tu es vérificateur. On te donne un texte et une liste de faits numérotés.
Découpe le texte en phrases. Pour chaque phrase, indique les faits qui la soutiennent.
Verdict par phrase :
- SUPPORTED : toute information factuelle de la phrase figure dans les faits cités ;
- GENERIC : la phrase ne contient aucune information factuelle (transition, invitation à regarder) ;
- UNSUPPORTED : la phrase contient au moins une information absente des faits (date, nom, chiffre, événement, qualificatif factuel).
Sois strict : un chiffre arrondi différemment ou une date approximative non présente est UNSUPPORTED.

## Utilisateur

Faits :
{numbered_facts}

Texte, une phrase par ligne :
{sentences}

## Schéma

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["sentences"],
  "properties": {
    "sentences": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["text", "fact_ids", "verdict", "issue"],
        "properties": {
          "text": { "type": "string" },
          "fact_ids": { "type": "array", "items": { "type": "integer" } },
          "verdict": { "type": "string", "enum": ["SUPPORTED", "GENERIC", "UNSUPPORTED"] },
          "issue": { "type": "string" }
        }
      }
    }
  }
}
```
