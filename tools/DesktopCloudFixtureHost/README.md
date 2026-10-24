# Desktop cloud fixture host

This test-only host references the staged frontend assembly. It does not run the website Startup, Discord workers, production configuration, or Google network login. Only the desktop document/auth, economy and community controllers are registered. The random HTTPS port binds IPv4 loopback only.

From the YoMi_Frontend project root (the directory containing YoMi_Frontend/ and tools/):

```powershell
dotnet build tools/DesktopCloudFixtureHost -m:1 -p:NuGetAudit=false --ignore-failed-sources
dotnet run --no-build --project tools/DesktopCloudFixtureHost -- --self-test --exit-after-test
dotnet run --no-build --project tools/DesktopCloudFixtureHost
```

The run needs normal user LocalDB/certificate-store access (outside the Codex sandbox). It generates a temporary CA (CA=true) and a localhost server certificate without changing the Windows trust store. Only the public CA is written to `fixture-ca.pem`. For Node clients supply this PEM as the explicit `ca` option or set `NODE_EXTRA_CA_CERTS` before starting Node. Electron test code may trust only hostname localhost plus the exact `serverCertificateFingerprint` from fixture.json through its session certificate verifier. Never use a broad certificate bypass. The server private key is imported from in-memory PFX into the temporary Windows user key provider for SChannel; the PFX buffer is cleared and certificate disposal releases the provider key. No PFX or private-key file is written.

`fixture.json` in the YoMi_Frontend project root contains `{url,dbName,certificatePath,certificateThumbprint,serverCertificateFingerprint,certificateTrusted,processId,members}`. Each member has `{memberId,sub,token,role,email,displayName}`. Tokens are synthetic but still omitted from console output. Member 1 is admin with 6543 coins, 1798 active seconds, zero completed sessions/boxes, and neon-lamp inventory. Member 2 is a normal member with existing progress and more inventory. Both have an existing playlist and custom profile field.

The self-test preserves member 1's balance/focus/inventory. It activates authority and exercises preferences/replay, legacy PUT protection, public and admin announcement CRUD, ordinary-member rejection, and two real authenticated WebSockets including join/chat/seat. A separate fresh run starts from the original fixture values.

Graceful shutdown: send POST to `<url>/__fixture/stop` or press Ctrl+C in the running host. The host drops only its generated `YoMiDesktopFixture_<32-hex>` database, marks fixture.json stopped, and removes session tokens from that file. Do not force-kill the process when testing normal cleanup. Runtime generated files are not product deliverables.


Only the .cs, .csproj, README.md and .gitignore files are source deliverables. Do not copy bin/, obj/, fixture-keys/, generated PEM files, self-test-results.json or the project-root fixture.json into product packages.

