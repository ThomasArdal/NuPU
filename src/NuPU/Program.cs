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
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            AnsiConsole.Write(new FigletText("NuPU").Color(new Color(0, 72, 128)));
            var app = new CommandApp<UpdateCommand>();
            app.Configure(config =>
            {
                config.SetApplicationName("nupu");
                config.AddExample(new string[] { });
                config.AddExample(new[] { "--directory c:\\myproject" });
                config.AddExample(new[] { "--recursive false" });
                config.AddExample(new[] { "--package System.Text.Json" });
                config.AddExample(new[] { "--includeprerelease false" });
                config.AddExample(new[] { "--interactive true" });
                config.AddExample(new[] { "-d c:\\myproject", "-r false", "-p System.Text.Json", "-i false", "--interactive true" });
            });
            return await app.RunAsync(args);
        }
    }
}
