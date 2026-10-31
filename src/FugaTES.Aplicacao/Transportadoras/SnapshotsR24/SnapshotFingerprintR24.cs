using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;

// Versioned binary PROPOSAL only. PostgreSQL precision/interoperability is not homologated.
public static class SnapshotFingerprintR24
{
    public const string CanonicalizationProposal="R24_BINARY_PROPOSAL_V1";
    public static bool Supported(VersoesSnapshotR24 v)=>v==VersoesSnapshotR24.PropostaAtual||v==VersoesSnapshotR24.AprovadaR25T;
    public static string Scope(EscopoSnapshotR24 scope)=>Digest(CanonicalScope(scope));
    public static byte[] CanonicalScope(EscopoSnapshotR24 scope)=>Encode(w=>
    {
        w.Text(CanonicalizationProposal);w.Text("SCOPE");w.Text(scope.SapSourceCode);w.Text(scope.SapClientCode);
        w.Text(scope.SapEntity);w.Text(scope.CrmRoleCode);w.Text(scope.QueryContractVersion);
        w.Text(scope.QueryFilterContract);w.Text(scope.QueryOrderContract);w.Text(scope.PagingContract);
    });
    public static string Row(VersoesSnapshotR24 versions,RegistroBpSnapshotR24 row)=>Digest(CanonicalRow(versions,row));
    public static byte[] CanonicalRow(VersoesSnapshotR24 versions,RegistroBpSnapshotR24 row)
    {
        RequireVersion(versions);
        return Encode(w=>
        {
            w.Text(CanonicalizationProposal);w.Text("ROW");w.Versions(versions);w.Text(row.BusinessPartner);
            w.Int((int)row.Crm010TemporalEvaluation);w.Instant(row.RoleValidFromObserved);w.Instant(row.RoleValidToObserved);
            w.Text(row.ApprovedFullNameNormalized);
        });
    }
    public static string Page(PaginaSnapshotR24 page)=>Digest(CanonicalPage(page));
    public static byte[] CanonicalPage(PaginaSnapshotR24 page)=>Encode(w=>
    {
        w.Text(CanonicalizationProposal);w.Text("PAGE");w.Int(page.PageIndex);w.Long(page.ObservedOffset);
        w.Int(page.RequestedTop);w.Int(page.DeclaredRowCount);w.Int(page.TerminalPage?1:0);
        // Page observation digest excludes subsequent enrichment fields/status.
        foreach(var row in page.Rows.OrderBy(r=>r.BusinessPartner,StringComparer.Ordinal))
        {w.Text(row.BusinessPartner);w.Instant(row.RoleValidFromObserved);w.Instant(row.RoleValidToObserved);}
    });
    public static string Snapshot(ContextoSnapshotR24 context,DateTimeOffset reference,int pages,IEnumerable<RegistroBpSnapshotR24> rows)=>Digest(CanonicalSnapshot(context,reference,pages,rows));
    public static byte[] CanonicalSnapshot(ContextoSnapshotR24 context,DateTimeOffset reference,int pages,IEnumerable<RegistroBpSnapshotR24> rows)
    {
        RequireVersion(context.Versoes);
        var ordered=rows.OrderBy(r=>r.BusinessPartner,StringComparer.Ordinal).ToArray();
        return Encode(w=>
        {
            w.Text(CanonicalizationProposal);w.Text("SNAPSHOT");w.Text(context.Escopo.ScopeFingerprint);
            w.Versions(context.Versoes);w.Instant(reference);w.Int(pages);w.Int(ordered.Length);
            foreach(var row in ordered){w.Text(row.BusinessPartner);w.Text(Row(context.Versoes,row));}
        });
    }
    static void RequireVersion(VersoesSnapshotR24 v)
    {if(!Supported(v))throw new NotSupportedException("CANONICALIZATION_VERSION_NOT_SUPPORTED");}
    static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static byte[] Encode(Action<Writer> body)
    {
        using var stream=new MemoryStream();body(new Writer(stream));
        return stream.ToArray();
    }
    sealed class Writer(Stream stream)
    {
        static readonly UTF8Encoding Utf8=new(false,true);
        public void Int(int value){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(b,value);stream.Write(b);}
        public void Long(long value){Span<byte>b=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(b,value);stream.Write(b);}
        public void Text(string? value)
        {
            if(value is null){stream.WriteByte(0);return;}
            stream.WriteByte(1);var bytes=Utf8.GetBytes(value);Int(bytes.Length);stream.Write(bytes);
        }
        public void Instant(DateTimeOffset? value)
        {stream.WriteByte(value.HasValue?(byte)1:(byte)0);if(value.HasValue)Long(value.Value.UtcTicks);}
        public void Versions(VersoesSnapshotR24 v){Int(v.Contrato);Text(v.Normalizacao);Text(v.Comparacao);}
    }
}
