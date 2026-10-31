using System.Globalization;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public sealed record AcmR104CorrelationBinding(Guid IntegracaoId,long VersaoH4,string NumeroContratoSap,
    string Fingerprint,string FonteSnapshot,RecebimentoSapCpiMvp Snapshot);

public interface IAcmR104CallbackCorrelation
{
    Task PrepararAsync(VersaoContratoSapToledo version,AcmR57Plano plan,AcmR104CorrelationBinding? binding,long? confirmedProtocol,CancellationToken ct);
    Task ConfirmarAsync(VersaoContratoSapToledo version,AcmR57Plano plan,AcmR104CorrelationBinding? binding,ProtocoloAcmR57 protocol,CancellationToken ct);
}

// Reuses the existing correlation table/repository. No second journal or in-memory production fallback.
// H4, callback correlation and journal commits are separate: any uncertain intermediate result
// leaves the R96 ARMED marker and requires reconciliation, never another REST POST.
public sealed class AcmR104CallbackCorrelation(IRepositorioCorrelacaoRecebimento store,TimeProvider clock) : IAcmR104CallbackCorrelation
{
    public async Task PrepararAsync(VersaoContratoSapToledo version,AcmR57Plano plan,AcmR104CorrelationBinding? binding,long? confirmedProtocol,CancellationToken ct)
    {
        var input=Validar(version,plan,binding);
        var current=await store.ObterPorDocumentoAsync(input.DocumentoLogisticoReferencia,ct);
        if(confirmedProtocol is not null)
        {
            var reader=await store.ObterPorProtocoloAsync(confirmedProtocol.Value.ToString(CultureInfo.InvariantCulture),ct);
            ExigirMesmoSnapshot(current,input);ExigirMesmoSnapshot(reader,input);
            if(reader!.ProtocoloGuardian!=current!.ProtocoloGuardian || current.ProtocoloGuardian!=confirmedProtocol.Value.ToString(CultureInfo.InvariantCulture))
                throw new AcmR57BlockedException("R104_CALLBACK_PROTOCOL_MISMATCH");
            return;
        }
        if(current is not null)
        {
            ExigirMesmoSnapshot(current,input,allowUnassociatedContract:true);
            if(current.ProtocoloGuardian is not null || current.TicketGuardian is not null || current.CallbackFingerprint is not null)
                throw new AcmR57BlockedException("R104_CALLBACK_EXISTING_PROTOCOL");
        }
        var registered=await store.RegistrarSnapshotAsync(input,ct);
        if(registered.Status==StatusRegistroSnapshotMvp.Conflito)throw new AcmR57BlockedException("R104_CALLBACK_SNAPSHOT_CONFLICT");
        ExigirMesmoSnapshot(registered.Correlacao,input,allowUnassociatedContract:true);
        var associated=await store.AssociarContratoAcmAsync(input.DocumentoLogisticoReferencia,version.NumeroContrato,ct);
        if(associated.Status==StatusAssociacaoContratoAcmMvp.Conflito)throw new AcmR57BlockedException("R104_CALLBACK_CONTRACT_CONFLICT");
        ExigirMesmoSnapshot(associated.Correlacao,input);
    }

    public async Task ConfirmarAsync(VersaoContratoSapToledo version,AcmR57Plano plan,AcmR104CorrelationBinding? binding,ProtocoloAcmR57 protocol,CancellationToken ct)
    {
        var input=Validar(version,plan,binding);
        if(!AcmR57Mapping.RespostaConfere(protocol,plan.Protocolo) || protocol.Id==3)
            throw new AcmR57BlockedException("R104_CALLBACK_RESPONSE_IDENTITY_INVALID");
        var current=await store.ObterPorDocumentoAsync(input.DocumentoLogisticoReferencia,ct);
        ExigirMesmoSnapshot(current,input);
        var id=protocol.Id.ToString(CultureInfo.InvariantCulture);
        if(current!.ProtocoloGuardian is not null && current.ProtocoloGuardian!=id)
            throw new AcmR57BlockedException("R104_CALLBACK_PROTOCOL_CONFLICT");
        // Local receipt instant, not an invented business date from the Guardian payload.
        var observed=current.ProtocoloCriadoEm ?? clock.GetUtcNow();
        var result=await store.AssociarProtocoloAsync(input.DocumentoLogisticoReferencia,id,ct,observed);
        if(result.Status==StatusAssociacaoProtocoloMvp.Conflito)throw new AcmR57BlockedException("R104_CALLBACK_PROTOCOL_CONFLICT");
        ExigirMesmoSnapshot(result.Correlacao,input);
        var reader=await store.ObterPorProtocoloAsync(id,ct);
        ExigirMesmoSnapshot(reader,input);
        if(reader!.ProtocoloGuardian!=id)throw new AcmR57BlockedException("R104_CALLBACK_READBACK_MISSING");
    }

    private static RecebimentoSapCpiMvp Validar(VersaoContratoSapToledo v,AcmR57Plano p,AcmR104CorrelationBinding? b)
    {
        if(b is null || b.IntegracaoId!=v.IntegracaoId || b.VersaoH4!=v.Versao || b.NumeroContratoSap!=v.NumeroContrato ||
            b.Fingerprint!=v.FingerprintSha256 || string.IsNullOrWhiteSpace(b.FonteSnapshot) || b.Snapshot is null)
            throw new AcmR57BlockedException("R104_CALLBACK_SOURCE_REQUIRED");
        var x=b.Snapshot;var s=v.Snapshot;
        // The existing PostgreSQL adapter reconstructs this field rather than persisting it.
        // This is a compatibility restriction, NOT a default or functional approval for 1106.
        if(x.CodigoFluxo!="FLX_RECEBIMENTO")
            throw new AcmR57BlockedException("R104_CALLBACK_FLOW_STORAGE_UNSUPPORTED");
        if(x.Processo!="RECEBIMENTO" || x.Contrato!=v.NumeroContrato || x.ContratoAcmSap!=v.NumeroContrato ||
            x.Centro!=s.Centro || x.Material!=s.Material || x.BusinessPartnerEmissor!=s.IdParceiro ||
            x.TipoContrato!=s.TipoContrato || x.TipoDocumento!=s.TipoContrato || x.OperacaoContrato!=s.OperacaoContrato ||
            x.DocumentoLogisticoReferencia!=p.Protocolo.Dados.NumeroReferencia || x.CodigoFluxo!=p.Protocolo.Dados.CodigoFluxo ||
            x.UrlParaRetorno!=p.Protocolo.Callback || string.IsNullOrWhiteSpace(x.BusinessPartnerTransportadora) || x.TipoOperacaoSap<=0)
            throw new AcmR57BlockedException("R104_CALLBACK_SOURCE_MISMATCH");
        return x;
    }
    private static void ExigirMesmoSnapshot(CorrelacaoRecebimentoMvp? current,RecebimentoSapCpiMvp input,bool allowUnassociatedContract=false)
    {
        if(current is null || current.DocumentoSap!=input.DocumentoLogisticoReferencia ||
            (current.Entrada with{ContratoAcmSap=input.ContratoAcmSap})!=input ||
            (!allowUnassociatedContract && current.Entrada.ContratoAcmSap!=input.ContratoAcmSap) ||
            (current.Entrada.ContratoAcmSap is not null && current.Entrada.ContratoAcmSap!=input.ContratoAcmSap))
            throw new AcmR57BlockedException("R104_CALLBACK_SNAPSHOT_CONFLICT");
    }
}
