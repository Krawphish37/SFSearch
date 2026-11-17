# Third-party software

SFSearch is released under the GNU General Public License version 3 (see `LICENSE`), because the libraries it is built on require that.

Most of these libraries are downloaded from nuget.org when you build SFSearch. The exception is Mutagen and its serializer: SFSearch needs builds of them with fixes that aren't in a published version yet, so their six packages are in `packages/`. Their full source is public, as GPL-3.0 asks:
- **Mutagen** (Mutagen.Bethesda.Starfield, .Core, .Kernel `0.55.0-fixes.25`): https://github.com/Krawphish37/Mutagen, branch `fixes25` (Mutagen's upstream code at commit 9da101acf2847a36d5b8e667c1bb92dedf981ca3, plus the changes on that branch).
- **Mutagen.Bethesda.Serialization** (with .Newtonsoft and .SourceGenerator, `1.38.6-fixes.7`): https://github.com/Krawphish37/Mutagen.Bethesda.Serialization, branch `fixes/1.38.6-fixes.7` (the upstream code at tag 1.38.6, commit 55675ef962e7e234fa015122e37f5c1019a04db3, plus the changes on that branch).

Each package's `.nuspec` names the commit it was built from.

## SFSearch (`SFSearch.exe` and its libraries)
| Library | Version | License | Source |
|---|---|---|---|
| Mutagen.Bethesda.Starfield, .Core, .Kernel | 0.55.0-fixes.25 (modified; in `packages/`) | GPL-3.0-only | https://github.com/Krawphish37/Mutagen (branch `fixes25`; upstream: https://github.com/Mutagen-Modding/Mutagen) |
| Mutagen.Bethesda.Serialization, .Newtonsoft | 1.38.6-fixes.7 (modified; in `packages/`) | GPL-3.0-only | https://github.com/Krawphish37/Mutagen.Bethesda.Serialization (branch `fixes/1.38.6-fixes.7`; upstream: https://github.com/Mutagen-Modding/Mutagen.Bethesda.Serialization, tag 1.38.6, commit 55675ef962e7e234fa015122e37f5c1019a04db3) |
| Mutagen.Bethesda.Serialization.SourceGenerator (used while building only: it writes code into `SFSearch.Records`) | 1.38.6-fixes.7 (modified; in `packages/`) | GPL-3.0-only | as above |
| Noggog.CSharpExt | 4.3.0 | GPL-3.0 | https://github.com/Noggog/CSharpExt |
| Loqui | 3.7.0 | GPL-3.0 | https://github.com/Noggog/Loqui |
| GameFinder.Common, .RegistryUtils, .StoreHandlers.GOG, .StoreHandlers.Steam, .StoreHandlers.Xbox, .Wine | 4.9.0 | GPL-3.0-only | https://github.com/erri120/GameFinder |
| NexusMods.Paths | 0.19.1 | GPL-3.0-only | https://github.com/Nexus-Mods/NexusMods.Paths |
| Reloaded.Memory | 9.4.2 | GPL-3.0 (parts MIT) | https://github.com/Reloaded-Project/Reloaded.Memory |
| Microsoft.Data.Sqlite, .Core | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyInjection.Abstractions, .Logging.Abstractions | 9.0.4 | MIT | https://github.com/dotnet/runtime |
| System.IO.Hashing | 10.0.0 | MIT | https://github.com/dotnet/runtime |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (inside e_sqlite3) | | Public domain | https://www.sqlite.org/copyright.html |
| Newtonsoft.Json | 13.0.4 | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| System.Data.HashFunction.Core, .Interfaces, .xxHash | 2.0.0 | MIT (the project's LICENSE; the nuspec only links it) | https://github.com/brandondahler/Data.HashFunction |
| DynamicData | 9.4.31 | MIT | https://github.com/reactivemarbles/DynamicData |
| FluentResults | 3.15.2 | MIT | https://github.com/altmann/FluentResults |
| ini-parser-netstandard | 2.5.3 | MIT | https://github.com/rickyah/ini-parser |
| K4os.Compression.LZ4, .Streams | 1.3.8 | MIT | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 |
| K4os.Hash.xxHash | 1.0.8 | MIT | https://github.com/MiloszKrajewski/K4os.Hash.xxHash |
| OneOf | 3.0.271 | MIT | https://github.com/mcintyre321/OneOf |
| SharpZipLib | 1.4.2 | MIT | https://github.com/icsharpcode/SharpZipLib |
| StrongInject | 1.4.4 | MIT | https://github.com/YairHalberstadt/stronginject |
| System.IO.Abstractions, TestableIO.System.IO.Abstractions, .Wrappers | 22.1.1 | MIT | https://github.com/TestableIO/System.IO.Abstractions |
| System.Reactive | 6.1.0 | MIT | https://github.com/dotnet/reactive |
| Testably.Abstractions.FileSystem.Interface | 10.1.0 | MIT | https://github.com/Testably/Testably.Abstractions |
| TransparentValueObjects.Abstractions | 1.1.0 | MIT | https://github.com/erri120/TransparentValueObjects |
| ValveKeyValue | 0.13.1.398 | MIT | https://github.com/ValveResourceFormat/ValveKeyValue |
| NETStandard.Library, Microsoft.NETCore.Platforms (restore metadata only: nothing of them is in the build) | 1.6.1, 1.1.0 | .NET Library License (as their nuspec links it) | https://github.com/dotnet/standard |

## The MCP server (`SFSearch.Mcp.exe`)
| Library | Version | License | Source |
|---|---|---|---|
| ModelContextProtocol, .Core | 2.2.0 | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.Hosting and its dependencies (.Hosting.Abstractions, .Configuration and its providers, .DependencyInjection, .Diagnostics, .FileProviders, .FileSystemGlobbing, .Logging and its providers, .Options, .Primitives, .Caching.Abstractions), System.Diagnostics.EventLog | 10.0.10 | MIT | https://github.com/dotnet/runtime |

## The tests (not part of SFSearch itself)
The test projects also use xunit and xunit.runner.visualstudio (Apache-2.0, https://github.com/xunit/xunit) and Microsoft.NET.Test.Sdk (MIT, https://github.com/microsoft/vstest). They are downloaded from nuget.org when you build the tests.

The .NET runtime (MIT, https://github.com/dotnet/runtime) is not in the repository: you install the .NET 10 SDK yourself. A ready-made download, where a release has one, carries it.

## The Creation Kit's names
The display names SFSearch shows beside the engine's names (field, type and enum names, their tooltips, ranges and the "hidden" marks) are in `SFSearch.SchemaGen/inputs/ck-names-1.16.244.0.tsv`. They come from the Starfield Creation Kit 1.16.244.0, © Bethesda Softworks. The file holds names only, no game data or code, and they are used for interoperability: to show a field under the name modders see in the Creation Kit.

## Not included
- **Starfield and its plugins, and any mod's plugins or assets.** SFSearch ships none of them. It reads the copies already on your computer, and the working files it makes stay on your computer. Don't share them: they hold the game's and mods' data.
- **Champollion**, a Papyrus script decompiler (LGPL-3.0, https://github.com/Orvid/Champollion). The `scripts2` report uses it to read scripts: download it yourself from https://github.com/Orvid/Champollion/releases.
