using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cetus.Hosting;

/// <summary>
/// Tightens filesystem access on DSH's credentials file. The file itself
/// must stay plaintext (DSH reads and writes it), so the defense here is the
/// Windows ACL: broad entries like Users or Everyone are removed when the
/// filesystem allows it. Best effort by design — a refused ACL change is
/// reported, never fatal.
/// </summary>
public static class CredentialGuard
{
    public enum AccessState
    {
        AlreadyTight,
        Tightened,
        Failed,
    }

    public static AccessState EnsureUserOnlyAccess(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return AccessState.Failed;
            }

            FileSecurity security = fileInfo.GetAccessControl();
            bool removed = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier)))
            {
                if (!IsBroadPrincipal(rule.IdentityReference) || (rule.FileSystemRights & FileSystemRights.Read) == 0)
                {
                    continue;
                }

                // Inherited rules cannot be removed individually; denying them
                // for this file requires breaking inheritance, which is more
                // invasive than this guard should be. Only explicit grants go.
                if (rule.IsInherited)
                {
                    continue;
                }

                security.RemoveAccessRuleSpecific(rule);
                removed = true;
            }

            if (!removed)
            {
                return AccessState.AlreadyTight;
            }

            fileInfo.SetAccessControl(security);
            return AccessState.Tightened;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or System.SystemException)
        {
            _ = error;
            return AccessState.Failed;
        }
    }

    /// <summary>Well-known group principals that must not hold read access.</summary>
    public static bool IsBroadPrincipal(IdentityReference identity)
    {
        try
        {
            SecurityIdentifier sid = (SecurityIdentifier)identity;
            return sid.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null))
                || sid.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null))
                || sid.Equals(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }
}
