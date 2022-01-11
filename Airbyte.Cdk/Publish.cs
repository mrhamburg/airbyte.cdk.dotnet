using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CliWrap;
using Spectre.Console;

namespace Airbyte.Cdk
{
    public class Publish
    {
        private static string Check = "[bold yellow]CHECK[/] ";
        private static string Error = "[bold red]ERROR[/] ";
        private static string Progress = "[bold green]PROGRESS[/] ";

        public static async Task Process(PublishOptions options)
        {
            try
            {
                //Use git cli to get the last commit and check which files have changed, from those files get first path for connector name and use that to build and publish
                //Get the version from the readme and use that as the tag, also push to latest
                if(options.IsBuildOnly)
                    ToConsole(Progress, "---> Running as build only! <---");
                var files = await GetFilesChanged();
                var connectors = GetConnectorsFromChanges(files);
                if(connectors.Length == 0)
                    throw new Exception("Could not find any changed connector files");
                
                foreach (var item in connectors)
                {
                    string image = $"airbytedotnet/{item}";
                    ToConsole(Progress, $"Processing changes for connector: {item}");
                
                    if (string.IsNullOrWhiteSpace(item))
                        throw new Exception("Could not find connector name");
                    string connectorpath = Path.Join(MoveToUpperPath(Assembly.GetExecutingAssembly().Location, 5, true), "airbyte-integrations", "connectors", item);
                    var semver = GetSemver(connectorpath);

                    if (string.IsNullOrWhiteSpace(semver))
                        throw new Exception("Could not acquire semver from changelog");

                    await CheckDocker();
                    await CheckIfDockerIsRunning();
                    if (!await TryBuildAndPush(connectorpath, semver, image, !options.IsBuildOnly))
                        throw new Exception("Failed to build and publish connector image");   
                }
            }
            catch (Exception e)
            {
                ToConsole(Error, $"Could not finish execution due to error: {e.Message}");
                Environment.Exit(1);
            }
        }
        
        public static string MoveToUpperPath(string path, int level, bool removefilename = false)
        {
            for (int i = 0; i < level; i++)
                path = Path.Combine(Path.GetDirectoryName(path), removefilename ? "" : Path.GetFileName(path));

            return path;
        }

        private static async Task<string[]> GetFilesChanged()
        {
            ToConsole(Check, "Getting files changed...");
            var stdOutBuffer = new StringBuilder();
            var cmd = Cli.Wrap("git")
                .WithArguments(new []{"diff", "--name-only", "HEAD", "HEAD~1"}) | stdOutBuffer;
            await cmd.ExecuteAsync();
            var found = stdOutBuffer.ToString().Split("\n");
            ToConsole(Check, $"Getting files changed... {found.Length} files found");
            return found;
        }

        public static string GetSemver(string connectorpath)
        {
            var changelog = Path.Join(connectorpath, "CHANGELOG.md");
            if (!File.Exists(changelog))
            {
                ToConsole(Error, $"Could not find changelog, searched in path: {changelog}");
                return string.Empty;
            }

            var contents = File.ReadAllText(changelog)
                .Replace("\n", " ")
                .Replace(Environment.NewLine, " ")
                .Replace("#", " ")
                .Replace("\r", " ")
                .Replace("v", " ");
            List<Version> _versions = new List<Version>();
            foreach (var v in contents.Split(" "))
                if (Version.TryParse(v, out var ver))
                    _versions.Add(ver);

            if (_versions.Count == 0)
            {
                ToConsole(Error, "Could not find any semver versions");
                return string.Empty;
            }
            
            _versions.Sort();
            _versions.Reverse();
            var version = _versions.First().ToString();
            ToConsole(Check, $"Found connector version {version}");

            return version;
        }

        private static string[] GetConnectorsFromChanges(string[] filechanges) =>
            filechanges.Where(x => x.StartsWith("airbyte-integrations/connectors/"))
                .Select(x => x.Split("connectors/").Last().Split("/").First()).Distinct().ToArray();

