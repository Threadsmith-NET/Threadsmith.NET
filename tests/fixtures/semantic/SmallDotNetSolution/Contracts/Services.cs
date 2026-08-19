namespace SmallSolution.Contracts;

public interface IService
{
    string GetValue();
}

public sealed class Service : IService
{
    public string GetValue() => "value";
}
