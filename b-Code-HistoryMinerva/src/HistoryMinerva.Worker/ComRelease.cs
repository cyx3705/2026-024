using System.Runtime.InteropServices;

namespace SE2SW.Worker;

internal static class ComRelease
{
    public static void One(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;
        try
        {
            _ = Marshal.ReleaseComObject(instance);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    public static void Final(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;
        try
        {
            Marshal.FinalReleaseComObject(instance);
        }
        catch (InvalidComObjectException)
        {
        }
    }
}
