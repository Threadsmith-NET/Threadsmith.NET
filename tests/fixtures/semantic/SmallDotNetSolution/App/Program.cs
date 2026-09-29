using SmallSolution.Contracts;

IService service = new Service();
Console.WriteLine(service.GetValue());
Console.WriteLine(service.UpperValue);
Console.WriteLine(IService.Create().Repeat(2));
