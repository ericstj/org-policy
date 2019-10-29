﻿using System;
using System.Collections.Generic;
using System.Linq;

using Terrajobst.GitHubCaching;

namespace GitHubPermissionPolicyChecker
{
    internal static class CachedOrgExtensions
    {
        public static CachedTeam GetMicrosoftTeam(this CachedOrg org)
        {
            return org.Teams.SingleOrDefault(t => string.Equals(t.Name, "microsoft", StringComparison.OrdinalIgnoreCase));
        }

        public static CachedTeam GetMicrosoftBotsTeam(this CachedOrg org)
        {
            return org.Teams.SingleOrDefault(t => string.Equals(t.Name, "microsoft-bots", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsOwnedByMicrosoft(this CachedRepo repo)
        {
            var microsoftTeam = repo.Org.GetMicrosoftTeam();
            return repo.Teams.Any(ta => ta.Team == microsoftTeam);
        }

        public static bool IsOwnedByMicrosoft(this CachedTeam team)
        {
            var microsoftTeam = team.Org.GetMicrosoftTeam();
            return team.AncestorsAndSelf().Any(t => t == microsoftTeam);
        }

        public static bool IsClaimingToBeWorkingForMicrosoft(this CachedUser user)
        {
            var companyContainsMicrosoft = user.Company != null &&
                                           user.Company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

            var emailContainsMicrosoft = user.Email != null &&
                                         user.Email.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

            return companyContainsMicrosoft ||
                   emailContainsMicrosoft;
        }

        public static bool IsMicrosoftUser(this PolicyAnalysisContext context, CachedUser user)
        {
            if (context.UserLinks.LinkByGitHubLogin.ContainsKey(user.Login))
                return true;

            var microsoftBotsTeam = user.Org.GetMicrosoftBotsTeam();
            return microsoftBotsTeam != null && microsoftBotsTeam.Members.Contains(user);
        }


        public static IEnumerable<CachedUser> GetAdministrators(this CachedRepo repo)
        {
            return repo.Users
                       .Where(ua => ua.Permission == CachedPermission.Admin &&
                                    !ua.Describe().IsOwner)
                       .Select(ua => ua.User);
        }
    }
}
