namespace SE2SW;

public sealed record ProjectLayout(
    string ProjectRoot,
    string SourceDirectory,
    string XtDirectory,
    string SolidWorksDirectory,
    string UnusedXtDirectory,
    string UnusedSolidWorksDirectory);

public sealed record DirectoryMove(string Source, string Destination);

public sealed record ScanCandidate(
    string SourcePath,
    string XtPath,
    string SolidWorksPath,
    bool HasExistingOutput);
