# Store listing, English version (draft)

Status: draft of 2026-10-01. English ships with the MVP phase (matrix §3.2: MVP-0 is French only, `english` is a feature flag), so this listing is **not needed for the MVP-0 test builds**; it is prepared now so that the two languages stay in step. Square brackets `[…]` are **points to confirm with the owner** before submission. Lengths are checked by `docs/stores/check-lengths.py`.

## Common fields

| Field | Limit | Value | Len. |
| --- | --- | --- | --- |
| App name (Apple and Google) | 30 | ON.VOYAGE | 9 |
| Subtitle (Apple) | 30 | An audio guide that knows you | 29 |
| Promotional text (Apple, editable without a new version) | 170 | Marseille told out loud: audio stories picked for your tastes, and the places the crowds have not found yet. No account needed, no ads, no trackers. | 148 |
| Short description (Google) | 80 | Audio stories about the places around you, chosen to match your tastes. | 71 |
| Keywords (Apple, comma-separated, no spaces) | 100 | audio guide,travel,Marseille,walk,history,heritage,sightseeing,map,tourism,off the beaten path | 94 |
| Primary category | — | Apple: Travel. Google: Travel & Local. | — |
| Secondary category (Apple) | — | [Navigation or Lifestyle; avoid Navigation unless the listing promises routing] | — |
| Primary language | — | English (U.K.) [or English (U.S.)] | — |
| Support URL | — | `https://on.voyage/support` [to be created] | — |
| Privacy policy URL | — | `https://on.voyage/privacy` [to be created, see `docs/PRIVACY.md`] | — |
| Marketing URL (Apple) | — | `https://on.voyage` | — |
| Contact e-mail (Google) | — | `contact@on.voyage` [to confirm] | — |
| Copyright (Apple) | — | 2026 [legal name of the publisher] | — |

## Full description (limit 4000, App Store and Google Play)

```text
ON.VOYAGE is an audio travel guide that learns what you like and tells you the stories of the places that matter to you, instead of the same ten must-sees.

In Marseille, open the app, listen to a few short clips, and get a "For you" selection: architecture, history, nature, the sea, neighbourhood life. The more you listen, the more it looks like you. One tap of feedback (like, so-so, not for me) is enough to refine it.

WHAT YOU CAN DO
• Listen to the story of a place in a few minutes, with the screen locked if you wish, with playback speed and a 10-second rewind.
• Browse the map of places around you and filter by interest, by less crowded places, or by places with a story.
• Turn on discovery mode: the app opens the story of the place you are walking past, with the app in the foreground.
• Save places you want to see and get a reminder when you walk near one of them.
• Plan your visit with "Marseille for you" and "What to visit?".
• Read the transcript of every story and learn more through links to Wikipedia and videos (they open outside the app).

LESS CROWDED PLACES, MORE RESPECTFUL TRAVEL
ON.VOYAGE highlights lesser-known places ("Less crowded, just as beautiful") and never starts a story automatically at a site flagged as fragile.

YOUR PRIVACY COMES FIRST
• No account required: the app works as soon as you open it, with an anonymous session. An optional account is created with your e-mail address and a 6-digit code, with no password.
• Your location is used to find nearby places; it is never stored on our servers.
• No ads, no advertising trackers, and no analytics or crash-reporting tools from third-party companies.
• Usage statistics are sent only if you accept them, and you can change your mind at any time.
• Hosted in Europe.

TRANSPARENT ABOUT ARTIFICIAL INTELLIGENCE
Stories are written from verified sources (Wikipedia, Wikidata, OpenStreetMap and official sources, with their licences), then checked by automatic and editorial controls. They are read by a synthetic voice, which the player announces.

Map data © OpenStreetMap contributors, Protomaps.
First destination: Marseille. More to come.
```

Writing notes:

- Same scope rules as the French listing: foreground discovery mode only, no search, no offline, no Premium, no car mode. Add features only when shipped.
- "No ads" is true for MVP-0 and MVP; remove or qualify it when the in-house ad network (V1.1) goes live. The rest of the privacy section is a permanent commitment (§1.2, D-02).

## What's new (App Store "What's New" / Google "What's new")

```text
First test version: audio guide for Marseille, "For you" selection, map, foreground discovery mode, saved places and nearby reminders.
```
