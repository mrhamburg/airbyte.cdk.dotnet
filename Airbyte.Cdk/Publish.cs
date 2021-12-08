﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliWrap;
using Spectre.Console;

namespace Airbyte.Cdk
{
    public class Publish
    {
        private static string Check = "[bold gray]CHECK[/] ";
        private static string Error = "[bold red]ERROR[/] ";
        private static string Progress = "[bold green]PROGRESS[/] ";

        public static async Task Process(PublishOptions options)
        {
            try
            {
                //Use git cli to get the last commit and check which files have changed, from those files get first path for connector name and use that to build and publish
                //Get the version from the readme and use that as the tag, also push to latest
                var files = await GetFilesChanged();
                if (files.Length == 0)
                    throw new Exception("Could not find any changed files");
                var connectors = GetConnectorsFromChanges(files);
                foreach (var item in connectors)
                {
                    string image = $"airbytedotnet/{item}";
                    ToConsole(Progress, $"Processing changes for connector: {item}");
                
                    if (string.IsNullOrWhiteSpace(item))
                        throw new Exception("Could not find connector name");
                    string connectorpath = Path.Join("airbyte-integrations", "connectors", item);
                    var semver = GetSemver(connectorpath);

                    if (string.IsNullOrWhiteSpace(semver))
                        throw new Exception("Could not acquire semver from readme");

                    await CheckDocker();
                    if (!await TryBuildAndPush(connectorpath, semver, image, options.Push))
                        throw new Exception("Failed to build and publish connector image");   
                }
            }
            catch (Exception e)
            {
                ToConsole(Error, $"Could not finish execution due to error: {e.Message}");
            }
        }

        private static async Task<string[]> GetFilesChanged()
        {
            ToConsole(Check, "Getting files changed...");
            var stdOutBuffer = new StringBuilder();
            var cmd = Cli.Wrap("git")
                .WithArguments("--name-only")
                .WithArguments("HEAD")
                .WithArguments("HEAD~1") | stdOutBuffer;
            await cmd.ExecuteAsync();
            var found = stdOutBuffer.ToString().Split(Environment.NewLine);
            ToConsole(Check, $"Getting files changed... {found.Length} files found");
            return found;
        }

        private static string GetSemver(string connectorpath)
        {
            var readme = Path.Join(connectorpath, "CHANGELOG.md");
            if (!File.Exists(readme))
                return string.Empty;
            var contents = File.ReadAllText(readme);
            List<Version> _versions = new List<Version>();
            foreach (var line in contents.Split(" "))
                if (Version.TryParse(line, out var ver))
                    _versions.Add(ver);

            if (_versions.Count == 0)
                return string.Empty;

            return _versions.OrderByDescending(x => x).First().ToString();
        }

        private static string[] GetConnectorsFromChanges(string[] filechanges) =>
            filechanges.Where(x => x.StartsWith("airbyte-integrations/connectors/"))
                .Select(x => string.Concat(x.Split("connectors/")).Split("/").First()).Distinct().ToArray();

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

        private static async Task<bool> TryBuildAndPush(string folderpath, string semver, string connectorname,
            bool pushimages)
        {
            var targetversionedimage = $"{connectorname}:{semver}";
            var targetlatestimage = $"{connectorname}:latest";
            
            //Check if image already exists remotely
            if (pushimages && await ImageAlreadyExists(targetversionedimage))
                throw new Exception($"Image {targetversionedimage} already exists remotely.");

            ToConsole(Check, $"Building Docker Image...");
            var cmd = Cli.Wrap("docker")
                .WithWorkingDirectory(folderpath)
                .WithArguments("build")
                .WithArguments(new[] {"-t", targetversionedimage})
                .WithArguments(new[] {"-t", targetlatestimage})
                .WithArguments(".") | (s => ToConsole(Progress, s));
            var result = await cmd.ExecuteAsync();
            if (result.ExitCode != 0)
                return false;
            ToConsole(Check, $"Building Docker Image...done");

            if (!pushimages) return true;
            ToConsole(Check, $"Publishing Docker Image...");
            cmd = Cli.Wrap("docker")
                .WithArguments("push")
                .WithArguments("-a") | (s => ToConsole(Progress, s));
            result = await cmd.ExecuteAsync();
            if (result.ExitCode != 0)
                return false;
            ToConsole(Check, $"Publishing Docker Image...done");
            return true;
        }

        private static async Task<bool> ImageAlreadyExists(string imagename)
        {
            var stdOutBuffer = new StringBuilder();
            var cmd = Cli.Wrap("docker")
                .WithArguments("manifest")
                .WithArguments("inspect")
                .WithArguments(imagename) | stdOutBuffer;
            
            await cmd.ExecuteAsync();

            return !stdOutBuffer.ToString().Contains("no such manifest");
        }

        private static void ToConsole(params string[] lines) => AnsiConsole.MarkupLine(string.Concat(lines));
    }
}