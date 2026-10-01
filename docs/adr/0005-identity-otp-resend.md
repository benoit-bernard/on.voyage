# ADR-0005 — Identité maison : session anonyme + code OTP par e-mail (Resend) — T-004

- Statut : accepté (décision du propriétaire produit, 2026-10-01). **Remplace Supabase Auth** (D-04, §9.1, §10 du cahier des charges) : on se contente d'un code OTP envoyé par e-mail pour créer un compte.
- Contexte : Supabase Auto-hébergé ajoutait GoTrue, un SMTP et une dépendance d'exploitation pour un besoin simple (F-01). Aucun mot de passe, aucune connexion Apple/Google/Facebook n'est prévu.

## Décision
- **Platform** porte l'identité (schéma `platform` : `account`, `otp_challenge`, `refresh_token`). L'`id` du compte est le `traveler_id` de tous les services.
- **Session anonyme** au premier lancement, sans saisie : `POST /api/platform/v1/auth/anonymous`.
- **Code OTP** : `POST /auth/otp/request` (6 chiffres, CSPRNG, valable 10 min, 5 essais, renvoi après 60 s, 5 codes par adresse et par heure — clés `auth.*` et `security.otp_per_email_per_hour` de l'annexe E). Seul un HMAC du code est stocké. La réponse est toujours 202 : elle ne révèle pas si l'adresse a déjà un compte.
- **Liaison** : `POST /auth/otp/verify` avec la session anonyme conserve le `traveler_id` (profil, consentements, historique restent attachés). Si l'adresse a déjà un compte (réinstallation, nouvel appareil), la session devient celle du compte existant et la session anonyme de l'appareil est retirée (`replaced_by`, jetons révoqués). Changer d'adresse n'est pas géré : 409 `email_already_linked`.
- **Jetons** : accès JWT HS256 d'une heure (`sub`, `is_anonymous`, `email_verified`, `roles`), signé avec `Auth:JwtSecret` (paramètre secret Aspire, jamais commité) et validé par le Gateway **et** chaque service (SEC-02). Refresh opaque à usage unique, haché en base, 90 jours ; la rotation détecte le rejeu et révoque toute la famille.
- **Politiques** (SEC-03) : `traveler`, `account` (e-mail vérifié), `admin`, `creator`, `internal`. Le premier administrateur s'obtient en listant son adresse dans `Auth:BootstrapAdminEmails` : le rôle est accordé à la vérification du code.
- **E-mail** : `IEmailSender` ; `ResendEmailSender` appelle l'API HTTP de Resend (clé `Email:Resend:ApiKey`, expéditeur `Email:From`, domaine à vérifier chez Resend). Le fournisseur `log` affiche le code dans le journal et n'est accepté qu'en Development (le démarrage échoue sinon).
- **Clients** : `SessionService` (App.Core) crée et rafraîchit la session, une seule actualisation à la fois ; `BearerTokenHandler` ajoute le jeton et `X-App-Version`, réessaie une fois après un 401. Stockage : `SecureStorage` (Android Keystore, iOS Keychain) dans l'app MAUI ; `localStorage` dans la PWA (exposé au XSS, d'où l'absence de script tiers).

## Conséquences et écarts
- **Resend est un nouveau sous-traitant** hors liste du §16.4 (qui prévoyait un SMTP européen) : il reçoit l'adresse e-mail et le code. Décision explicite du propriétaire produit ; `docs/PRIVACY.md` et `docs/questions/Q-2026-10-01-resend.md` listent ce qu'il faut encore faire (DPA, région UE).
- Le Gateway valide le jeton et refuse les routes protégées ; la **limitation de débit par IP, le blocage des User-Agents et l'expurgation des traces du Gateway (reste de T-003) ne sont pas faits** — la création de sessions anonymes n'est donc pas encore limitée.
- Pas de suppression de compte ni d'export (T-507) ; la purge des comptes anonymes inactifs (24 mois) reste à faire. `account.last_active_at` est déjà tenu à jour au rafraîchissement.
- Pas de rôle `creator` attribué (arrive avec `CreatorTermsAcceptedV1`).
- Les entrées `X-Admin-Key` / `X-Traveler-Id` du T-007 sont supprimées.
