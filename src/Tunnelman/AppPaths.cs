using System.Security.AccessControl;
using System.Security.Principal;

namespace Tunnelman;

internal static class AppPaths
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Tunnelman");

    private static string _directory = DefaultDirectory;

    public static string Directory => _directory;

    public static void Use(string fullPath) => _directory = fullPath;

    public static string UiFile => Path.Combine(Directory, "ui.json");

    public static string TunnelsFile => Path.Combine(Directory, "tunnels.json");

    public static bool TryEnsureDirectory() =>
        TryProtectDirectory(Directory, UiFile, TunnelsFile);

    internal static bool TryProtectDirectory(string directory, params string[] existingFiles)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            if (!TryApplyDirectoryAcl(directory))
                return false;

            foreach (var file in existingFiles)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    continue;
                if (!TryApplyFileAcl(file))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    internal static bool TryProtectWrittenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            return TryApplyFileAcl(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    private static bool TryApplyDirectoryAcl(string path)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
            return false;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, user, FileSystemRights.Modify);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        var info = new DirectoryInfo(path);
        info.SetAccessControl(security);
        return IsProtectedDacl(info.GetAccessControl());
    }

    private static void AddDirectoryRule(DirectorySecurity security, IdentityReference id, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            id,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static bool TryApplyFileAcl(string path)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
            return false;

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        var info = new FileInfo(path);
        info.SetAccessControl(security);
        return IsProtectedDacl(info.GetAccessControl());
    }

    private static bool IsProtectedDacl(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected)
            return false;

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(everyone) || sid.Equals(users) || sid.Equals(authenticated))
                return false;
        }

        return true;
    }
}
