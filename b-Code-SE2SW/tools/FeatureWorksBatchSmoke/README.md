# FeatureWorksBatchSmoke

`FeatureWorksBatchSmoke` is a non-interactive diagnostic for Solid Edge parts.
By default it runs each requested `.par` in a separate production `SE2SW.Worker` process through:

```text
fixture copy -> Solid Edge export -> XT -> SolidWorks import -> FeatureWorks -> SLDPRT
```

The original fixture is never opened by the Worker. The smoke copies it to a private temporary
directory and verifies the original and copied SHA-256 hashes afterward. It also verifies that
the Worker created non-empty XT and SLDPRT outputs and that no new `Edge` or `SLDWORKS` process
remains after the test.

The JSON report has two distinct outcomes:

- `success`: the production pipeline completed without a Worker/CAD failure for that part.
- `recognitionHealthy`: FeatureWorks actually created one or more features without degradation.

`recognitionHealthy: false` does not by itself mean that the part crashed the conversion. It
means that the report must be read for a FeatureWorks fallback or diagnostic.

## Build

```powershell
dotnet build .\tools\FeatureWorksBatchSmoke\FeatureWorksBatchSmoke.csproj -c Release -p:NuGetAudit=false
```

## Run

```powershell
.\tools\FeatureWorksBatchSmoke\bin\Release\net8.0-windows\win-x64\FeatureWorksBatchSmoke.exe `
  --worker .\src\SE2SW.Worker\bin\Release\net8.0-windows\win-x64\SE2SW.Worker.exe `
  --part "C:\fixtures\test-part-5.par" `
  --part "C:\fixtures\test-part-6.par" `
  --keep
```

The default timeout is 180 seconds per part. `--keep` preserves the temporary directory even
when the test succeeds; failed runs always retain it for diagnosis.

Add `--single-batch` to send every requested part to one outer production Worker. This is the
V3.5.2 regression mode: the outer Worker must export the batch and isolate each FeatureWorks
import in its own child Worker without losing progress events or leaving CAD processes behind.
Add `--require-healthy-recognition` to make any FeatureWorks degradation, geometry change, or
session fault fail the smoke instead of appearing only in the diagnostic fields.
