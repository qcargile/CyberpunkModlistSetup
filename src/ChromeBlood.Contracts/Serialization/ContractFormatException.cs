namespace ChromeBlood.Contracts.Serialization;

public sealed class ContractFormatException : Exception
{
    public ContractFormatException(string message)
        : base(message)
    {
    }

    public ContractFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
