using System.Security.AccessControl;
using System.Security.Principal;
using FugaTES.Infraestrutura.Mm;
namespace FugaTES.WinForms.Mm;
public sealed class MmSnapshotStore : MmFileSnapshotStore
{
    public MmSnapshotStore(string directory):base(directory)
    {
        if(OperatingSystem.IsWindows())
        {
            var current=WindowsIdentity.GetCurrent().User??throw new InvalidOperationException("MM_WINDOWS_IDENTITY_REQUIRED");
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(current);
            foreach(var id in new[]{current,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(id,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
    }
}
