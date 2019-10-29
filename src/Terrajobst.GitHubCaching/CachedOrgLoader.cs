﻿using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Octokit;

namespace Terrajobst.GitHubCaching
{
    public sealed class CachedOrgLoader
    {
        public CachedOrgLoader(GitHubClient gitHubClient, TextWriter logWriter, bool forceUpdate)
        {
            GitHubClient = gitHubClient;
            LogWriter = logWriter;
            ForceUpdate = forceUpdate;
        }

        public GitHubClient GitHubClient { get; }
        public TextWriter LogWriter { get; }
        public bool ForceUpdate { get; }

        private string GetCachedPath(string orgName)
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cachedDirectory = Path.Combine(localData, "GitHubPermissionSurveyor", "Cache");
            return Path.Combine(cachedDirectory, $"{orgName}.json");
        }

        public async Task<CachedOrg> LoadAsync(string orgName)
        {
            var cachedOrg = ForceUpdate
                                ? null
                                : await LoadFromCacheAsync(orgName);

            if (cachedOrg == null || cachedOrg.Version != CachedOrg.CurrentVersion)
            {
                cachedOrg = await LoadFromGitHubAsync(orgName);
                await SaveToCacheAsync(cachedOrg);
            }

            return cachedOrg;
        }

        private async Task<CachedOrg> LoadFromCacheAsync(string orgName)
        {
            var path = GetCachedPath(orgName);
            if (!File.Exists(path))
                return null;

            using (var stream = File.OpenRead(path))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                options.Converters.Add(new JsonStringEnumConverter());
                var orgData = await JsonSerializer.DeserializeAsync<CachedOrg>(stream, options);
                orgData.Initialize();

                if (orgData.Name != orgName)
                    return null;

                return orgData;
            }
        }

        private async Task SaveToCacheAsync(CachedOrg cachedOrg)
        {
            var path = GetCachedPath(cachedOrg.Name);
            var cacheDirectory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(cacheDirectory);

            using (var stream = File.Create(path))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                options.Converters.Add(new JsonStringEnumConverter());
                await JsonSerializer.SerializeAsync(stream, cachedOrg, options);
            }
        }

        private async Task<CachedOrg> LoadFromGitHubAsync(string orgName)
        {
            var start = DateTimeOffset.Now;

            LogWriter.WriteLine($"Start: {start}");
            LogWriter.WriteLine("Loading org data from GitHub...");

            var cachedOrg = new CachedOrg
            {
                Version = CachedOrg.CurrentVersion,
                Name = orgName
            };

            await LoadMembersAsync(cachedOrg);
            await LoadTeamsAsync(cachedOrg);
            await LoadReposAndCollaboratorsAsync(cachedOrg);
            await LoadExternalUsersAsync(cachedOrg);
            await LoadUsersDetailsAsync(cachedOrg);

            var finish = DateTimeOffset.Now;
            var duration = finish - start;
            LogWriter.WriteLine($"Finished: {finish}. Took {duration}.");

            cachedOrg.Initialize();

            return cachedOrg;
        }

        private async Task LoadMembersAsync(CachedOrg cachedOrg)
        {
            await PrintProgressAsync("Loading owner list");
            var owners = await GitHubClient.Organization.Member.GetAll(cachedOrg.Name, OrganizationMembersFilter.All, OrganizationMembersRole.Admin, ApiOptions.None);

            await PrintProgressAsync("Loading non-owner list");
            var nonOwners = await GitHubClient.Organization.Member.GetAll(cachedOrg.Name, OrganizationMembersFilter.All, OrganizationMembersRole.Member, ApiOptions.None);

            foreach (var owner in owners)
            {
                var member = new CachedUser
                {
                    Login = owner.Login,
                    IsMember = true,
                    IsOwner = true
                };
                cachedOrg.Users.Add(member);
            }

            foreach (var nonOwner in nonOwners)
            {
                var member = new CachedUser
                {
                    Login = nonOwner.Login,
                    IsMember = true,
                    IsOwner = false
                };
                cachedOrg.Users.Add(member);
            }
        }

        private async Task LoadTeamsAsync(CachedOrg cachedOrg)
        {
            await PrintProgressAsync("Loading team list");
            var teams = await GitHubClient.Organization.Team.GetAll(cachedOrg.Name);
            var i = 0;

            foreach (var team in teams)
            {
                await PrintProgressAsync("Loading team", team.Name, i++, teams.Count);

                var cachedTeam = new CachedTeam
                {
                    Id = team.Id.ToString(),
                    ParentId = team.Parent?.Id.ToString(),
                    Name = team.Name
                };
                cachedOrg.Teams.Add(cachedTeam);

                var maintainerRequest = new TeamMembersRequest(TeamRoleFilter.Maintainer);
                var maintainers = await GitHubClient.Organization.Team.GetAllMembers(team.Id, maintainerRequest);

                foreach (var maintainer in maintainers)
                    cachedTeam.MaintainerLogins.Add(maintainer.Login);

                await WaitForEnoughQuotaAsync();

                var memberRequest = new TeamMembersRequest(TeamRoleFilter.All);
                var members = await GitHubClient.Organization.Team.GetAllMembers(team.Id, memberRequest);

                foreach (var member in members)
                    cachedTeam.MemberLogins.Add(member.Login);

                await WaitForEnoughQuotaAsync();

                foreach (var repo in await GitHubClient.Organization.Team.GetAllRepositories(team.Id))
                {
                    var permissionLevel = repo.Permissions.Admin
                                            ? CachedPermission.Admin
                                            : repo.Permissions.Push
                                                ? CachedPermission.Push
                                                : CachedPermission.Pull;

                    var cachedRepoAccess = new CachedTeamAccess
                    {
                        RepoName = repo.Name,
                        Permission = permissionLevel
                    };
                    cachedTeam.Repos.Add(cachedRepoAccess);
                }
            }
        }

        private async Task LoadReposAndCollaboratorsAsync(CachedOrg cachedOrg)
        {
            await PrintProgressAsync("Loading repo list");
            var repos = await GitHubClient.Repository.GetAllForOrg(cachedOrg.Name);
            var i = 0;

            foreach (var repo in repos)
            {
                await PrintProgressAsync("Loading repo", repo.FullName, i++, repos.Count);

                var cachedRepo = new CachedRepo
                {
                    Name = repo.Name,
                    IsPrivate = repo.Private,
                    LastPush = repo.PushedAt ?? repo.CreatedAt
                };
                cachedOrg.Repos.Add(cachedRepo);

                foreach (var user in await GitHubClient.Repository.Collaborator.GetAll(repo.Owner.Login, repo.Name))
                {
                    var permission = user.Permissions.Admin
                                        ? CachedPermission.Admin
                                        : user.Permissions.Push
                                            ? CachedPermission.Push
                                            : CachedPermission.Pull;

                    var cachedCollaborator = new CachedUserAccess
                    {
                        RepoName = cachedRepo.Name,
                        UserLogin = user.Login,
                        Permission = permission
                    };
                    cachedOrg.Collaborators.Add(cachedCollaborator);
                }
            }
        }

        private async Task LoadExternalUsersAsync(CachedOrg cachedOrg)
        {
            await PrintProgressAsync("Loading outside collaborators");
            var outsideCollaborators = await GitHubClient.Organization.OutsideCollaborator.GetAll(cachedOrg.Name, OrganizationMembersFilter.All, ApiOptions.None);

            foreach (var user in outsideCollaborators)
            {
                var cachedUser = new CachedUser
                {
                    Login = user.Login,
                    IsOwner = false,
                    IsMember = false
                };
                cachedOrg.Users.Add(cachedUser);
            }
        }

        private async Task LoadUsersDetailsAsync(CachedOrg cachedOrg)
        {
            var i = 0;

            foreach (var cachedUser in cachedOrg.Users)
            {
                await PrintProgressAsync("Loading user details", cachedUser.Login, i++, cachedOrg.Users.Count);

                var user = await GitHubClient.User.Get(cachedUser.Login);
                cachedUser.Name = user.Name;
                cachedUser.Company = user.Company;
                cachedUser.Email = user.Email;
            }
        }

        private async Task PrintProgressAsync(string task, string itemName, int itemIndex, int itemCount)
        {
            var percentage = (itemIndex + 1) / (float)itemCount;
            var text = $"{task}: {itemName} {percentage:P1}";
            await PrintProgressAsync(text);
        }

        private async Task PrintProgressAsync(string text)
        {
            await WaitForEnoughQuotaAsync();

            var rateLimit = GitHubClient.GetLastApiInfo()?.RateLimit;
            var rateLimitText = rateLimit == null
                                    ? null
                                    : $" (Remaining API quota: {rateLimit.Remaining})";
            LogWriter.WriteLine($"{text}...{rateLimitText}");
        }

        private Task WaitForEnoughQuotaAsync()
        {
            var rateLimit = GitHubClient.GetLastApiInfo()?.RateLimit;

            if (rateLimit != null && rateLimit.Remaining <= 50)
            {
                var padding = TimeSpan.FromMinutes(2);
                var waitTime = (rateLimit.Reset - DateTimeOffset.Now).Add(padding);
                LogWriter.WriteLine($"API rate limit exceeded. Waiting {waitTime.TotalMinutes:N0} minutes until it resets ({rateLimit.Reset.ToLocalTime():M/d/yyyy h:mm tt}).");
                return Task.Delay(waitTime);
            }

            return Task.CompletedTask;
        }
    }
}
