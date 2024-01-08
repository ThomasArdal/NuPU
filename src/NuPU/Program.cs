using Spectre.Console;
using Spectre.Console.Cli;
using System.Threading.Tasks;

namespace NuPU
{
    /// <summary>
    /// Main entry point for updating NuGet packages.
    /// Numerous lines in this file are highly inspired (some copied) from NuKeeper (https://github.com/NuKeeperDotNet/NuKeeper)
    /// and dotnet-outdated (https://github.com/dotnet-outdated/dotnet-outdated). For much better and feature rich tools, check those out.
    /// This is private/internal tool for now. It would be worth to consider donating it to either NuKeeper or dotnet-outdated.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            AnsiConsole.Write(new FigletText("NuPU").Color(new Color(0, 72, 128)));
            var app = new CommandApp<UpdateCommand>();
            app.Configure(config =>
            {
                config.SetApplicationName("nupu");
                config.AddExample();
                config.AddExample("--directory c:\\myproject");
                config.AddExample("--recursive false");
                config.AddExample("--package System.Text.Json");
                config.AddExample("--includeprerelease false");
                config.AddExample("--interactive true");
                config.AddExample("-d c:\\myproject", "-r false", "-p System.Text.Json", "-i false", "--interactive true");
            });
            return await app.RunAsync(args);
        }
    }
}
