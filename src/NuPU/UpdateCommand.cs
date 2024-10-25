using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace NuPU
{
    internal class UpdateCommand : AsyncCommand<UpdateCommand.UpdateCommandSettings>
    {
        private const string UpToDate = " [green]up to date[/]";
        private const string NeedsUpdate = " [red]needs update[/]";

        public override async Task<int> ExecuteAsync(CommandContext context, UpdateCommandSettings updateCommandSettings)
        {
            if (updateCommandSettings.Version)
            {
                var version = GetType().Assembly.GetName().Version;
                if (version != null) AnsiConsole.WriteLine(version.ToString());
                return 0;
            }

            DefaultCredentialServiceUtility.SetupDefaultCredentialService(new NullLogger(), !updateCommandSettings.Interactive);

            var rootPath = string.IsNullOrWhiteSpace(updateCommandSettings.Directory) || !Directory.Exists(updateCommandSettings.Directory)
                ? Directory.GetCurrentDirectory()
                : updateCommandSettings.Directory;

            var rootDir = new DirectoryInfo(rootPath);
            var ignoreDirs = ResolveIgnoreDirs(rootPath);

            var csProjFiles = rootDir.EnumerateFiles("*.csproj", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = updateCommandSettings.Recursive });
            foreach (var csProjFile in csProjFiles.Where(f => !Ignored(f, ignoreDirs)))
            {
                var settings = Settings.LoadDefaultSettings(csProjFile.Directory.FullName);
                var enabledSources = SettingsUtility.GetEnabledSources(settings).ToList();
                AnsiConsole.MarkupLine($"Analyzing [grey]{csProjFile.FullName}[/]");
                var packages = new List<Package>();
                using (var fileStream = File.OpenRead(csProjFile.FullName))
                {
                    try
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
                    catch (XmlException e)
                    {
                        AnsiConsole.MarkupLine($"[red]Error[/] {e.Message}");
                        continue;
                    }
                }

                if (packages.Count == 0) continue;

                var skip = false;

                foreach (var package in packages.Where(p => string.IsNullOrWhiteSpace(updateCommandSettings.Package) || string.Equals(p.Id, updateCommandSettings.Package, StringComparison.OrdinalIgnoreCase)))
                {
                    if (skip) break;
                    NuGetVersion nugetVersion = null;

                    var packageVersion = package.Version ?? GetPackageVersionFromProps(package.Id, csProjFile.Directory, rootDir);

                    if (VersionRange.TryParse(packageVersion, out VersionRange versionRange))
                    {
                        nugetVersion = versionRange.MinVersion;
                    }
                    else if (NuGetVersion.TryParse(packageVersion, out NuGetVersion parsedVersion))
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
                    var sourcesToDelete = new List<PackageSource>();
                    foreach (var source in enabledSources)
                    {
                        var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
                        try
                        {
                            FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();
                            using var cacheContext = new SourceCacheContext();
                            var allVersions = await resource.GetAllVersionsAsync(package.Id, cacheContext, NullLogger.Instance, CancellationToken.None);
                            var newerVersions = allVersions.Where(v => v > nugetVersion).ToList();
                            if (newerVersions.Count == 0)
                            {
                                continue;
                            }

                            var stableVersions = newerVersions.Where(v => !v.IsPrerelease);

                            var versionsToShow = new List<NuGetVersion>();
                            versionsToShow.AddRange(HighestMajor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestMinor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestPatch(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestRevision(stableVersions, nugetVersion));

                            if (updateCommandSettings.IncludePrerelease)
                            {
                                var prereleaseVersions = newerVersions.Where(v => v.IsPrerelease);
                                versionsToShow.AddRange(HighestMajor(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestMinor(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestPatch(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestRevision(prereleaseVersions, nugetVersion));
                            }

                            if (versionsToShow.Count == 0)
                            {
                                continue;
                            }

                            var choices = new List<string>();
                            var currentVersionString = $"{nugetVersion.OriginalVersion} (current)";
                            choices.Add(currentVersionString);
                            choices.AddRange(versionsToShow.OrderBy(v => v).Select(v => Colored(nugetVersion, v)));
                            var skipString = "[grey]Skip project[/]";
                            choices.Add(skipString);

                            showUpToDate = false;
                            AnsiConsole.MarkupLine(NeedsUpdate);
                            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().PageSize(10).AddChoices([.. choices]));

                            if (choice == currentVersionString) continue;
                            if (choice == skipString)
                            {
                                skip = true;
                                break;
                            }

                            var dotnet = new ProcessStartInfo("dotnet", $"add package {package.Id} -v {Uncolored(choice)} -s {source.SourceUri}")
                            {
                                WorkingDirectory = csProjFile.Directory.FullName,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            var process = Process.Start(dotnet);
                            var outputAndError = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

                            await process.WaitForExitAsync();
                            var exitCode = process.ExitCode;

                            if (exitCode != 0)
                            {
                                if (!string.IsNullOrWhiteSpace(outputAndError[0])) Console.WriteLine(outputAndError[0]);
                                if (!string.IsNullOrWhiteSpace(outputAndError[1])) AnsiConsole.MarkupLine($"[red]{outputAndError[1]}[/]");
                                return -1;
                            }

                            if (!string.IsNullOrWhiteSpace(outputAndError[0]))
                            {
                                var lines = outputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
#pragma warning disable S6608 // Prefer indexing instead of "Enumerable" methods on types implementing "IList"
                                if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                {
                                    AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                }
#pragma warning restore S6608 // Prefer indexing instead of "Enumerable" methods on types implementing "IList"
                            }
                        }
                        catch (FatalProtocolException ex) when (IsAuthenticationError(ex))
                        {
                            sourcesToDelete.Add(source);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.WriteException(ex);
                        }
                    }

                    if (showUpToDate) AnsiConsole.MarkupLine(UpToDate);

                    // If we had any unauthenticated sources we remove them to avoid requesting them for the next package.
                    foreach (var sourceToDelete in sourcesToDelete.Where(s => enabledSources.Contains(s)))
                    {
                        AnsiConsole.MarkupLine($"[yellow]Unauthenticated source '{sourceToDelete.Name}'. Skipping further requests on this source.[/]");
                        enabledSources.Remove(sourceToDelete);
                    }
                }
            }

            return 0;
        }

        public static string Uncolored(string input)
        {
            var pattern = @"\[(.*?)\](.*?)\[\/\]";
            return Regex.Replace(input, pattern, "$2", RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        private static string GetPackageVersionFromProps(string packageId, DirectoryInfo startDirectory, DirectoryInfo rootDirectory)
        {
            var currentDirectory = startDirectory;
            while (currentDirectory != null)
            {
                var packageVersions = LoadPackageVersionsFromPropsFile(currentDirectory.FullName);
                if (packageVersions.TryGetValue(packageId, out var version))
                {
                    return version;
                }

                // Stop traversal once we reach or exceed the root directory's parent
                if (currentDirectory.FullName == rootDirectory.FullName)
                {
                    break;
                }

                currentDirectory = currentDirectory.Parent;
            }
            return null;
        }

        private static Dictionary<string, string> LoadPackageVersionsFromPropsFile(string directory)
        {
            var packagesPropsPath = Path.Combine(directory, "Directory.Packages.props");
            var packageVersions = new Dictionary<string, string>();

            if (File.Exists(packagesPropsPath))
            {
                var doc = XDocument.Load(packagesPropsPath);
                foreach (var package in doc.Descendants("PackageReference"))
                {
                    var packageName = package.Attribute("Include")?.Value;
                    var packageVersion = package.Attribute("Version")?.Value;
                    if (!string.IsNullOrEmpty(packageName) && !string.IsNullOrEmpty(packageVersion))
                    {
                        packageVersions[packageName] = packageVersion;
                    }
                }
            }

            return packageVersions;
        }

        private static string Colored(NuGetVersion currentVersion, NuGetVersion newVersion)
        {
            if (currentVersion.Major != newVersion.Major) return $"[red]{newVersion}[/]";
            else if (currentVersion.Minor != newVersion.Minor || newVersion.IsPrerelease) return $"[yellow]{newVersion}[/]";
            else return $"[green]{newVersion}[/]";
        }

        private static bool IsAuthenticationError(FatalProtocolException ex)
        {
            var baseException = ex.GetBaseException() as HttpRequestException;
            return baseException?.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }

        private static bool Ignored(FileInfo fileInfo, List<string> ignoreDirs)
        {
            if (ignoreDirs.Count == 0) return false;

            var directory = fileInfo.Directory;
            while (directory != null)
            {
                if (ignoreDirs.Contains(directory.Name, StringComparer.OrdinalIgnoreCase)) return true;
                directory = directory.Parent;
            }

            return false;
        }

        private static List<string> ResolveIgnoreDirs(string rootPath)
        {
            var ignoreDirs = new List<string> { ".git", ".github", ".vs", ".vscode", "bin", "obj", "packages", "node_modules" };

            var nupuIgnore = Path.Combine(rootPath, ".nupuignore");
            if (File.Exists(nupuIgnore))
            {
                AnsiConsole.MarkupLine($"Ignore directories in [grey]{nupuIgnore}[/]");
                var lines = File.ReadAllLines(nupuIgnore);
                ignoreDirs = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            }

            return ignoreDirs;
        }

        private static List<NuGetVersion> HighestRevision(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build == nugetVersion.Patch && v.Version.Revision > nugetVersion.Revision).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static List<NuGetVersion> HighestPatch(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build > nugetVersion.Patch).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static List<NuGetVersion> HighestMinor(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
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

        private sealed class Package
        {
            public string Id { get; set; }
            public string Version { get; set; }
        }

        public class UpdateCommandSettings : CommandSettings
        {
            [Description("Show version information")]
            [CommandOption("-v||--version")]
            public bool Version { get; set; }

            [Description("A root directory to search (default: current directory)")]
            [CommandOption("-d|--directory")]
            public string Directory { get; set; }

            [Description("A NuGet package to update (default: all)")]
            [CommandOption("-p|--package")]
            public string Package { get; set; }

            [Description("Include subdirectories when looking for csproj files (default: true)")]
            [CommandOption("-r|--recursive")]
            [DefaultValue(true)]
            public bool Recursive { get; set; }

            [Description("Include prerelease versions in suggested updates (default: true)")]
            [CommandOption("-i|--includeprerelease")]
            [DefaultValue(true)]
            public bool IncludePrerelease { get; set; }

            [Description("Run the tool in NuGet interactive mode which will prompt you to log in and more (default: false)")]
            [CommandOption("--interactive")]
            [DefaultValue(false)]
            public bool Interactive { get; set; }
        }
    }
}
