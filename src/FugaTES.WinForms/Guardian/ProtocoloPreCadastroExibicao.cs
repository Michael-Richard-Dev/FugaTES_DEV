#nullable enable
using System.Globalization;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

namespace FugaTES.WinForms.Guardian;

/// <summary>
/// Linha como a grade a exibe. Toda ausencia vira UNKNOWN visivel, nunca espaco em
/// branco: o operador precisa distinguir "nao ha" de "nao foi lido".
/// </summary>
public sealed record ProtocoloPreCadastroExibicao
{
    public string Integracao { get; init; } = "";
    public string Contrato { get; init; } = "";
    public string Item { get; init; } = "";
    public string Referencia { get; init; } = "";
    public string Protocolo { get; init; } = "";
    public string TentativaEm { get; init; } = "";
    public string StatusHttp { get; init; } = "";
    public string EstadoEmpresarial { get; init; } = "";
    public string Tentativas { get; init; } = "";
    public string UltimoErro { get; init; } = "";
    public string Correlacao { get; init; } = "";
    public string Origem { get; init; } = "";
    public string Reenvio { get; init; } = "";

    public ProtocoloPreCadastroLinha Origemlinha { get; init; } = new();

    public static ProtocoloPreCadastroExibicao De(ProtocoloPreCadastroLinha linha)
    {
        var avaliacao = ReenvioProtocolo.Avaliar(linha);
        string Texto(string? valor) => ProtocoloPreCadastro.Texto(valor);
        var estado = linha.EstadoEmpresarial is null && linha.EtapaFuncional is null
            ? ProtocoloPreCadastro.Desconhecido
            : $"{Texto(linha.EstadoEmpresarial)} / {Texto(linha.EtapaFuncional)}";
        return new ProtocoloPreCadastroExibicao
        {
            Integracao = linha.IntegracaoId.ToString(),
            Contrato = Texto(linha.Contrato),
            Item = Texto(linha.Item),
            Referencia = Texto(linha.Referencia),
            Protocolo = Texto(linha.Protocolo),
            TentativaEm = linha.TentativaEm?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                ?? ProtocoloPreCadastro.Desconhecido,
            // Nenhuma coluna guarda o status HTTP; exibir vazio sugeriria sucesso.
            StatusHttp = linha.StatusHttp?.ToString(CultureInfo.InvariantCulture) ?? ProtocoloPreCadastro.Desconhecido,
            EstadoEmpresarial = estado,
            Tentativas = linha.Tentativas.ToString(CultureInfo.InvariantCulture),
            UltimoErro = Texto(linha.UltimoErro),
            Correlacao = Texto(linha.StatusCorrelacao),
            Origem = linha.Origem,
            Reenvio = $"{avaliacao.Rotulo} — {avaliacao.Codigo}",
            Origemlinha = linha,
        };
    }
}
