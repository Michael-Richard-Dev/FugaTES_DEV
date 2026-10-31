from pathlib import Path
r=Path(r'C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV')
def edit(path,old,new):
 p=r/path;s=p.read_text(encoding='utf-8-sig');assert old in s,path;p.write_text(s.replace(old,new),encoding='utf-8')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','TimeProvider clock)\n    {','TimeProvider clock,Func<string,CancellationToken,Task<ResultadoCarrierManual>>? manualReadOnly=null)\n    {')
edit('src/FugaTES.WinForms/Transportadoras/CarrierDesktopLifecycle.cs','    private readonly TimeProvider clock;','    private readonly TimeProvider clock;\n    public Task<ResultadoCarrierManual> ConsultarManualReadOnlyAsync(string bp,CancellationToken ct)=>Task.FromResult(ResultadoCarrierManual.Bloqueado(bp,"NOT_IMPLEMENTED"));')
edit('src/FugaTES.WinForms/Transportadoras/MasterdataR3/MasterdataSharedOrchestratorR3.cs','    public async Task EncerrarAsync()','    public Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual> ConsultarReadOnlyAsync(string bp,Func<CancellationToken,Task<FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual>> consultar,CancellationToken ct)=>Task.FromResult(FugaTES.Aplicacao.Transportadoras.ResultadoCarrierManual.Bloqueado(bp,"NOT_IMPLEMENTED"));\n\n    public async Task EncerrarAsync()')
