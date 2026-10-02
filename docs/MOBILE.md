# Mobile et PWA — compiler et lancer

Une seule interface Razor (`OnVoyage.UI.Components`, sans dépendance MAUI) est hébergée par :

| Cible | Projet | Technologie |
| --- | --- | --- |
| Android | `src/Mobile/OnVoyage.App` (`net10.0-android`) | .NET MAUI 10 + `BlazorWebView` |
| iPhone / iPad | `src/Mobile/OnVoyage.App` (`net10.0-ios`, **sur macOS uniquement**) | idem |
| Web installable (PWA) | `src/Web/OnVoyage.Web.Pwa` | Blazor WebAssembly, manifeste + service worker hors ligne |

La logique client (classement, profil, flux d'accueil) est dans `OnVoyage.App.Core`, testée sans MAUI. Le client HTTP (`OnVoyage.App.Infrastructure`) n'appelle que le Gateway.

## Lancer en développement

```bash
dotnet run --project src/Aspire/OnVoyage.AppHost      # Gateway :5080, Catalog, PWA :5090
```

- PWA : ouvrir `http://localhost:5090`.
- Android (émulateur) : `dotnet build src/Mobile/OnVoyage.App -f net10.0-android -t:Run`. L'émulateur joint le Gateway via `10.0.2.2:5080`.
- iOS (Mac) : `dotnet build src/Mobile/OnVoyage.App -f net10.0-ios -t:Run`.

Prérequis : `dotnet workload install maui-android` (et `maui-ios` sur Mac), JDK 21, SDK Android. `OnVoyage.App` n'est pas dans `OnVoyage.slnx` (il exige les workloads) ; la CI `mobile.yml` le compile.

## État de vérification Android (2026-10-02)

Environnement d'essai : Linux x64, SDK .NET 10.0.112, workload `maui-android` 10.0.110 (Microsoft.Android.Sdk 36.1.69), OpenJDK 21.

Commandes (à lancer avec le SDK Android installé) :

```bash
dotnet workload install maui-android
dotnet build src/Mobile/OnVoyage.App -f net10.0-android -c Debug -m:1 -p:AndroidPackageFormat=apk   # APK signé debug dans bin/Debug/net10.0-android/
dotnet build src/Mobile/OnVoyage.App -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=True
```

Vérifié : l'installation du workload, JDK 21, et le test `MobileWiringTests` (`tests/OnVoyage.UI.Components.Tests`) qui construit un `ServiceCollection` avec `AddAppCore`, `AddAppInfrastructure` et `AddLocalData` (implémentations de plateforme remplacées par des doubles) et vérifie que chaque service injecté par un composant Razor se résout, avec `ValidateScopes`.

**Non vérifié : la compilation de `OnVoyage.App` elle-même.** Sur la machine d'essai, `dl.google.com` et `aka.ms` sont bloqués par le proxy sortant : le SDK Android (plateforme 36, build-tools, `aapt2`) ne peut pas être installé, donc `dotnet build -f net10.0-android` s'arrête sur `XA5300` (SDK introuvable). Aucun APK n'a été produit ici ; la CI `mobile.yml` (ubuntu-latest, SDK Android préinstallé) est la première compilation réelle. Aucun émulateur ni appareil : démarrage, permissions de position, audio écran verrouillé, accès à `10.0.2.2:5080` non testés.

Réseau : en Debug, `network_security_config.xml` n'autorise le HTTP en clair que vers `10.0.2.2` ; en Release, seul HTTPS est accepté.

## Compte et connexion

Au premier lancement l'app crée une session anonyme sans rien demander. L'écran **Compte** permet de créer un compte avec un code à 6 chiffres reçu par e-mail (ADR-0005). En développement, le code est affiché dans le journal du service `platform-api` (ligne `DEVELOPMENT sign-in code`) au lieu d'être envoyé ; en staging et production il part par Resend (paramètre secret `resend-api-key`).

## Mise en production de la PWA

Publier avec `dotnet publish src/Web/OnVoyage.Web.Pwa -c Release`, fixer `Gateway:BaseAddress` dans `wwwroot/appsettings.json` (vide = même origine) et autoriser l'origine de la PWA dans `Cors:AllowedOrigins` du Gateway.

## Carte, découverte, audio et données locales

Voir ADR-0010. Pour la carte : renseigner `Map:TilesUrl` (fichier `.pmtiles` servi avec requêtes Range) et ajouter les polices dans `src/Web/OnVoyage.UI.Components/wwwroot/fonts/`. Le mode découverte demande la permission de position uniquement quand on appuie sur le bouton ; dans la PWA il ne fonctionne que page ouverte (le navigateur coupe la position écran verrouillé).

## Retours, envies et destination

Voir ADR-0011. Les interactions (retours, enregistrements, signaux d'écoute, visites) partent vers `POST /api/discovery/v1/me/interactions` ; sur téléphone elles attendent dans la file de `user.db` (le planificateur de 60 s démarre dans `MauiProgram`). Pages : `/envies`, `/destination`, `/carte`.

## Reste à faire pour le MVP-0 mobile

Essai sur appareil (audio écran verrouillé, GPS réel), traces GPX réelles de H-001, fichier PMTiles et polices de la carte, signature des builds (H-003), endpoint d'événements de la plateforme pour vider la file d'envoi.
