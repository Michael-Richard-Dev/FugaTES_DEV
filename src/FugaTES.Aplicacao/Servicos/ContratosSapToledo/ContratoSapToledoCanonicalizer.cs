using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;
public sealed class ContratoSapToledoCanonicalizer
{
    public ContratoSapToledoCanonico Canonicalizar(ContratoSapToledoSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(source.NumeroContrato)) throw new ArgumentException("NumeroContrato is required.", nameof(source));
        var centros = source.CentroOpcional.Select(NormalizarCentro).Order(StringComparer.Ordinal).ToArray();
        if (centros.Distinct(StringComparer.Ordinal).Count() != centros.Length) throw new ArgumentException("CentroOpcional cannot contain duplicates.", nameof(source));
        var n = source with { SistemaBalanca=N(source.SistemaBalanca), NumeroContrato=N(source.NumeroContrato)!, TipoContrato=N(source.TipoContrato), DescricaoTipoContrato=N(source.DescricaoTipoContrato), DataEmissao=N(source.DataEmissao), DataEntregaInicio=N(source.DataEntregaInicio), DataEntregaFim=N(source.DataEntregaFim), DataLimiteEntrega=N(source.DataLimiteEntrega), StatusContrato=N(source.StatusContrato), IdParceiro=N(source.IdParceiro), Material=N(source.Material), TipoPesagem=N(source.TipoPesagem), TabelaQualidadeID=N(source.TabelaQualidadeID), TabelaQualidadeVersao=N(source.TabelaQualidadeVersao), TabelaQualidadeVersaoValidade=N(source.TabelaQualidadeVersaoValidade), Observacao=N(source.Observacao), Centro=N(source.Centro), OperacaoContrato=N(source.OperacaoContrato), CentroOpcional=centros, Item=N(source.Item), UnidCont=N(source.UnidCont) };
        var json = JsonSerializer.Serialize(n);
        var values = new string?[] { n.SistemaBalanca,n.NumeroContrato,n.TipoContrato,n.DescricaoTipoContrato,n.DataEmissao,n.DataEntregaInicio,n.DataEntregaFim,n.DiasToler?.ToString(System.Globalization.CultureInfo.InvariantCulture),n.DataLimiteEntrega,n.StatusContrato,n.QuantidadeContrato?.ToString("G29",System.Globalization.CultureInfo.InvariantCulture),n.QuantidadeSaldoDevolucao?.ToString("G29",System.Globalization.CultureInfo.InvariantCulture),n.IdParceiro,n.Material,n.TipoPesagem,n.TabelaQualidadeID,n.TabelaQualidadeVersao,n.TabelaQualidadeVersaoValidade,n.Observacao,n.ValorUnitario?.ToString("G29",System.Globalization.CultureInfo.InvariantCulture),n.Centro,n.OperacaoContrato };
        var framed = new StringBuilder(); foreach(var value in values) Append(framed,value); framed.Append(centros.Length).Append(':'); foreach(var centro in centros) Append(framed,centro);
        // Preserve every historical fingerprint when R57 fields are absent. Item is never H4 version.
        if (n.Item is not null || n.UnidCont is not null) { framed.Append("R57:"); Append(framed,n.Item); Append(framed,n.UnidCont); }
        return new(n, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed.ToString()))));
    }
    private static string? N(string? value) => value?.Normalize(NormalizationForm.FormC);
    private static string NormalizarCentro(string? value) => string.IsNullOrEmpty(value) ? throw new ArgumentException("CentroOpcional contains an invalid Centro.") : value.Normalize(NormalizationForm.FormC);
    private static void Append(StringBuilder target,string? value){if(value is null){target.Append("-1:");return;}target.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);}
}
