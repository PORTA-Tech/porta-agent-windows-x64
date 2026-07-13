using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Porta.Agent.Service;

/// <summary>
/// v1.2.112 (Part B2) — self-healing ACL for C:\ProgramData\PortaAgent. The user-context
/// processes (UserAgent, Control Center) must be able to create and atomically replace
/// the shared coarse-data files (focus-app labels/categories, session context, logs). A
/// machine whose folder ACL got tightened (e.g. an installer re-creating the folder with
/// restrictive rights) silently freezes those files — labels go stale, digests show
/// hashes with no names, and nothing reports the failure.
///
/// Runs at service startup as SYSTEM: grants BUILTIN\Users Modify on the PortaAgent
/// folder (inheriting to files and subfolders), then re-protects the security subfolder
/// (local API auth material) with an explicit read-only-for-Users ACL so the broad grant
/// never reaches it. Idempotent — checks for the ACEs before adding. Best-effort — any
/// failure logs one line and never blocks service start.
///
/// Note: the folder holds coarse, non-sensitive data BY DESIGN (labels are coarse tokens,
/// never titles/URLs); consent and security material live in the security subfolder,
/// which this method explicitly locks down harder than inheritance would.
/// </summary>
internal static class SharedDataAclHealer
{
    public static void EnsureSharedFolderWritable()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string folderPath = Path.Combine(root, "PortaAgent");
            if (!Directory.Exists(folderPath))
            {
                return; // first boot creates it elsewhere; nothing to heal yet
            }

            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            EnsureUsersModifyOnFolder(folderPath, usersSid);
            ProtectSecuritySubfolder(Path.Combine(folderPath, "security"), usersSid);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Shared-folder ACL heal skipped: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void EnsureUsersModifyOnFolder(string folderPath, SecurityIdentifier usersSid)
    {
        var directory = new DirectoryInfo(folderPath);
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);

        // Idempotence: look for an existing explicit Users allow-ACE that already covers
        // Modify with full inheritance. The default ProgramData inherited ACEs do NOT
        // (Users get RX on files + create-only on folders — in-place writes to files
        // created by another account, and atomic replaces of them, fail).
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference == usersSid
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit))
            {
                return; // already healed
            }
        }

        security.AddAccessRule(new FileSystemAccessRule(
            usersSid,
            FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
        Console.WriteLine("Shared-folder ACL healed: BUILTIN\\Users granted Modify on PortaAgent.");
    }

    // The security subfolder must NOT receive the broad Users-Modify grant. Break
    // inheritance (preserving current effective rules), then remove every explicit
    // Users write-capable ACE and pin Users to read-only. SYSTEM/Administrators keep
    // Full via the preserved rules. Agents only ever READ this folder.
    private static void ProtectSecuritySubfolder(string securityPath, SecurityIdentifier usersSid)
    {
        if (!Directory.Exists(securityPath))
        {
            return;
        }

        var directory = new DirectoryInfo(securityPath);
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);

        bool alreadyProtected = security.AreAccessRulesProtected;
        bool changed = false;
        if (!alreadyProtected)
        {
            // Copy inherited rules into explicit ones, then cut inheritance so the parent's
            // new Users-Modify ACE can never flow down here.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            changed = true;
        }

        // Drop any Users ACE granting more than read+execute, then pin read-only.
        var toRemove = new System.Collections.Generic.List<FileSystemAccessRule>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference == usersSid
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
            {
                toRemove.Add(rule);
            }
        }

        foreach (FileSystemAccessRule rule in toRemove)
        {
            security.RemoveAccessRule(rule);
            changed = true;
        }

        if (toRemove.Count > 0 || !alreadyProtected)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            changed = true;
        }

        if (changed)
        {
            directory.SetAccessControl(security);
            Console.WriteLine("Security subfolder ACL pinned: Users read-only, inheritance cut.");
        }
    }
}
