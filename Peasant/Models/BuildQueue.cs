﻿using Akavache;
using GitHub.Helpers;
using Octokit;
using Punchclock;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Peasant.Models
{
    public class BuildQueueItem 
    {
        public long BuildId { get; set; }
        public string RepoUrl { get; set; }
        public string SHA1 { get; set; }
        public string BuildScriptUrl { get; set; }
        public string BuildOutput { get; set; }

        public string GetBuildDirectory()
        {
            var rootDir = Environment.GetEnvironmentVariable("PEASANT_BUILD_DIR") ?? Path.GetTempPath();
            var di = new DirectoryInfo(Path.Combine(rootDir, "Build_" + RepoUrl.ToSHA1()));
            if (!di.Exists) di.Create();

            return di.FullName;
        }
    }

    public class BuildQueue
    {
        readonly OperationQueue opQueue = new OperationQueue(2);
        readonly IBlobCache blobCache;
        readonly GitHubClient client;
        readonly Subject<BuildQueueItem> enqueueSubject = new Subject<BuildQueueItem>();
        readonly Subject<BuildQueueItem> finishedBuilds = new Subject<BuildQueueItem>();
        readonly Func<BuildQueueItem, IObserver<string>, Task<int>> processBuildFunc;

        long nextBuildId;

        public BuildQueue(IBlobCache cache, GitHubClient githubClient, Func<BuildQueueItem, IObserver<string>, Task<int>> processBuildFunc = null)
        {
            blobCache = cache;
            client = githubClient;
            this.processBuildFunc = processBuildFunc ?? ProcessSingleBuild;
        }

        public IObservable<BuildQueueItem> Enqueue(string repoUrl, string sha1, string buildScriptUrl)
        {
            var buildId = Interlocked.Increment(ref nextBuildId);

            enqueueSubject.OnNext(new BuildQueueItem() {
                BuildId = buildId,
                RepoUrl = repoUrl,
                SHA1 = sha1,
                BuildScriptUrl = buildScriptUrl,
            });

            return finishedBuilds.Where(x => x.BuildId == buildId).Take(1);
        }

        public IDisposable Start()
        {
            var buildOutput = new Subject<string>();
            var currentOutput = new StringBuilder();
            buildOutput.Subscribe(x => currentOutput.AppendLine(x));

            var enqueueWithSave = enqueueSubject
                .SelectMany(x => blobCache.InsertObject("build_" + x.BuildId, x).Select(_ => x));

            var ret = blobCache.GetAllObjects<BuildQueueItem>()
                .Do(x => nextBuildId = x.Max(y => y.BuildId) + 1)
                .SelectMany(x => x.ToObservable())
                .Concat(enqueueWithSave)
                .SelectMany(x => opQueue.Enqueue(10, () => processBuildFunc(x, buildOutput))
                    .ToObservable()
                    .Catch<int, Exception>(ex => { buildOutput.OnNext(ex.Message); return Observable.Return(-1); })
                    .Select(y => new { Build = x, Output = currentOutput.ToString(), }))
                .SelectMany(async x => {
                    await blobCache.Insert("buildoutput_" + x.Build.BuildId, Encoding.UTF8.GetBytes(x.Output));
                    await blobCache.Invalidate("build_" + x.Build.BuildId);

                    x.Build.BuildOutput = x.Output;
                    return x.Build;
                })
                .Multicast(finishedBuilds);

            return ret.Connect();
        }

        public async Task<int> ProcessSingleBuild(BuildQueueItem queueItem, IObserver<string> stdout = null)
        {
            var target = queueItem.GetBuildDirectory();

            var repo = default(LibGit2Sharp.Repository);
            try {
                repo = new LibGit2Sharp.Repository(target);
                var dontcare = repo.Info.IsHeadUnborn; // NB: We just want to test if the repo is valid
            } catch (Exception) {
                repo = null;
            }

            var creds = new LibGit2Sharp.Credentials() { Username = client.Credentials.Login, Password = client.Credentials.Password };
            await cloneOrResetRepo(queueItem, target, repo, creds);

            // XXX: This needs to be way more secure
            await validateBuildUrl(queueItem.BuildScriptUrl);
            var filename = queueItem.BuildScriptUrl.Substring(queueItem.BuildScriptUrl.LastIndexOf('/') + 1);
            var buildScriptPath = Path.Combine(target, filename);

            var wc = new WebClient();
            await wc.DownloadFileTaskAsync(queueItem.BuildScriptUrl.Replace("/blob/", "/raw/"), buildScriptPath);

            var process = new ObservableProcess(createStartInfoForScript(buildScriptPath));
            if (stdout != null) {
                process.Output.Subscribe(stdout);
            }

            var exitCode = await process;

            if (exitCode != 0) {
                var ex = new Exception("Build failed with code: " + exitCode.ToString());
                ex.Data["ExitCode"] = exitCode;
                throw ex;
            }

            return exitCode;
        }

        static async Task cloneOrResetRepo(BuildQueueItem queueItem, string target, LibGit2Sharp.Repository repo, LibGit2Sharp.Credentials creds)
        {
            if (repo == null) {
                await Task.Run(() => {
                    LibGit2Sharp.Repository.Clone(queueItem.RepoUrl, target, credentials: creds);
                    repo = new LibGit2Sharp.Repository(target);
                });
            } else {
                repo.Network.Fetch(repo.Network.Remotes["origin"], credentials: creds);
            }

            await Task.Run(() => {
                var sha = default(LibGit2Sharp.ObjectId);
                LibGit2Sharp.ObjectId.TryParse(queueItem.SHA1, out sha);
                var commit = repo.Commits.FirstOrDefault(x => x.Id == sha);

                if (commit == null) {
                    throw new Exception(String.Format("Commit {0} in Repo {1} doesn't exist", queueItem.RepoUrl, queueItem.SHA1));
                }

                repo.Reset(LibGit2Sharp.ResetOptions.Hard, commit);
                repo.RemoveUntrackedFiles();
            });
        }

        static ProcessStartInfo createStartInfoForScript(string buildScript)
        {
            var ret = new ProcessStartInfo() {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };

            switch (Path.GetExtension(buildScript)) {
            case "cmd":
                ret.FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
                ret.Arguments = "/C \"" + buildScript + "\"";
                break;
            case "ps1":
                ret.FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\PowerShell.exe");
                ret.Arguments = "-ExecutionPolicy Unrestricted -File \"" + buildScript + "\"";
                break;
            default:
                ret.FileName = buildScript;
                break;
            }

            return ret;
        }

        async Task<string> validateBuildUrl(string buildUrl)
        {
            var m = Regex.Match(buildUrl.ToLowerInvariant(), @"https://github.com/(\w+)/(\w+)");
            if (!m.Success) {
                goto fail;
            }

            var org = m.Captures[1].Value;
            var repo = m.Captures[2].Value;

            // Anything from your own repo is :cool:
            if (org == client.Credentials.Login) {
                return null;
            }

            var repoInfo = default(Repository);
            try {
                // XXX: This needs to be a more thorough check, this means any
                // public repo can be used.
                repoInfo = await client.Repository.Get(org, repo);
            } catch (Exception ex) {
                goto fail;
            }

            if (repoInfo != null) return null;

        fail:
            throw new Exception("Build URL must be hosted on a repo or organization you are a member of and that you have made at least one commit to.");
        }
    }
}