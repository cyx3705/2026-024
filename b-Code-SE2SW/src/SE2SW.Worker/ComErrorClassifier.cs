using System.Runtime.InteropServices;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class ComErrorClassifier
{
    public static ConversionErrorClass Classify(Exception exception, ConversionErrorClass fallback)
    {
        if (exception is ClassifiedConversionException classified)
            return classified.ErrorClass;
        var hResult = unchecked((uint)(exception is COMException com ? com.HResult : exception.HResult));
        return hResult switch
        {
            0x80010001 or 0x8001010A => ConversionErrorClass.CallRejected,
            0x80080005 => ConversionErrorClass.AppLaunchFailed,
            0x80040154 or 0x800401F3 => ConversionErrorClass.ComNotRegistered,
            0x80070005 => ConversionErrorClass.LicenseUnavailable,
            _ => fallback,
        };
    }
}
