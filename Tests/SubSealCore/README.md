# SubSeal offline source regression suite

Run with `dotnet run --project Tests/SubSealCore/SubSealCore.csproj --configuration Release` on .NET 8 or use `.github/workflows/subseal-core.yml` on GitHub Actions.

This project directly compiles the real production source files `contentserver/CSharp/Server/Leases.cs`, `Wire/Protocol.cs`, `Wire/TransferSeal.cs`; only Barotrauma-specific entity/time dependencies are replaced with simple deterministic test stubs. The test program verifies the authenticated-seal roundtrip and tamper detection, key wrapping, contract invariants, per-client lease quotas, ACK ownership, consumption, per-connection revocation, expiration, key wiping uint ID wraparound, and transactional ECDH session replacement.

The workflow also verifies that the client and server protocol files are byte-identical.

**Limitations:** This does not compile the game-dependent `TransferPatches.cs`, `WallCollision`, or full Barotrauma client/server modules; it does not substitute for a live multiplayer/physics regression test. The offline tests protect selected pure C# invariants only. Their existence is not a PASS result until a runner actually executes them and reports success.
