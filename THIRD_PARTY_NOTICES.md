# Third-party software

SFSearch is released under the GNU General Public License version 3 (see `LICENSE`), because the libraries it is built on require that.

Most of these libraries are downloaded from nuget.org when you build SFSearch. The exception is Mutagen: SFSearch needs a build of it with fixes that aren't in a published version yet, so its three packages are in `packages/`. Their full source is public, as GPL-3.0 asks: https://github.com/Krawphish37/Mutagen, branch `starfield-fixes5` (Mutagen's upstream code at commit 9da101acf2847a36d5b8e667c1bb92dedf981ca3, plus the changes on that branch).

| Library | Version | License | Source |
|---|---|---|---|
| Mutagen.Bethesda.Starfield, .Core, .Kernel | 0.55.0-fixes.5 (modified; in `packages/`) | GPL-3.0-only | https://github.com/Krawphish37/Mutagen/branches (branch starfield-fixes5) (upstream: https://github.com/Mutagen-Modding/Mutagen) |
| Noggog.CSharpExt | 4.3.0 | GPL-3.0 | https://github.com/Noggog/CSharpExt |
| Loqui | 3.7.0 | GPL-3.0 | https://github.com/Noggog/Loqui |
| GameFinder.Common, .RegistryUtils, .StoreHandlers.GOG, .StoreHandlers.Steam, .StoreHandlers.Xbox, .Wine | 4.9.0 | GPL-3.0-only | https://github.com/erri120/GameFinder |
| NexusMods.Paths | 0.19.1 | GPL-3.0-only | https://github.com/Nexus-Mods/NexusMods.Paths |
| Reloaded.Memory | 9.4.2 | GPL-3.0 (parts MIT) | https://github.com/Reloaded-Project/Reloaded.Memory |
| Microsoft.Data.Sqlite, .Core | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyInjection.Abstractions, .Logging.Abstractions | 9.0.4 | MIT | https://github.com/dotnet/runtime |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (inside e_sqlite3) | | Public domain | https://www.sqlite.org/copyright.html |
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
| .NET runtime (inside the ready-made download only) | 10.0 | MIT | https://github.com/dotnet/runtime |

## Not included

- **Starfield and its plugins, and any mod's plugins or assets.** SFSearch ships none of them. It reads the copies already on your computer, and the databases it builds stay on your computer. Don't share them: they hold the game's and mods' data.
- **Champollion**, a Papyrus script decompiler (LGPL-3.0, https://github.com/Orvid/Champollion). `Sources --scripts` lists scripts worth reading; you need your own copy of a decompiler to read them.
| ModelContextProtocol, .Core (the MCP adapter, `SFSearch.Mcp`) | 2.2.0 | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.Hosting and its dependencies (the MCP adapter) | 10.0.10 | MIT | https://github.com/dotnet/runtime |

Not included: Champollion (the Papyrus decompiler the `Scripts` command uses, LGPL-3.0). Download it yourself from https://github.com/Orvid/Champollion/releases.
