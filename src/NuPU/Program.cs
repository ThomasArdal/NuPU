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
            var app = new CommandApp<UpdateCommand>();
            return await app.RunAsync(args);
        }
    }
}
