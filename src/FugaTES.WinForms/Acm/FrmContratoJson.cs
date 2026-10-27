#nullable enable
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FugaTES.WinForms.Acm;

/// <summary>
/// Visualizacao do JSON de um contrato SAP, por contrato + item.
///
/// Apresentacao apenas: nao consulta SAP, nao escreve em banco e nao dispara
/// integracao. O payload chega pronto, da projecao persistida carregada com a linha,
/// de modo que abrir esta janela nao depende de consulta ao vivo.
///
/// A origem e sempre declarada e nunca embelezada: uma reconstrucao nao e anunciada
/// como o payload originalmente recebido.
/// </summary>
public sealed class FrmContratoJson : Form
{
    private readonly TextBox jsonPersistido;
    private readonly TextBox jsonDocumentado;
    private readonly DataGridView campos;
    private readonly DataGridView extras;
    private readonly TabControl abas;
    private readonly Label cabecalho;
    private readonly Label rodape;

    public FrmContratoJson(
        string numeroContrato,
        string? item,
        Guid integracaoId,
        long versao,
        string? payloadJson,
        OrigemJsonContrato origem)
    {
        Text = $"FugaTES - Contrato {numeroContrato} / item {item ?? "(ausente)"} - JSON";
        ClientSize = new Size(1060, 660);
        MinimumSize = new Size(820, 480);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        cabecalho = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = false,
            TextAlign = ContentAlignment.TopLeft,
            Text = $"Contrato {numeroContrato} | Item {item ?? "AUSENTE NO PAYLOAD"} | "
                 + $"Versao da integracao {versao} | Integracao {integracaoId}\r\n"
                 + $"ORIGEM: {OrigemJsonContratoTexto.Rotulo(origem)}\r\n"
                 + OrigemJsonContratoTexto.Explicacao(origem),
        };

        var barra = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var copiar = new Button { Text = "Copiar JSON exibido", Width = 170, Height = 28 };
        copiar.Click += (_, _) => Copiar();
        barra.Controls.Add(copiar);
        var fechar = new Button { Text = "Fechar", Width = 90, Height = 28 };
        fechar.Click += (_, _) => Close();
        barra.Controls.Add(fechar);

