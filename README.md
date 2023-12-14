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

## Acknowledgements 

- [BepInEx/BepInEx.NuGetUpload.Service](https://github.com/BepInEx/BepInEx.NuGetUpload.Service) for inspiration and publicising logic.
