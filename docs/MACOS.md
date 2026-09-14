# macOS build and release notes

## What is required

An Apple developer certificate is not required to build or test Instant Mesh+
on a Mac. The ordinary development path needs:

- macOS 12.4 or newer and Rhino 8;
- Xcode Command Line Tools;
- CMake 3.25 or newer;
- the .NET 8 SDK;
- PowerShell 7 only when building a Yak archive.

Clone all submodules, then build both native slices and the managed plug-in:

```bash
git submodule update --init --recursive
bash scripts/build-macos.sh Debug
```

The script builds `arm64` and `x86_64` separately, combines the worker and
oneTBB runtime into universal binaries, validates their architecture and
runtime linkage, builds the managed solution, and runs the integration tests.
The Grasshopper payload is written to:

```text
src/InstantMeshPlus.Grasshopper/bin/Debug/net8.0
```

It contains the `.gha`, managed core, extensionless
`InstantMeshPlus.Worker`, and `libtbb.12.dylib`.

## Unsigned Yak package

For local testing or a test-server distribution, an unsigned package is a
valid starting point:

```powershell
./scripts/Build-YakPackage.ps1 -Version 0.1.3 -Platform mac
./scripts/Validate-YakPackages.ps1 `
  -Root ./artifacts/yak/mac `
  -Version 0.1.3 `
  -ExpectedPlatform mac
```

The result is
`artifacts/yak/mac/instantmeshplus-0.1.3-rh8_32-mac.yak`. The validator checks
that both Mach-O files contain `arm64` and `x86_64`, retain Unix executable
permissions, and do not contain Windows runtime files.

Because the worker is a child executable rather than only managed plug-in
code, an unsigned download may cause a Gatekeeper warning or require a user to
approve it. That affects first-run convenience, not whether the plug-in can be
developed.

## Optional Developer ID signing and notarization

Signing is deliberately outside the normal build path. To enable it for a CI
release, set the repository variable `ENABLE_MACOS_SIGNING` to `true` and add
these repository secrets:

- `MACOS_CERTIFICATE_P12`: base64-encoded Developer ID Application certificate
  and private key exported as `.p12`;
- `MACOS_CERTIFICATE_PASSWORD`: password protecting that export;
- `APPLE_API_KEY_P8`: base64-encoded App Store Connect API key;
- `APPLE_API_KEY_ID`: API key identifier;
- `APPLE_API_ISSUER_ID`: App Store Connect issuer identifier;
- `APPLE_TEAM_ID`: Apple developer team identifier.

`scripts/sign-notarize-macos.sh` creates a temporary keychain, signs the oneTBB
library and worker with hardened runtime and a timestamp, submits a ZIP to
Apple's notary service, requires an `Accepted` result, writes the notary log
beside the payload, and removes temporary credentials. Standalone executables
cannot have the ticket stapled, so Gatekeeper retrieves the accepted ticket
online.

The manual **Test Yak package** workflow also has a `sign_macos` option. Leave
it disabled until the Apple credentials exist. The public tag workflow follows
`ENABLE_MACOS_SIGNING`; an unsigned public release remains possible when the
variable is absent or false.

## CI coverage

The normal build workflow performs all of the following without publishing:

1. builds, tests, packages, and validates Windows x64;
2. builds a universal macOS payload and tests it on Apple Silicon;
3. extracts the produced Mac Yak archive and runs the same worker integration
   tests on a native Intel runner;
4. validates that exactly one Windows and one macOS distribution share the
   same version, Rhino floor, manifest, GUID, and common payload.

Test-server and public Yak pushes remain separate guarded workflows. Creating
a local package or a CI artifact does not push anything to Yak.
