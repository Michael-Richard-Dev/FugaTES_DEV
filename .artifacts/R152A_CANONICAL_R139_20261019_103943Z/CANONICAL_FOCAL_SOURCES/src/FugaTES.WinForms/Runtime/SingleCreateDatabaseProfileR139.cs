using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FugaTES.WinForms.Runtime;

// Explicit, non-secret identity contract. No default database, port, role or approval.
public sealed record SingleCreateDatabaseProfileR139(string Environment, bool Approved, string EvidenceReference,
    string Host, int Port, string Database, string Role, string Schema)
{
    public static SingleCreateDatabaseProfileR139 Load(IConfiguration config)
    {
        const string p="Carrier:SingleCreate:PostgreSql:";
        return new(config[p+"Environment"]??"",bool.TryParse(config[p+"Approved"],out var ok)&&ok,
            config[p+"EvidenceReference"]??"",config[p+"Host"]??"",int.TryParse(config[p+"Port"],out var port)?port:0,
            config[p+"Database"]??"",config[p+"Role"]??"",config[p+"Schema"]??"");
    }

    public NpgsqlConnectionStringBuilder Options(string validatedDesktopConnection)
    {
        if(Environment!="HML"||!Approved||string.IsNullOrWhiteSpace(EvidenceReference)||Host!="127.0.0.1"||
            Port is <1 or >65535||Port is 5432 or 5433||Schema!="fugates"||
            string.IsNullOrWhiteSpace(Database)||string.IsNullOrWhiteSpace(Role)||
            Database=="fugates_dev"||Role=="fugates_dev_app")
            throw new DesktopStartupException("SINGLE_CREATE_SEGREGATED_HML_PROFILE_NOT_PROVEN");
        var options=new NpgsqlConnectionStringBuilder(validatedDesktopConnection);
        // Never redirect a desktop profile to a new server/role or reuse another credential target.
        if(options.Host!=Host||options.Port!=Port||options.Database!=Database||options.Username!=Role)
            throw new DesktopStartupException("SINGLE_CREATE_DESKTOP_PROFILE_IDENTITY_MISMATCH");
        options.Enlist=false;
        options.Options="-c default_transaction_read_only=off -c search_path=fugates";
        options.ApplicationName="FugaTES.Desktop.SingleCreate.HML";
        return options;
    }
}
