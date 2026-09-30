# CLAUDE.md — ON.VOYAGE

Lire dans cet ordre avant toute tâche :

1. `.github/copilot-instructions.md` (architecture, style, vie privée, contenu IA) et `.github/instructions/*.instructions.md`.
2. `docs/CAHIER_DES_CHARGES.md` : §0, §2, l'epic concernée, §23, puis la tâche au §24.
3. `docs/adr/` et `docs/questions/`.

Stack : .NET 10, Aspire, WolverineFx, MAUI Blazor Hybrid (Android, iOS) + PWA Blazor WebAssembly, tous deux sur la RCL `OnVoyage.UI.Components`.

Commandes : `dotnet build OnVoyage.slnx`, `dotnet test OnVoyage.slnx`, `dotnet format OnVoyage.slnx --verify-no-changes`, `dotnet run --project src/Aspire/OnVoyage.AppHost`.

Les projets MAUI (`src/Mobile/OnVoyage.App`) ne sont pas dans `OnVoyage.slnx` : ils exigent les workloads MAUI. Voir `docs/MOBILE.md`.
