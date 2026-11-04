namespace FugaTES.Aplicacao.Configuracao;

/// <summary>
/// Centros do fluxo ACM. Decisao do operador em 04/11/2026: o cadastro de contrato no Guardian
/// atende 3011 E 3008 - era essa a intencao do negocio desde o inicio.
///
/// Ate aqui o "3011" estava escrito em cada etapa do caminho do ACM, de forma independente: o
/// recorte de admissao, a resolucao de emissor e material, o mapeador do Guardian, o preflight da
/// devolutiva e a identidade do callback. Isso tinha dois efeitos ruins. O primeiro, visivel: um
/// contrato de 3008 chegava, virava integracao e ficava PENDENTE para sempre - o motor nao o
/// considerava trabalho seu e, por isso, nao registrava recusa alguma (foi o caso de 1207 e 1208,
/// em 04/11/2026). O segundo, pior: liberar um centro novo exigia achar e trocar vinte literais,
/// e esquecer um deles move o bloqueio para o meio do fluxo, em vez de impedir a entrada.
///
/// Existe uma lista so, aqui, por isso. E a mesma decisao que o SD ja tomou em
/// <c>OrdensVendaSdFiltro.CentrosPermitidos</c>.
///
/// E regra, nao conveniencia: centro fora da lista continua REPROVANDO o escopo, nunca sendo
/// ignorado em silencio. O campo adicional 1002 do Guardian leva o centro DO CONTRATO - 3011 para
/// contrato de 3011, 3008 para contrato de 3008 - e sempre levou: os mapeadores ja copiavam
/// <c>Snapshot.Centro</c>. O que faltava era deixar o contrato de 3008 chegar ate eles.
/// </summary>
public static class CentrosAcm
{
    /// <summary>Unicos centros aceitos no fluxo ACM.</summary>
    public static readonly string[] Permitidos = ["3011", "3008"];

    /// <summary>
    /// Nulo e vazio NAO passam: "nao sei de que centro e" nao pode valer como centro aprovado.
    /// </summary>
    public static bool EhPermitido(string? centro) =>
        centro is not null && Permitidos.Contains(centro, StringComparer.Ordinal);
}
