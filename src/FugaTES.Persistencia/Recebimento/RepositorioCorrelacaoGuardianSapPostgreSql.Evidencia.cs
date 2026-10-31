using System.Data;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Npgsql;

namespace FugaTES.Persistencia.Recebimento;

public sealed partial class RepositorioCorrelacaoGuardianSapPostgreSql
{
    private const string SelectEvidenceForUpdateSql="""
        select id, guardian_ticket_evidence_json, guardian_ticket_evidence_sha256,
               guardian_ticket_evidence_received_at
        from fugates.correlacao_guardian_sap
        where protocolo_guardian=@protocol and ticket_guardian=@ticket
          and callback_fingerprint=@fingerprint and estado='TICKET_ASSOCIADO'
          and contrato_acm_sap=@contract and centro_sap=@center and material_sap=@material
          and bp_emissor=@partner and documento_logistico_referencia=@reference
          and peso_bruto=@gross and tara_final=@tare and peso_liquido=@net
        for update;
        """;
    private const string StoreEvidenceSql="""
        update fugates.correlacao_guardian_sap
        set guardian_ticket_evidence_json=@json, guardian_ticket_evidence_sha256=@hash,
            guardian_ticket_evidence_received_at=now()
        where id=@id and guardian_ticket_evidence_json is null
          and guardian_ticket_evidence_sha256 is null and guardian_ticket_evidence_received_at is null;
        """;

    public async Task PreservarAsync(CorrelacaoRecebimentoMvp c,PesagemSapMvp p,CancellationToken ct)
    {
        var snapshot=EvidenciaPesagemGuardianSnapshot.Criar(c,p);
        await using var connection=await dataSource.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try
        {
            await using var select=new NpgsqlCommand(SelectEvidenceForUpdateSql,connection,transaction);
            select.Parameters.AddWithValue("protocol",c.ProtocoloGuardian!);
            select.Parameters.AddWithValue("ticket",c.TicketGuardian!);
            select.Parameters.AddWithValue("fingerprint",c.CallbackFingerprint!);
            select.Parameters.AddWithValue("contract",p.ContratoACM);
            select.Parameters.AddWithValue("center",p.Centro);
            select.Parameters.AddWithValue("material",p.Material);
            select.Parameters.AddWithValue("partner",p.IdParceiro);
            select.Parameters.AddWithValue("reference",c.DocumentoSap);
            select.Parameters.AddWithValue("gross",p.PesoBruto);
            select.Parameters.AddWithValue("tare",p.PesoTara);
            select.Parameters.AddWithValue("net",p.PesoAntesDesconto);
            Guid id;bool exists;
            await using(var reader=await select.ExecuteReaderAsync(ct))
            {
                if(!await reader.ReadAsync(ct))throw new InvalidOperationException("R165A_EVIDENCE_PARENT_NOT_CONFIRMED");
                id=reader.GetGuid(0);
                exists=snapshot.ConfirmarExistente(reader.IsDBNull(1)?null:reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetString(2),!reader.IsDBNull(3));
            }
            if(!exists)
            {
                await using var save=new NpgsqlCommand(StoreEvidenceSql,connection,transaction);
                save.Parameters.AddWithValue("id",id);save.Parameters.AddWithValue("json",snapshot.Json);save.Parameters.AddWithValue("hash",snapshot.Sha256);
                if(await save.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("R165A_EVIDENCE_WRITE_UNCONFIRMED");
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None);throw; }
    }
}
