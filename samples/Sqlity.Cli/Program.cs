namespace Sqlity.Cli;

public static class Program
{
    public static int Main(string[] args) =>
        SqlityCli.Run(args, Console.In, Console.Out, Console.Error, !Console.IsInputRedirected);
}
