using FugaTES.Aplicacao.Configuracao;
using FugaTES.WinForms.Transportadoras;

namespace FugaTES.WinForms.Runtime;

/// <summary>
/// Qual ambiente esta janela esta operando, para o titulo dize-lo.
///
/// A mesma publicacao vai para C:\FugaTES_DEV110 e C:\FugaTES_QAS120, e o que as distingue e o
/// FugaTES.carrier.json ao lado do executavel. As telas sao identicas e - confirmado pelo operador
/// em 05/11/2026 - as CREDENCIAIS dos dois ambientes sao as mesmas. Nada no que se ve na tela diz
/// em qual deles a pessoa esta, e nada na senha a impede de agir no outro.
///
/// A separacao por pasta ja impede o processo de alcancar o SAP errado. O que ela nao cobre e o
/// risco humano: operar na janela errada por nao saber qual e. O titulo cobre isso.
///
/// Ate 05/11/2026 o titulo trazia "DEV110" ESCRITO - um desktop de QAS mostraria DEV110 e diria a
/// coisa errada com toda a confianca.
/// </summary>
public static class AmbienteDesktop
{
    /// <summary>
    /// O que aparece quando o ambiente nao pode ser determinado.
    ///
    /// Em vez de supor DEV110, que e o que existia antes: um titulo que afirma o ambiente errado e
    /// pior do que um que admite nao saber, porque convida a confiar nele.
    /// </summary>
    public const string Desconhecido = "ambiente não identificado";

    private static string? nome;

    /// <summary>
    /// Nome do ambiente deste desktop, lido uma vez. Nunca lanca: um titulo de janela nao pode
    /// impedir o programa de abrir, e o perfil ja e conferido - com erro visivel - por quem
    /// realmente fala com o SAP.
    /// </summary>
    public static string Nome => nome ??= Descobrir();

    private static string Descobrir()
    {
        try
        {
            var config = CarrierDesktopProfile.Load(
                Path.Combine(AppContext.BaseDirectory, "FugaTES.carrier.json"));
            if (!Uri.TryCreate(config["Sap:BaseUrl"], UriKind.Absolute, out var url)) return Desconhecido;
            return AmbientesSap.Por(url, config["Sap:SapClient"])?.Nome ?? Desconhecido;
        }
        catch { return Desconhecido; }
    }

    /// <summary>Titulo de janela com o ambiente. Exemplo: "FugaTES — QAS120 — v1.2.3".</summary>
    public static string Titulo(string? sufixo = null)
        => "FugaTES — " + Nome + (string.IsNullOrWhiteSpace(sufixo) ? "" : " — " + sufixo);
}
