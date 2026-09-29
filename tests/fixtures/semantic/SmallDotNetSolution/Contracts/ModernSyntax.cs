namespace SmallSolution.Contracts;

public sealed class AnalyzerProbe;

public sealed class FieldBackedValue
{
    public string Value
    {
        get => field;
        set => field = value.Trim();
    } = string.Empty;
}

public static class ServiceExtensions
{
    extension(IService service)
    {
        public string UpperValue => service.GetValue().ToUpperInvariant();

        public string Repeat(int count) => string.Concat(Enumerable.Repeat(service.GetValue(), count));
    }

    extension(IService)
    {
        public static IService Create() => new Service();
    }
}
