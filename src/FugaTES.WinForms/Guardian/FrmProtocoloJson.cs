#nullable enable
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;

namespace FugaTES.WinForms.Guardian;

/// <summary>
/// JSON de uma tentativa de pre-cadastro REST, em tres partes separadas: o que foi
/// enviado, o que o Guardian respondeu e as informacoes tecnicas da execucao.
///
/// Apresentacao apenas: nao consulta Guardian, nao escreve em banco, nao dispara
/// integracao. A origem de cada parte e declarada; uma reconstrucao nunca e anunciada
/// como o corpo originalmente trafegado, e o que nao esta armazenado aparece como
/// ORIGINAL_NAO_ARMAZENADO.
/// </summary>
public sealed class FrmProtocoloJson : Form
{
    private readonly TabControl abas;
    private readonly TextBox enviado;
    private readonly TextBox resposta;
    private readonly TextBox tecnico;
    private readonly Label rodape;

    public FrmProtocoloJson(ProtocoloPreCadastroLinha linha)
    {
        ArgumentNullException.ThrowIfNull(linha);
        Text = $"FugaTES - Pre-cadastro {ProtocoloPreCadastro.Texto(linha.Contrato)} - JSON";
        ClientSize = new Size(1020, 640);
        MinimumSize = new Size(800, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var cabecalho = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Text = $"Contrato {ProtocoloPreCadastro.Texto(linha.Contrato)} | Item {ProtocoloPreCadastro.Texto(linha.Item)} | "
                 + $"Referencia OC {ProtocoloPreCadastro.Texto(linha.Referencia)}\r\n"
                 + $"Integracao {linha.IntegracaoId} | Protocolo {ProtocoloPreCadastro.Texto(linha.Protocolo)} | "
                 + $"Tentativas {linha.Tentativas}\r\n"
                 + $"Etapa {ProtocoloPreCadastro.Etapa} ({ProtocoloPreCadastro.Rota}) | Origem {linha.Origem}",
        };

        var barra = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var copiar = new Button { Text = "Copiar JSON exibido", Width = 170, Height = 28 };
        copiar.Click += (_, _) => Copiar();
        barra.Controls.Add(copiar);
        var fechar = new Button { Text = "Fechar", Width = 90, Height = 28 };
        fechar.Click += (_, _) => Close();
        barra.Controls.Add(fechar);

        static TextBox Monospaco(string nome) => new()
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), AccessibleName = nome,
        };
        enviado = Monospaco("JSON enviado ao Guardian REST");
        resposta = Monospaco("JSON da resposta Guardian REST");
        tecnico = Monospaco("Informacoes tecnicas de execucao");

        abas = new TabControl { Dock = DockStyle.Fill };
        void Aba(string titulo, Control conteudo)
        {
            var aba = new TabPage(titulo);
            aba.Controls.Add(conteudo);
            abas.TabPages.Add(aba);
        }
        Aba("A. JSON enviado", enviado);
        Aba("B. JSON da resposta", resposta);
        Aba("C. Execucao", tecnico);

        rodape = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };

        layout.Controls.Add(cabecalho, 0, 0);
        layout.Controls.Add(barra, 0, 1);
        layout.Controls.Add(abas, 0, 2);
        layout.Controls.Add(rodape, 0, 3);
        Controls.Add(layout);

        Preencher(linha);
    }

    private void Preencher(ProtocoloPreCadastroLinha linha)
    {
        enviado.Text = Corpo(linha.PayloadEnviadoJson,
            "JSON enviado ao Guardian REST",
            "O corpo enviado nao esta armazenado nesta origem. A reconstrucao a partir do contrato H4 "
            + "pertence ao mecanismo de comandos autorizado e nao e feita aqui, de modo que nada e exibido "
            + "no lugar do original.");
        resposta.Text = Corpo(linha.RespostaJson,
            "JSON da resposta Guardian REST",
            "Nenhuma coluna da origem persiste o corpo HTTP da resposta Guardian. Respostas externas nao "
            + "sao reconstruidas: o que o servico respondeu so pode ser afirmado se tiver sido gravado.");
        tecnico.Text = Tecnico(linha);

        var partes = new List<string>();
        partes.Add("A: " + (linha.PayloadEnviadoJson is null ? ProtocoloPreCadastro.NaoArmazenado : ProtocoloPreCadastro.Projecao));
        partes.Add("B: " + (linha.RespostaJson is null ? ProtocoloPreCadastro.NaoArmazenado : ProtocoloPreCadastro.Projecao));
        partes.Add("C: evidencia duravel do journal e das tentativas");
        rodape.Text = string.Join("   |   ", partes)
            + "\r\nTokens, autenticacao e cabecalhos sensiveis nao sao exibidos nem armazenados por esta tela.";
    }

    private static string Corpo(string? json, string titulo, string ausente)
    {
        if (string.IsNullOrWhiteSpace(json))
            return $"{ProtocoloPreCadastro.NaoArmazenado}\r\n\r\n{titulo}: {ausente}";
        var texto = Sanitizar(json);
        try
        {
            using var documento = JsonDocument.Parse(texto);
            return JsonSerializer.Serialize(documento.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex)
        {
            // Melhor mostrar o bruto e o erro do que esconder um conteudo ilegivel.
            return $"CONTEUDO ARMAZENADO NAO E JSON VALIDO: {ex.Message}\r\n\r\n{texto}";
        }
    }

    /// <summary>Remove valores de autenticacao antes de qualquer exibicao ou copia.</summary>
    public static string Sanitizar(string texto)
    {
        const string marca = "\"***REMOVIDO***\"";
        return System.Text.RegularExpressions.Regex.Replace(texto,
            "(?i)\"(authorization|bearer|token|access_token|refresh_token|password|senha|secret|apikey|api_key|cookie|set-cookie|assinatura)\"\\s*:\\s*(\"(\\\\.|[^\"\\\\])*\"|[^,}\\]]+)",
            m => $"\"{m.Groups[1].Value}\": {marca}");
    }

    private static string Tecnico(ProtocoloPreCadastroLinha linha)
    {
        var saida = new StringBuilder();
        void Linha(string chave, string? valor) => saida.Append(chave.PadRight(26)).Append(": ")
            .AppendLine(ProtocoloPreCadastro.Texto(valor));
        Linha("Etapa", ProtocoloPreCadastro.Etapa);
        Linha("Rota", ProtocoloPreCadastro.Rota);
        Linha("Metodo", "POST");
        Linha("IntegracaoId", linha.IntegracaoId.ToString());
        Linha("CorrelationId", linha.CorrelationId);
        Linha("Contrato", linha.Contrato);
        Linha("Item", linha.Item);
        Linha("Referencia OC", linha.Referencia);
        Linha("Protocolo Guardian", linha.Protocolo);
        Linha("Ticket Guardian", linha.TicketGuardian);
        Linha("Status HTTP", linha.StatusHttp?.ToString());
        Linha("Estado empresarial", linha.EstadoEmpresarial);
        Linha("Etapa funcional", linha.EtapaFuncional);
        Linha("Estado do motor", linha.EstadoMotor);
        Linha("Status da correlacao", linha.StatusCorrelacao);
        Linha("Tentativas", linha.Tentativas.ToString());
        Linha("Ultima tentativa (UTC)", linha.TentativaEm?.UtcDateTime.ToString("O"));
        Linha("Ultimo erro", linha.UltimoErro);
        Linha("Origem", linha.Origem);
        saida.AppendLine();
        var avaliacao = ReenvioProtocolo.Avaliar(linha);
        saida.AppendLine($"Reenvio: {avaliacao.Rotulo} — {avaliacao.Codigo}");
        saida.AppendLine(avaliacao.Motivo);
        saida.AppendLine();
        saida.AppendLine("O status HTTP e o corpo da resposta nao possuem coluna na origem atual; por isso "
            + "aparecem como " + ProtocoloPreCadastro.Desconhecido + " e " + ProtocoloPreCadastro.NaoArmazenado + ".");
        return saida.ToString();
    }

    private void Copiar()
    {
        var texto = abas.SelectedTab?.Controls.Count > 0 && abas.SelectedTab.Controls[0] is TextBox caixa
            ? caixa.Text : string.Empty;
        if (string.IsNullOrEmpty(texto)) { rodape.Text = "Nada a copiar na aba atual."; return; }
        try
        {
            Clipboard.SetText(texto);
            rodape.Text = $"Conteudo da aba \"{abas.SelectedTab?.Text ?? "atual"}\" copiado para a area de transferencia.";
        }
        catch (ExternalException)
        {
            rodape.Text = "Nao foi possivel acessar a area de transferencia. Tente novamente.";
        }
    }
}
