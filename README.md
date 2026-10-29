<p align="center">
  <img width="1920" alt="lt" src="https://github.com/user-attachments/assets/658f539a-f4a9-4ad5-a3a2-6bb7aa6809bd" />

</p>

# LuaTools
<p>
  <img align="right" height="250" src="https://github.com/user-attachments/assets/df083fb0-9be7-4690-9f0f-c8b0a73da881" />

  [Discord](https://discord.gg/luatools) • [Website](https://lua.tools) • [Git Mirror](https://git.lua.tools/luatools)
  
  A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 8.
    
  LuaTools browses and installs manifest sources, edits `stplug-in` lua files (depot pinning,
  per-depot enable/disable), manages unlocker modes, and injects a companion plugin into Steam's
  store pages.
  
  It ships fully translated in 29 languages and auto-updates via Velopack.
  <br><sub>Found a translation error? Tell us about it over on [Discord](https://discord.gg/luatools)</sub>
</p>

## Statistics
<div>
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Setup.exe?displayAssetName=true&style=for-the-badge" />
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Portable.zip?displayAssetName=true&style=for-the-badge" />
</div>

<a href="https://www.star-history.com/?repos=madoiscool%2Fluatools&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&theme=dark&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
 </picture>
</a>

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the released installer bundles a
  check for the .NET 8 **Desktop Runtime** and installs it if missing; [building from source](https://github.com/madoiscool/LuaTools/blob/main/CONTRIBUTING.md#building-from-source--developing) needs
  the full SDK

## Installation
You can find release builds on the [luatools website](https://lua.tools/app) or in the [releases](https://github.com/madoiscool/LuaTools/releases/latest) tab. 

## Credits / Adjacent software

- [Millennium](https://steambrew.app/): the Steam plugin framework whose injection API this app
  polyfills when Millennium isn't installed
- [Velopack](https://velopack.io/): installer and auto-update framework
- [DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod): downloads depot content
  from Steam's CDN, powering the Depots page's Download action. A fork of
  [DepotDownloader](https://github.com/SteamRE/DepotDownloader), fetched and run as a standalone tool
- [SteamAutoCrack](https://github.com/SteamAutoCracks/Steam-auto-crack): fetched and launched from the
  Downloads page
- [Steamless](https://github.com/atom0s/Steamless): removes SteamStub from game executables
- [CloudRedirect](https://github.com/Vigno04/CloudRedirect): Steam Cloud revival project, can be turned on via the mode page

## Licence

MIT. See [LICENSE](LICENSE).

<img width="100%" alt="928c14bad5bbc258894b050af1e17ba8" src="https://github.com/user-attachments/assets/90ed4a2b-6fec-4afa-a56d-4983ea190ddb" />

