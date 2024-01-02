# NuGet GameLib Dehumidifer

An automated utility that maintains 'GameLibs' packages for Steam games.
> [!WARNING]
> This project is a **work in progress**. 
> Features may be added/removed without notice and important functionality may not be implemented at all. 

## About the Project

### What the heck is a 'GameLibs' package?

A GameLibs package is a publicly available NuGet package that provides stubbed .NET assemblies for a game.

They are invaluable to modders as they significantly reduce the complexity of configuring CI/CD for their 
projects without violating copyright laws or Steam terms of service by redistributing proprietary intellectual property.

### What's with the name 'Dehumidifier'?

Because I have an elite sense of humour (I am not interested in differences of opinion at this time) - I like to imagine that 
this project 'condenses' the Steam games into their GameLibs packages.

### Why build this utility? 

A large collection of GameLibs is maintained by modding communities and the [BepInEx organisation](https://github.com/BepInEx). 
They self-host a NuGet feed to distribute those packages [here](https://nuget.bepinex.dev).
However, due to the complexity of moderating the platform, the BepInEx team stopped accepting package maintainer
applications for new GameLibs packages in January 2023. 

This project aims to supercede the service previously provided by the BepInEx team.
View the source code for their utility here: 
[BepInEx.NuGetUpload.Service](https://github.com/BepInEx/BepInEx.NuGetUpload.Service).

The Dehumidifier project is intended to largely maintain its packages automatically, 
differentiating it from BepInEx's service which requires significant input from community members 
to keep packages up-to-date.

## How does it work?

It's all powered by GitHub Actions (workflows) and Cake Frosting DSL!

### `checkAllGamesForUpdates`

This workflow runs nightly, dispatching the `checkGameForUpdates` workflow for each game directory in the repository's `Games` folder.

### `checkGameForUpdates`

This workflow:
1. fetches the steam app info for the target game.
2. fetches the available NuGet package versions for the target game.
3. if the current game version is not recognised (present in the game's `metadata.json`), a pull request is opened to add the version entry.
   As Steam has no consistent info on actual version numbers (just build IDs), the version number must be filled manually before the PR is merged.
4. dispatches the `updateGameVersionPackage` workflow for all game versions found in `metadata.json` that were not found on NuGet.

### `updateGameVersionPacakge`

This workflow:
1. fetches the steam app info for the target game.
2. fetches the available NuGet package versions for the target game.
3. downloads the NuGet dependencies for the target game version.
4. constructs an assembly name blacklist from the NuGet dependencies.
5. downloads the game version's depot from Steam.
6. strips (and publicizes) the game's assemblies.
7. selects the next available pre-release number based on existing NuGet package versions.
8. constructs a NuGet package containing the reference assemblies.
9. pushes the package to [NuGet.org](https://nuget.org).

## Acknowledgements 

- [BepInEx/BepInEx.NuGetUpload.Service](https://github.com/BepInEx/BepInEx.NuGetUpload.Service) for inspiration and publicising logic.
