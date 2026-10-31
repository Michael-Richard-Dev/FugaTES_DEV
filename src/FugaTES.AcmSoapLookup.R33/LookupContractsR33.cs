namespace FugaTES.AcmSoapLookup.R33;
public sealed record LookupRequestR33(string Code,string Center,int ReferenceVersion);
public sealed record LookupProfileR33(string Environment,Uri? Endpoint,Uri? ApprovedOrigin,string? ApprovalReference,
    DateTimeOffset UntilUtc,bool ReadApproved=false,int TimeoutSeconds=10);
public sealed record LookupResultR33(string State,string Reason,string? ContractCode=null,
    string CenterReturned="UNVERIFIED",string VersionReturned="UNVERIFIED");
