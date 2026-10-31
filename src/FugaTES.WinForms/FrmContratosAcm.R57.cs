using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;

namespace FugaTES.WinForms;

public partial class FrmContratosAcm
{
    private TextBox? xmlR57,jsonR57;
    private void ConfigurarExecucaoR57()
    {
        Text="FugaTES — ACM R57 — SAP recebido / Contrato SOAP / Protocolo REST";
        centro.ReadOnly=true;atualizar.Text="Atualizar estados";atualizar.Width=150;
        if(atualizar.Parent is FlowLayoutPanel filters)
        {
            filters.AutoScroll=true;
            filters.Controls.Add(new Label{AutoSize=true,Padding=new Padding(4,7,4,0),Text="Próximo cenário: 1106 — aguardando evento SAP"});
            filters.Controls.Add(new Button{Text="Enviar SOAP — bloqueado",Enabled=false,Width=190});
            filters.Controls.Add(new Button{Text="Enviar REST — bloqueado",Enabled=false,Width=190});
        }
        grid.Columns.Clear();
        foreach(var name in new[]{"Etapa","Estado","Evidencia"})grid.Columns.Add(new DataGridViewTextBoxColumn{Name=name,HeaderText=name});
        grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
        var tabs=new TabControl{Dock=DockStyle.Fill};
        var execution=new TabPage("Execução / Correlação");execution.Controls.Add(grid);tabs.TabPages.Add(execution);
        TextBox Add(string name)
        {
            var tab=new TabPage(name);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",10),AccessibleName=name};
            tab.Controls.Add(t);tabs.TabPages.Add(tab);return t;
        }
        xmlR57=Add("XML SOAP");jsonR57=Add("JSON REST mínimo");
        if(Controls[0] is TableLayoutPanel layout){layout.Controls.Add(tabs,0,1);layout.RowStyles[2].Height=115;status.AutoEllipsis=false;}
        atualizar.Click+=(_,_)=>ApresentarExecucaoR57();FormClosing+=(_,_)=>cancelamento.Cancel();
        ApresentarExecucaoR57();
    }

    // Presentation only. Supplied observations must come from the approved runtime reader, never a demo fixture.
    // This method cannot dispatch SOAP/REST, enqueue work or access PostgreSQL.
    public void ApresentarExecucaoR57(VersaoContratoSapToledo? version=null,AcmR57Vinculos? links=null,ResultadoFluxoMvp? result=null)
    {
        if(xmlR57 is null||jsonR57 is null)return;
        grid.Rows.Clear();
        void Row(string step,string state,string evidence)=>grid.Rows.Add(step,state,evidence);
        Row("1. Contrato SAP recebido",version is null?"NÃO RECEBIDO NESTA SESSÃO":version.NumeroContrato,
            version is null?"POST /api/v1/integracoes/sap/contratos; 1106 é cenário informado, não payload recebido":$"Item {version.Snapshot.Item ?? "PENDENTE"} | Centro {version.Snapshot.Centro} | Versão integração {version.Versao}");
        AcmR57Plano? plan=null;string mapping="PENDENTE — origens e vínculos funcionais não carregados";
        if(version is not null&&links is not null)
        {
            try{plan=AcmR57Mapping.Criar(version,links);mapping="COMPOSTO — não autoriza envio";}
            catch(AcmR57BlockedException ex){mapping="BLOQUEADO — "+ex.Code;}
        }
        Row("2. Validação / mapeamento",mapping,"Emissor/item Guardian, documento/peso e valor adicional exigem origens comprovadas");
        var hasContract=result?.Etapa is "R57_SOAP_CONFIRMED" or "R57_REST_ARMED" or "R57_CONFIRMED";
        Row("3. Contrato Guardian",hasContract?"CONFIRMAÇÃO REGISTRADA":result?.Etapa ?? "NÃO CONSULTADO",hasContract?"Código confirmado na etapa durável; não confundir sequencial com protocolo":"1031 já criado manualmente; não repetir CREATE. 1106 sem execução real neste pacote");
        Row("4. Protocolo Guardian",result?.ProtocoloGuardian ?? "NÃO CRIADO",result is null?"REST somente após SOAP e journal durável":result.Status.ToString());
        var parts=result?.ResultadoFuncional?.Split(':');
        Row("5. Correlação",version?.IntegracaoId.ToString() ?? "NÃO DISPONÍVEL",$"Sequencial contrato: {(parts?.Length==3&&parts[0]=="R57"?parts[1]:"NÃO DISPONÍVEL")} | Referência: {links?.NumeroReferencia ?? "PENDENTE"} | Ticket: {result?.NumeroTicketGuardian ?? "NÃO ATRIBUÍDO"}");
        Row("Erros",result?.ErroCodigo ?? result?.Erro ?? "Nenhuma execução realizada","Timeout/dispatch ambíguo permanece INDETERMINATE; sem retry automático");
        xmlR57.Text=plan is null?"XML NÃO COMPOSTO: aguarda evento SAP real e mapeamentos comprovados.":GuardianContratoSoapCodec.CriarCadastro(plan.Contrato).ToString();
        jsonR57.Text=plan is null?"JSON NÃO COMPOSTO: documento/peso/referência/valor adicional/callback não podem ser inventados.":JsonSerializer.Serialize(GuardianSet26HttpClient.CriarPayloadR57(plan.Protocolo),new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true});
        status.Text="R57 — entrada por POST SAP preservada; GET diagnóstico não dispara integração.\r\nRuntime de escrita DESABILITADO. PostgreSQL DEV/journal físico e perfil HML não atestados; nenhuma chamada real nesta tela.\r\nItem SAP não é versão da integração; sequencial do contrato, ID do protocolo e ticket são identidades distintas.\r\nSem workers/timers/escritas automáticas. Esta sessão offline não representa E2E HML concluído.";
    }
}