        TextBox Monospaco(string nome) => new()
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), AccessibleName = nome,
        };
        jsonPersistido = Monospaco("JSON da projecao persistida");
        jsonDocumentado = Monospaco("JSON conforme documentacao SAP");

        DataGridView Grade(string nome)
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AccessibleName = nome,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            };
            void Coluna(string campo, string titulo) => g.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = campo, HeaderText = titulo, Name = campo });
            Coluna("CampoDocumentacao", "Campo (documentacao NTT)");
            Coluna("CampoSap", "Campo (servico SAP)");
            Coluna("Valor", "Valor");
            Coluna("Situacao", "Situacao");
            return g;
        }
        campos = Grade("Campos conforme documentacao SAP");
        extras = Grade("Campos fora da documentacao");

        abas = new TabControl { Dock = DockStyle.Fill };
        void Aba(string titulo, Control conteudo)
        {
            var aba = new TabPage(titulo);
            aba.Controls.Add(conteudo);
            abas.TabPages.Add(aba);
        }
        Aba("Conforme documentacao SAP", campos);
        Aba("JSON conforme documentacao", jsonDocumentado);
        Aba("JSON persistido (como armazenado)", jsonPersistido);
        Aba("Campos fora da documentacao", extras);

        rodape = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = false,
        };

        layout.Controls.Add(cabecalho, 0, 0);
        layout.Controls.Add(barra, 0, 1);
        layout.Controls.Add(abas, 0, 2);
        layout.Controls.Add(rodape, 0, 3);
        Controls.Add(layout);

        Preencher(payloadJson, origem);
    }

    private void Preencher(string? payloadJson, OrigemJsonContrato origem)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            const string semPayload =
                "SEM PAYLOAD ARMAZENADO PARA ESTA LINHA.\r\n\r\n"
                + "Nada e exibido e nada e reconstruido: a ausencia do payload nao autoriza "
                + "preencher campos por suposicao.";
            jsonPersistido.Text = semPayload;
            jsonDocumentado.Text = semPayload;
            campos.DataSource = Array.Empty<CampoDocumentadoNtt>();
            extras.DataSource = Array.Empty<CampoDocumentadoNtt>();
            rodape.Text = "Nenhum campo disponivel. "
                + "Origem declarada: " + OrigemJsonContratoTexto.Rotulo(origem) + ".";
            return;
        }

        try
        {
            jsonPersistido.Text = Indentar(payloadJson);
            jsonDocumentado.Text = ContratoSapDocumentoNtt.SerializarConformeDocumentacao(payloadJson);
            var mapeados = ContratoSapDocumentoNtt.Mapear(payloadJson);
            var fora = ContratoSapDocumentoNtt.CamposForaDaDocumentacao(payloadJson);
            campos.DataSource = mapeados.ToArray();
            extras.DataSource = fora.ToArray();

            var ausentes = mapeados.Count(c => !c.Presente);
            var resumo = new StringBuilder();
            resumo.Append(mapeados.Count - ausentes).Append(" de ").Append(mapeados.Count)
                  .Append(" campos da documentacao presentes; ").Append(ausentes)
                  .Append(" ausentes na origem.");
            if (fora.Count > 0)
                resumo.Append(' ').Append(fora.Count)
                      .Append(" campo(s) fora da documentacao preservado(s) e exibido(s).");
            resumo.Append("\r\nIdentificadores permanecem texto, com zeros a esquerda. ")
                  .Append("Decimais e datas /Date(...)/ sao mostrados como persistidos; ")
                  .Append("qualquer instante UTC vem rotulado como derivado, sem conversao de fuso.");
            rodape.Text = resumo.ToString();
        }
        catch (JsonException ex)
        {
            var erro = "PAYLOAD ARMAZENADO NAO E JSON VALIDO: " + ex.Message
                + "\r\n\r\nExibicao interrompida. Nenhum campo e inferido a partir de um "
                + "payload que nao pode ser lido.";
            jsonPersistido.Text = payloadJson;
            jsonDocumentado.Text = erro;
            campos.DataSource = Array.Empty<CampoDocumentadoNtt>();
            extras.DataSource = Array.Empty<CampoDocumentadoNtt>();
            rodape.Text = "Falha de leitura do payload. Origem declarada: "
                + OrigemJsonContratoTexto.Rotulo(origem) + ".";
        }
    }

    private static string Indentar(string json)
    {
        try
        {
            using var documento = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(documento.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // Texto preservado como esta: melhor mostrar o bruto que esconder o problema.
            return json;
        }
    }

    private void Copiar()
    {
        var texto = abas.SelectedTab?.Controls.Count > 0 && abas.SelectedTab.Controls[0] is TextBox caixa
            ? caixa.Text
            : TextoDaGrade(abas.SelectedTab?.Controls.Count > 0
                ? abas.SelectedTab.Controls[0] as DataGridView : null);
        if (string.IsNullOrEmpty(texto))
        {
            rodape.Text = "Nada a copiar na aba atual.";
            return;
        }
        try
        {
            Clipboard.SetText(texto);
            rodape.Text = "Conteudo da aba \"" + (abas.SelectedTab?.Text ?? "atual")
                + "\" copiado para a area de transferencia.";
        }
        catch (ExternalException)
        {
            // A area de transferencia pode estar tomada por outro processo.
            rodape.Text = "Nao foi possivel acessar a area de transferencia. Tente novamente.";
        }
    }

    private static string TextoDaGrade(DataGridView? grade)
    {
        if (grade?.DataSource is not IEnumerable<CampoDocumentadoNtt> linhas) return string.Empty;
        var saida = new StringBuilder();
        saida.AppendLine("CampoDocumentacao\tCampoSap\tValor\tSituacao");
        foreach (var linha in linhas)
            saida.Append(linha.CampoDocumentacao).Append('\t')
                 .Append(linha.CampoSap).Append('\t')
                 .Append(linha.Valor ?? string.Empty).Append('\t')
                 .AppendLine(linha.Situacao);
        return saida.ToString();
    }
}
