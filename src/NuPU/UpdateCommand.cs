using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NuPU
{
    internal class UpdateCommand : AsyncCommand<UpdateCommand.UpdateCommandSettings>
    {
        private const string UpToDate = " [green]up to date[/]";
        private const string NeedsUpdate = " [red]needs update[/]";

        public override async Task<int> ExecuteAsync(CommandContext context, UpdateCommandSettings updateCommandSettings)
        {
            var rootPath = string.IsNullOrWhiteSpace(updateCommandSettings.Directory) || !Directory.Exists(updateCommandSettings.Directory)
                ? Directory.GetCurrentDirectory()
                : updateCommandSettings.Directory;
            var settings = Settings.LoadDefaultSettings(rootPath);
            var enabledSources = SettingsUtility.GetEnabledSources(settings);

            var rootDir = new DirectoryInfo(rootPath);
            var csProjFiles = rootDir.EnumerateFiles("*.csproj", updateCommandSettings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            var ignoreDirs = new[] { ".git", ".github", ".vs", ".vscode", "bin", "obj", "packages", "node_modules" };
            foreach (var csProjFile in csProjFiles.Where(f => !ignoreDirs.Contains(f.DirectoryName)))
            {
                AnsiConsole.MarkupLine($"Analyzing [yellow]{csProjFile.FullName}[/]");
                var packages = new List<Package>();
                using (var fileStream = File.OpenRead(csProjFile.FullName))
                {
                    var document = XDocument.Load(fileStream);
                    var ns = document.Root.GetDefaultNamespace();
                    var project = document.Element(ns + "Project");
                    var itemGroups = project
                        .Elements(ns + "ItemGroup")
                        .ToList();
                    packages.AddRange(itemGroups.SelectMany(ig => ig.Elements(ns + "PackageReference")).Select(e => new Package
                    {
                        Id = e.Attribute("Include")?.Value,
                        Version = e.Attribute("Version")?.Value ?? e.Element(ns + "Version")?.Value,
                    }));
                }

                if (packages.Count() == 0) continue;

                var projectFileResults = new Dictionary<string, string>();

                foreach (var package in packages.Where(p => string.IsNullOrWhiteSpace(updateCommandSettings.Package) || p.Id == updateCommandSettings.Package))
                {
                    NuGetVersion nugetVersion = null;
                    if (VersionRange.TryParse(package.Version, out VersionRange versionRange))
                    {
                        nugetVersion = versionRange.MinVersion;
                    }
                    else if (NuGetVersion.TryParse(package.Version, out NuGetVersion parsedVersion))
                    {
                        nugetVersion = parsedVersion;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]Skipping {package.Id} because of unknown version[/]");
                        continue;
                    }

                    AnsiConsole.Markup(package.Id);

                    var showUpToDate = true;
                    foreach (var source in enabledSources)
                    {
                        var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
                        try
                        {
                            FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();
                            using var cacheContext = new SourceCacheContext();
                            var allVersions = await resource.GetAllVersionsAsync(package.Id, cacheContext, NullLogger.Instance, CancellationToken.None);
                            var newerVersions = allVersions.Where(v => v > nugetVersion);
                            if (newerVersions.Count() == 0)
                            {
                                continue;
                            }

                            var stableVersions = newerVersions.Where(v => !v.IsPrerelease);
                            var prereleaseVersions = newerVersions.Where(v => v.IsPrerelease);

                            var versionsToShow = new List<NuGetVersion>();
                            versionsToShow.AddRange(HighestMajor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestMajor(prereleaseVersions, nugetVersion));
                            versionsToShow.AddRange(HighestMinor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestMinor(prereleaseVersions, nugetVersion));
                            versionsToShow.AddRange(HighestPatch(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestPatch(prereleaseVersions, nugetVersion));
                            versionsToShow.AddRange(HighestRevision(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestRevision(prereleaseVersions, nugetVersion));

                            if (versionsToShow.Count == 0)
                            {
                                continue;
                            }

                            var choices = new List<string>();
                            var currentVersionString = $"{package.Version} (current)";
                            choices.Add(currentVersionString);
                            choices.AddRange(versionsToShow.OrderBy(v => v).Select(v => v.ToString()));

                            showUpToDate = false;
                            AnsiConsole.MarkupLine(NeedsUpdate);
                            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().PageSize(10).AddChoices(choices.ToArray()));

                            if (choice == currentVersionString) continue;

                            var dotnet = new ProcessStartInfo("dotnet", $"add package {package.Id} -v {choice} -s {source.SourceUri}")
                            {
                                WorkingDirectory = csProjFile.Directory.FullName,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            var process = Process.Start(dotnet);
                            var outputAndError = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

                            process.WaitForExit();
                            var exitCode = process.ExitCode;

                            if (exitCode < 0)
                            {
                                Console.WriteLine(outputAndError[1]);
                                return -1;
                            }

                            if (!string.IsNullOrWhiteSpace(outputAndError[0]))
                            {
                                var lines = outputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                {
                                    AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                }
                            }
                        }
                        catch { }
                    }

                    if (showUpToDate) AnsiConsole.MarkupLine(UpToDate);
                }
            }

            return 0;
        }

        private static IEnumerable<NuGetVersion> HighestRevision(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build == nugetVersion.Patch && v.Version.Revision > nugetVersion.Revision).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestPatch(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build > nugetVersion.Patch).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestMinor(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor > nugetVersion.Minor).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestMajor(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            return versions.Where(v => v.Version.Major > nugetVersion.Major).GroupBy(v => v.Version.Major).Select(g => g.OrderByDescending(v => v).First());
        }

        private class Package
        {
            public string Id { get; set; }
            public string Version { get; set; }
        }

        public class UpdateCommandSettings : CommandSettings
        {
            [Description("A root directory to search. If not specified the current directory is used.")]
            [CommandOption("-d|--directory")]
            public string Directory { get; set; }

            [Description("A NuGet package to update. If not specified all packages are checked for updates.")]
            [CommandOption("-p|--package")]
            public string Package { get; set; }

            [Description("Include subdirectories when looking for csproj files (default: true)")]
            [CommandOption("-r|--recursive")]
            [DefaultValue(true)]
            public bool Recursive { get; set; }
        }
    }
}
