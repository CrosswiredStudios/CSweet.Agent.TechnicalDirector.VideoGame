# Video Game Technical Director

Owns engine feasibility, runtime architecture, performance budgets, technical standards, and technical approvals.

## Contract

- Package ID: `com.csweet.video-game-technical-director`
- Version: `1.0.0`
- Provides: `video-game.technical-director.execute.v1`
- Activation: manual
- Requested platform/provider capabilities: none
- Event subscriptions: none
- Network access: none

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.TechnicalDirector.VideoGame -- --self-test
```

The tests run entirely in memory and require no C-Sweet instance or credentials.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 4.0.0.
