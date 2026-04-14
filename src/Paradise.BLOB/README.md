# Paradise.BLOB

`Paradise.BLOB` is a standalone binary blob builder library for Paradise Engine.
It ports the .NET-only functionality from `quabug/BLOB` into a Paradise-style repository layout with NativeAOT-friendly TUnit tests.

## Projects

- `src/Paradise.BLOB` - the blob types and builders.
- `src/Paradise.BLOB.Test` - TUnit coverage for the standalone builder API.

## Build

```bash
dotnet build src/Paradise.BLOB/Paradise.BLOB.csproj
dotnet test --project src/Paradise.BLOB.Test/Paradise.BLOB.Test.csproj -p:PublishAot=false --output normal
```

## NativeAOT tests

```bash
dotnet publish src/Paradise.BLOB.Test/Paradise.BLOB.Test.csproj -c Release -r osx-arm64
./src/Paradise.BLOB.Test/bin/Release/net10.0/osx-arm64/publish/Paradise.BLOB.Test --output Detailed
```