        /// <summary>
        /// TODO: this should be a generic check, InitCli has the same implementation
        /// </summary>
        /// <exception cref="Exception"></exception>
        private static async Task CheckDocker()
        {
            ToConsole(Check, "Validating Docker...");
            var stdOutBuffer = new StringBuilder();
            var cmd = Cli.Wrap("docker").WithArguments("--version") | stdOutBuffer;
            await cmd.ExecuteAsync();
            if (Version.TryParse(stdOutBuffer.ToString().Replace("Docker version", "").Split(",")[0], out var v))
                ToConsole(Check, "Found docker version: ", v.ToString(), Emoji.Known.CheckMark);
            else
            {
                ToConsole(Check, "Validating Docker...", Emoji.Known.CrossMark);
                throw new Exception("Could not find docker, please download at: https://docs.docker.com/get-docker/");
            }
        }
        
        /// <summary>
        /// TODO: this should be a generic check, InitCli has the same implementation
        /// </summary>
        /// <exception cref="Exception"></exception>
        private static async Task CheckIfDockerIsRunning()
        {
            ToConsole(Check, "Validating Docker is running...");
            var stdOutBuffer = new StringBuilder();
            var stdOutError = new StringBuilder();
            var cmd = Cli.Wrap("docker")
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdOutError))
                .WithValidation(CommandResultValidation.None)
                .WithArguments("version") | stdOutBuffer;
            await cmd.ExecuteAsync();
            if(stdOutError.Length == 0)
                ToConsole(Check, "Docker is running...", Emoji.Known.CheckMark);
            else
            {
                ToConsole(Check, "Docker is running...", Emoji.Known.CrossMark);
                throw new Exception("Docker is currently not running, please start docker first!");
            }
        }

        private static async Task<bool> TryBuildAndPush(string folderpath, string semver, string connectorname,
            bool pushimages)
        {
            var targetversionedimage = $"{connectorname}:{semver}";
            var targetlatestimage = $"{connectorname}:latest";
            
            //Check if image already exists remotely
            if (await ImageAlreadyExists(targetversionedimage))
                throw new Exception($"Image {targetversionedimage} already exists remotely, please update the CHANGELOG.md with a new version before proceeding.");

            ToConsole(Check, $"Building Docker Image...");
            string[] commandargs = {
                "build", "--build-arg", $"BUILD_VERSION={semver}", "-t", targetversionedimage, "-t", targetlatestimage, "."
            };
            ToConsole(Check, $"Command args: {string.Join(" ", commandargs)}");
            var cmd = Cli.Wrap("docker")
                .WithWorkingDirectory(folderpath)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithArguments(commandargs) | Console.WriteLine;
            var result = await cmd.ExecuteAsync();
            if (result.ExitCode == 1)
                return false;
            ToConsole(Check, $"Building Docker Image...done");

            if (!pushimages) return true;
            ToConsole(Check, $"Publishing Docker Image...");
            cmd = Cli.Wrap("docker")
                .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.WriteLine))
                .WithArguments(new []{"push", "-a", connectorname}) | Console.WriteLine;
            result = await cmd.ExecuteAsync();
            if (result.ExitCode != 0)
                return false;
            ToConsole(Check, $"Publishing Docker Image...done");
            return true;
        }

        public static async Task<bool> ImageAlreadyExists(string imagename)
        {
            var stdOutBuffer = new StringBuilder();
            var cmd = Cli.Wrap("docker")
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithArguments(new []{"manifest", "inspect", imagename}) | stdOutBuffer;
            
            await cmd.ExecuteAsync();

            return !stdOutBuffer.ToString().Contains("no such manifest") && !stdOutBuffer.ToString().Contains("requested access to the resource is denied");
        }

        private static void ToConsole(params string[] lines) => AnsiConsole.MarkupLine(string.Concat(lines));
    }
}