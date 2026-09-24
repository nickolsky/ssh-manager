using System.Security.AccessControl;
using System.Security.Principal;

namespace SshManager.Core.Storage;

public static class FileAcl
{
    /// <summary>
    /// Removes inherited permissions and grants access to the current user only —
    /// Windows OpenSSH refuses private key files that others can read.
    /// </summary>
    public static void RestrictToCurrentUser(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(me);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>Writes a file that is readable only by the current user from the start.</summary>
    public static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, "");
        RestrictToCurrentUser(path);
        File.WriteAllText(path, content);
    }
}
