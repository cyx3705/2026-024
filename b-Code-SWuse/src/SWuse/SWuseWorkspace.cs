using System.IO;

namespace SWuse;

internal static class SWuseWorkspace
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SWuseWorkspace");

    public static string DefaultProgram => """
        using SWuse.Api;

        [SwuseEntry]
        public sealed class DemoPart : PartProgram
        {
            public override void Build(PartBuilder part)
            {
                var baseSketch = part.Sketch("Base", ReferencePlane.Front, sketch =>
                    sketch.CenteredRectangle(0, 0, 0.06, 0.04));
                var baseFeature = part.Extrude("BaseBoss", baseSketch, 0.01);

                var holeSketch = part.Sketch("Hole", ReferencePlane.Front, sketch =>
                    sketch.Circle(0, 0, 0.005));
                part.CutExtrude("CenterHole", holeSketch, 0.01);
            }
        }
        """;

    public static void Initialize(string workspacePath)
    {
        Directory.CreateDirectory(workspacePath);
        var sourcePath = Path.Combine(workspacePath, "DemoPart.cs");
        if (!File.Exists(sourcePath))
            File.WriteAllText(sourcePath, DefaultProgram);
    }

    public static IReadOnlyList<string> SourceFiles(string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
            return [];
        return Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string CreateHelperClass(string workspacePath)
    {
        Directory.CreateDirectory(workspacePath);
        var index = 1;
        string path;
        do
        {
            path = Path.Combine(workspacePath, $"Helper{index}.cs");
            index++;
        } while (File.Exists(path));
        File.WriteAllText(path, """
            namespace SWuseUser;

            public static class Helper
            {
                public static double Millimeters(double value) => value / 1000d;
            }
            """);
        return path;
    }

    private static bool IsIgnored(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
