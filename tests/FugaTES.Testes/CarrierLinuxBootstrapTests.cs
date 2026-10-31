using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
namespace FugaTES.Testes;
public sealed class CarrierLinuxBootstrapTests
{
    static CarrierLinuxProfile Valid()=>new("HML","192.168.17.40","fugates-carrier",false,10,
        new("127.0.0.1",5432,"fugates_dev","fugates_dev_app","fugates"),
        new("HML","https://guardian-hml.example.invalid/","SYNTHETIC_HML_APPROVAL"),
        new("HML","https://sap-hml.example.invalid/","SYNTHETIC_HML_APPROVAL"));
    [Fact] public void Local_HML_profile_uses_designated_role_without_SID_or_SSH()=>Valid().Validar("fugates-carrier");
    [Theory][InlineData("PRD","CARRIER_LINUX_ENVIRONMENT_INVALID")][InlineData("DEV","CARRIER_LINUX_ENVIRONMENT_INVALID")]
    public void Non_HML_is_rejected(string environment,string reason)=>Assert.Equal(reason,Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {Environment=environment}).Validar("fugates-carrier")).Code);
    [Fact] public void INC005_is_always_rejected()=>Assert.Equal("CARRIER_INC005_FORBIDDEN",Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {IntegrationWorkerEnabled=true}).Validar("fugates-carrier")).Code);
    [Theory][InlineData("other_role","fugates_dev","127.0.0.1")][InlineData("fugates_dev_app","prd","127.0.0.1")][InlineData("fugates_dev_app","fugates_dev","192.168.17.40")]
    public void Remote_or_wrong_database_identity_is_rejected(string role,string db,string host)=>Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {Database=Valid().Database with {Username=role,Database=db,Host=host}}).Validar("fugates-carrier"));
    [Fact] public void Root_or_other_service_account_is_rejected()=>Assert.Throws<CarrierServiceStartupException>(()=>Valid().Validar("root"));
    [Theory][InlineData("http://guardian-hml.example.invalid/")][InlineData("https://user:secret@example.invalid/")][InlineData("https://example.invalid/?token=secret")]
    public void Unsafe_Guardian_origin_is_rejected(string url)=>Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {Guardian=Valid().Guardian with {BaseUrl=url}}).Validar("fugates-carrier"));
    [Fact] public void Guardian_PRD_profile_cannot_be_selected()=>Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {Guardian=Valid().Guardian with {Environment="PRD"}}).Validar("fugates-carrier"));
    [Fact] public void Missing_approval_cannot_label_an_arbitrary_origin_as_HML()=>Assert.Throws<CarrierServiceStartupException>(()=>(Valid() with {Guardian=Valid().Guardian with {ApprovalReference=""}}).Validar("fugates-carrier"));
    [Fact] public void Connection_explicitly_binds_local_role_and_does_not_put_secret_in_profile()
    {var b=new NpgsqlConnectionStringBuilder(Valid().ConnectionString("SYNTHETIC_PASSWORD"));Assert.Equal("fugates_dev_app",b.Username);Assert.Equal("127.0.0.1",b.Host);Assert.Equal("fugates_dev",b.Database);Assert.False(b.IncludeErrorDetail);Assert.DoesNotContain("SYNTHETIC_PASSWORD",Valid().ToString());}
    [Theory][InlineData("../password")][InlineData("guardian-prd-password")][InlineData("/etc/passwd")]
    public void Credential_ids_are_fixed_HML_names_without_traversal(string id)=>Assert.Throws<CarrierServiceStartupException>(()=>CarrierSystemdCredentials.ValidateName(id));
    [Theory][InlineData(UnixFileMode.UserRead|UnixFileMode.GroupRead)][InlineData(UnixFileMode.UserRead|UnixFileMode.OtherRead)][InlineData(UnixFileMode.UserRead|UnixFileMode.UserExecute)]
    public void Credential_permissions_are_owner_read_only(UnixFileMode mode)=>Assert.Throws<CarrierServiceStartupException>(()=>CarrierSystemdCredentials.ValidateMode(mode));
    [Fact] public void Owner_read_only_credentials_are_accepted()=>CarrierSystemdCredentials.ValidateMode(UnixFileMode.UserRead);
    [Fact] public void Database_identity_and_DDL_capability_are_fail_closed()
    {CarrierLinuxDatabaseIdentity.Validate("fugates_dev","fugates_dev_app",true,false,false);Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxDatabaseIdentity.Validate("fugates_dev","other",true,false,false));Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxDatabaseIdentity.Validate("fugates_dev","fugates_dev_app",true,true,false));Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxDatabaseIdentity.Validate("fugates_dev","fugates_dev_app",true,false,true));}
    [Fact] public void Linux_preflight_without_worker_registers_no_poll_or_manual_pipe()
    {var services=new ServiceCollection();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Carrier:Environment","HML"},{"Carrier:LinuxProfile","/etc/fugates/carrier-hml.json"}}).Build();CarrierLinuxHosting.Configure(services,config,false);Assert.Contains(services,s=>s.ImplementationType==typeof(CarrierLinuxReadiness));Assert.DoesNotContain(services,s=>s.ImplementationType==typeof(CarrierSyncWorker)||s.ImplementationType==typeof(CarrierSyncManualCommandServer)||s.ImplementationType==typeof(HospedagemIntegracaoWorker));}
    [Fact] public void Linux_hosting_rejects_INC005_alias_before_secret_or_database_access()
    {var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"INC005_WORKER_ENABLED","true"}}).Build();Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxHosting.Configure(new ServiceCollection(),config,false));}
    [Fact] public void Secret_fields_cannot_enter_the_plain_profile()
    {var path=Path.GetTempFileName();try{File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(Valid()).Replace("\"Environment\"","\"Password\":\"FORBIDDEN\",\"Environment\""));Assert.Equal("CARRIER_LINUX_PROFILE_INVALID",Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxProfile.Load(path)).Code);}finally{File.Delete(path);}}
    [Fact] public void Profile_error_does_not_disclose_file_or_payload()
    {var path=Path.GetTempFileName();try{File.WriteAllText(path,"SECRET-LIKE-PAYLOAD");var error=Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxProfile.Load(path));Assert.Equal("CARRIER_LINUX_PROFILE_INVALID",error.Message);Assert.Null(error.InnerException);Assert.DoesNotContain(path,error.ToString());Assert.DoesNotContain("SECRET-LIKE-PAYLOAD",error.ToString());}finally{File.Delete(path);}}
    [Fact] public void Conflicting_worker_flags_are_rejected()
    {var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Carrier:Environment","HML"},{"CARRIER_WORKER_ENABLED","true"}}).Build();Assert.Equal("CARRIER_LINUX_FLAG_MISMATCH",Assert.Throws<CarrierServiceStartupException>(()=>CarrierLinuxHosting.Configure(new ServiceCollection(),config,false)).Code);}
    [Fact] public void Authenticated_proof_rejects_ownership_and_privileged_reachable_roles()
    {
        var directory=new DirectoryInfo(Environment.CurrentDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"FugaTES.slnx")))directory=directory.Parent;
        var sql=File.ReadAllText(Path.Combine(directory!.FullName,"src","FugaTES.HostIntegracao","CarrierLinuxRuntimeBootstrap.cs"));
        foreach(var guard in new[]{"pg_has_role(current_user,oid,'MEMBER')","c.relowner","p.proowner","t.typowner","n.nspowner","r.rolsuper"})Assert.Contains(guard,sql);
    }
}