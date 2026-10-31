from pathlib import Path
r=Path(r'C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV')
def edit(path,old,new):
 p=r/path;s=p.read_text(encoding='utf-8-sig');assert old in s,path;p.write_text(s.replace(old,new),encoding='utf-8')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','public Task<ResultadoCarrierManual> ConsultarManualReadOnlyAsync(string bp,CancellationToken ct)=>Task.FromResult(ResultadoCarrierManual.Bloqueado(bp,"NOT_IMPLEMENTED"));','private readonly Func<string,CancellationToken,Task<ResultadoCarrierManual>> manualReadOnly;\n    public bool ManualReadOnlyAvailable(string bp)=>CarrierManualReadOnlyComposition.Available(bp);\n    public Task<ResultadoCarrierManual> ConsultarManualReadOnlyAsync(string bp,CancellationToken ct)\n        =>Masterdata.ConsultarReadOnlyAsync(bp,token=>manualReadOnly(bp,token),ct);')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','this.factory=factory;this.clock=clock;','this.factory=factory;this.clock=clock;this.manualReadOnly=manualReadOnly??CarrierManualReadOnlyComposition.ConsultarAsync;')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','await Masterdata.EncerrarAsync().ConfigureAwait(false);','if(stopped)await Masterdata.EncerrarAsync().ConfigureAwait(false);\n            else await Masterdata.AguardarOciosidadeAsync().ConfigureAwait(false);')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','        await task.ConfigureAwait(false);','        await task.ConfigureAwait(false);\n        await Masterdata.EncerrarAsync().ConfigureAwait(false);')
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/MasterdataSharedOrchestratorR3.cs','    public Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual> ConsultarReadOnlyAsync(string bp,Func<CancellationToken,Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual>> consultar,CancellationToken ct)=>Task.FromResult(FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"NOT_IMPLEMENTED"));',"""
    public async Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual> ConsultarReadOnlyAsync(string bp,
        Func<CancellationToken,Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual>> consultar,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_vida.Token);
        bool acquired;
        try{acquired=await _exclusao.WaitAsync(0,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"APP_CLOSED_OR_CANCELLED");}
        if(!acquired)return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"SHARED_LOCK_BUSY");
        try
        {
            var result=await consultar(linked.Token).ConfigureAwait(false);
            FugaTES.Aplicacao.Transportadoras.CarrierPersistencePolicy.ValidateCode(result.Motivo);
            Registrar(CategoriaMasterdataR3.Transportadoras,OrigemCicloMasterdataR3.Manual,result.Motivo);
            return result;
        }
        catch(OperationCanceledException) when(linked.IsCancellationRequested)
        {return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"APP_CLOSED_OR_CANCELLED");}
        catch{return FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"MANUAL_READ_FAILED");}
        finally{_exclusao.Release();}
    }
    public async Task AguardarOciosidadeAsync()
    {
        await _exclusao.WaitAsync().ConfigureAwait(false);_exclusao.Release();
    }
""")
# Explicit, separate read-only UI action. Persistent sync action remains unchanged.
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/FrmCadastrosGuardianR3.cs','    private bool _manualRunning;','    private bool _manualRunning;\n    private bool _readOnlyRunning;\n    private readonly Button _readOnly=new(){Text="CONSULTA MANUAL — SOMENTE LEITURA",Width=285,Height=30};')
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/FrmCadastrosGuardianR3.cs','        barra.Controls.Add(_bp);barra.Controls.Add(_sync);','        barra.Controls.Add(_bp);barra.Controls.Add(_sync);\n        _readOnly.Click+=async(_,_)=>await ConsultarReadOnlyNaInterfaceAsync();\n        barra.Controls.Add(_readOnly);')
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/FrmCadastrosGuardianR3.cs','    private async Task CarregarLogsAsync()',"""
    private async Task ConsultarReadOnlyNaInterfaceAsync()
    {
        if(_fechando||_readOnlyRunning||_lifecycle is null)return;
        _readOnlyRunning=true;_readOnly.Enabled=false;
        try{
            var result=await _lifecycle.ConsultarManualReadOnlyAsync(_bp.Text.Trim(),_viewCancellation.Token);
            if(!_fechando)_resumo.Text=$"CONSULTA SOMENTE LEITURA: {result.Decisao} | {result.Motivo} | Não autoriza POST/PUT.";
        }catch{if(!_fechando)_resumo.Text="Consulta interrompida; nenhuma escrita autorizada.";}
        finally{_readOnlyRunning=false;if(!_fechando)_readOnly.Enabled=_lifecycle.ManualReadOnlyAvailable(_bp.Text.Trim());}
    }

    private async Task CarregarLogsAsync()""")
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/FrmCadastrosGuardianR3.cs','        var estados = _orquestrador.Estados();','        _readOnly.Enabled=!_readOnlyRunning&&_lifecycle?.ManualReadOnlyAvailable(_bp.Text.Trim())==true;\n        var estados = _orquestrador.Estados();')
