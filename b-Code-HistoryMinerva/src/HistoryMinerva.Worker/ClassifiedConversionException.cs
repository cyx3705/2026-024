using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed class ClassifiedConversionException : Exception
{
    public ClassifiedConversionException(
        ConversionErrorClass errorClass,
        string message,
        Exception? innerException = null) : base(message, innerException)
    {
        ErrorClass = errorClass;
        if (innerException is not null)
            HResult = innerException.HResult;
    }

    public ConversionErrorClass ErrorClass { get; }
}
