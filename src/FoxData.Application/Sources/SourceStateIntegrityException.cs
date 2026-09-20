namespace FoxData.Application.Sources;

public sealed class SourceStateIntegrityException : Exception
{
    public SourceStateIntegrityException(string message)
        : base(message)
    {
    }
}
