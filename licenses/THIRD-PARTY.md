# Third-party components (shipped next to HallowTorrents.exe)

All from NuGet (nuget.org), .NET Framework builds (MonoTorrent's `lib/net472`, the others `lib/netstandard2.0`, Numerics.Vectors `lib/net46`).
All are MIT licensed - the licence texts are in this folder.

| Package | Version | Licence | nupkg SHA-256 |
|---|---|---|---|
| MonoTorrent (Alan McGovern, Olivier Dufour) - includes Mono.Nat.dll | 3.0.2 | MIT | 4765c03424515407b598ecdc7165784a8f39910603536a5649639fadb5c08a0c |
| Mono.Nat (dependency; MonoTorrent ships the same DLL) | 3.0.0 | MIT | 287e3d475610fbc6bfaf3c3050fac4f364104e2f27c5073a6f4296cb2f939e9e |
| ReusableTasks | 4.0.0 | MIT | f19eb041a3d0317645aaf4a2f562f98dd98b1e89672d0c199a61b4f8e5323dea |
| StandardSocketsHttpHandler | 2.2.0.4 | MIT | b6415ba3af289f98443a41ce79a8a16a96566be48c72e074e6f3e043ecc893f9 |
| System.Memory | 4.5.1 | MIT (.NET Foundation) | ec9850352bc4e898a0335aa69a1cce5f736267a7a4f36478c2140d6fe1690738 |
| System.Buffers | 4.4.0 | MIT (.NET Foundation) | 293c408586b0146d95e555f58f0de9cf1dc8ad05d1827d53b2f8233e0c406ea0 |
| System.Numerics.Vectors | 4.4.0 | MIT (.NET Foundation) | 6ae5d02b67e52ff2699c1feb11c01c526e2f60c09830432258e0809486aabb65 |
| System.Runtime.CompilerServices.Unsafe | 4.5.0 | MIT (.NET Foundation) | 83d8c8750b57480858f9ecd0b583601c4528411dc7cf39c7b3724ccc3f5b8a9e |

MonoTorrent 3.0.2 is the newest stable release (the later 3.0.3 / 3.9.0 builds on NuGet are alpha); it has a .NET Framework 4.7.2
build, which runs on .NET Framework 4.8. Font Awesome Free icons: CC BY 4.0, see FontAwesome-LICENSE.txt.
