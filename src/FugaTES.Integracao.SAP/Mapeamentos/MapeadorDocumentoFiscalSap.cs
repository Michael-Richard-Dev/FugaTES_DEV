using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Dtos.BusinessPartner;

namespace FugaTES.Integracao.SAP.Mapeamentos;

internal static class MapeadorDocumentoFiscalSap
{
    public static ResultadoMapeamentoFiscal Mapear(IEnumerable<DocumentoFiscalSapDto> documentosSap)
    {
        var documentos = new List<DocumentoFiscalNormalizado>();

        foreach (var documentoSap in documentosSap)
        {
            var tipo = documentoSap.BPTaxType switch
            {
                "BR1" => TipoDocumentoFiscal.Cnpj,
                "BR2" => TipoDocumentoFiscal.Cpf,
                "BR3" => TipoDocumentoFiscal.InscricaoEstadual,
                _ => (TipoDocumentoFiscal?)null
            };

            if (tipo is null)
            {
                continue;
            }

            var curto = NormalizarAusencia(documentoSap.BPTaxNumber);
            var longo = NormalizarAusencia(documentoSap.BPTaxLongNumber);
            var valor = ResolverValor(curto, longo);

            if (valor.Conflito)
            {
                return ResultadoMapeamentoFiscal.Falha(StatusConsultaSap.FalhaContrato);
            }

            if (valor.Valor is not null)
            {
                documentos.Add(new DocumentoFiscalNormalizado(tipo.Value, valor.Valor));
            }
        }

        return ResultadoMapeamentoFiscal.Sucesso(documentos);
    }

    private static string? NormalizarAusencia(string? valor)
    {
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }

    private static ValorFiscalResolvido ResolverValor(string? curto, string? longo)
    {
        if (curto is null && longo is null)
        {
            return new ValorFiscalResolvido(null, false);
        }

        if (curto is null)
        {
            return new ValorFiscalResolvido(longo, false);
        }

        if (longo is null)
        {
            return new ValorFiscalResolvido(curto, false);
        }

        return string.Equals(curto, longo, StringComparison.Ordinal)
            ? new ValorFiscalResolvido(curto, false)
            : new ValorFiscalResolvido(null, true);
    }

    private sealed record ValorFiscalResolvido(string? Valor, bool Conflito);
}

internal sealed class ResultadoMapeamentoFiscal
{
    private ResultadoMapeamentoFiscal(StatusConsultaSap status, IReadOnlyCollection<DocumentoFiscalNormalizado> documentos)
    {
        Status = status;
        Documentos = documentos;
    }

    public StatusConsultaSap Status { get; }

    public IReadOnlyCollection<DocumentoFiscalNormalizado> Documentos { get; }

    public static ResultadoMapeamentoFiscal Sucesso(IReadOnlyCollection<DocumentoFiscalNormalizado> documentos)
    {
        return new ResultadoMapeamentoFiscal(StatusConsultaSap.Sucesso, documentos);
    }

    public static ResultadoMapeamentoFiscal Falha(StatusConsultaSap status)
    {
        return new ResultadoMapeamentoFiscal(status, Array.Empty<DocumentoFiscalNormalizado>());
    }
}
