using FugaTES.Aplicacao.Contratos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Servicos.Mvp;

// Only the existing immutable H4 version/link is used for initial contract/protocol creation.
// This does not create a weighing snapshot or authorize processing a callback or SAP return.
public sealed class AcmR57H4CorrelationAdapter(IAcmGuardianVinculoStore store)
{
    public static void Validate(VersaoContratoSapToledo version, AcmR57NormalContract contract, AcmGuardianVinculo link)
    {
        if (link.IntegracaoIdOrigem != version.IntegracaoId || link.NumeroContratoSap != version.NumeroContrato ||
            link.VersaoH4 != version.Versao || link.NumeroReferenciaSolicitado != contract.NumeroReferencia ||
            link.CodigoContratoSolicitado != contract.Contrato.Codigo ||
            (link.CodigoContratoConfirmado is not null && link.CodigoContratoConfirmado != contract.Contrato.Codigo) ||
            link.ProtocoloIdGuardian is <= 0)
            throw new AcmR57BlockedException("R165U_H4_LINK_IDENTITY_MISMATCH");
    }

    public async Task<AcmGuardianVinculo> ReserveAsync(VersaoContratoSapToledo version, AcmR57NormalContract contract, CancellationToken ct)
    {
        var link = await store.ReservarSolicitacaoUnicaAsync(version, contract.NumeroReferencia, contract.Contrato.Codigo, ct);
        Validate(version, contract, link);
        return link;
    }

    public async Task<AcmGuardianVinculo> ReadAsync(VersaoContratoSapToledo version, AcmR57NormalContract contract, CancellationToken ct)
    {
        var link = await store.ObterPorIntegracaoAsync(version.IntegracaoId, ct)
            ?? throw new AcmR57BlockedException("R165U_H4_LINK_MISSING");
        Validate(version, contract, link);
        return link;
    }
}
