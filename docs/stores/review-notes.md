# Notes pour la revue (App Review et Play Console)

Brouillon du 2026-10-01, à copier dans « Notes pour la revue » (Apple) et « Accès à l'application » (Google).

## Accès

```text
No sign-in is required. On first launch the app opens an anonymous session automatically. An optional account (e-mail address + 6-digit code, no password) can be created from the "Account" screen; reviewers do not need it, every feature is available without it.
```

(Les relecteurs ne reçoivent pas les e-mails de code ; ne **pas** fournir d'identifiants fictifs.)

## Comment tester

```text
1. Open the app and answer the short audio onboarding (or skip it).
2. Set the device location to Marseille, France (43.2951 N, 5.3744 E; Xcode: Debug > Simulate Location; Android emulator: Extended controls > Location). The app uses location only while it is open, and only after you tap the discovery button or open the map.
3. "For you" lists places near Marseille. Open a place and play its story. Lock the screen: playback continues (background audio) and can be controlled from the lock screen.
4. Discovery mode (toggle on the home screen): with a simulated route through the Old Port, the story of the place you pass is offered/played automatically while the app is open.
Stories are written from cited sources and read by a synthetic (AI-generated) voice; the player says so. There is no AI chat and no user-generated content.
```

## Points que les relecteurs posent souvent

| Sujet | Réponse |
| --- | --- |
| Pourquoi la position ? | Voir `permissions.md` : lieux proches, premier plan, coordonnées jamais stockées côté serveur. |
| Pourquoi l'audio en arrière-plan ? | Lecture d'histoires écran verrouillé, rien d'autre en arrière-plan. |
| Compte et suppression | Compte facultatif ; la suppression de compte dans l'app est **requise avant la publication publique** (F-22 / T-507). Ne pas soumettre en production tant que ce n'est pas livré. |
| Connexion avec un tiers (Apple 4.8) | Aucune connexion sociale, donc « Se connecter avec Apple » n'est pas exigé. |
| Contenu IA | Voir `age-rating.md`. |
| Liens sortants | Wikipédia et vidéos s'ouvrent hors de l'app. |
| Publicité, suivi | Aucun. |
| Contact de revue | [nom, téléphone, e-mail du propriétaire] |
