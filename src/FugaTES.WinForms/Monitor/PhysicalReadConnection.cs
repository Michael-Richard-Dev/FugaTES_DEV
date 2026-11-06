namespace FugaTES.WinForms.Monitor;

public sealed class PhysicalConnectionException(string code):InvalidOperationException(code)
{
    public PhysicalConnectionException(string code,bool resourceOnly):this(code){ResourceOnly=resourceOnly;}
    public string Code{get;}=code;
    public bool ResourceOnly{get;}
}

public sealed record PhysicalReadConnection
{
    public bool Connected{get;init;}
    public bool PilotOnly{get;init;}=true;
    public string Cluster{get;init;}="";
    public int ServerPort{get;init;}
    public string Database{get;init;}="";
    public string Role{get;init;}="";
    public DateTimeOffset? LastSuccessUtc{get;init;}
    public string Message{get;init;}="Aguardando conexão de leitura.";

    /// <summary>
    /// Porta REMOTA da API privada declarada no perfil (18762 no perfil unificado, 18761 no
    /// anterior). A porta LOCAL do túnel é sempre 127.0.0.1:18761, conforme
    /// LocalReadTunnel ("-L 127.0.0.1:18761:127.0.0.1:&lt;remota&gt;"), e é a que o cliente de
    /// leitura exige. As duas são exibidas para que a mensagem de desconexão não sugira um
    /// endpoint que não corresponde ao perfil em uso.
    /// </summary>
    public int RemoteReadPort{get;init;}
    private string EndpointEsperado => RemoteReadPort>0
        ?$"API privada: túnel local 127.0.0.1:{LocalReadPort} → porta remota {RemoteReadPort}"
        :$"API privada: túnel local 127.0.0.1:{LocalReadPort}";
    public const int LocalReadPort=18761;
    public string Display => (Connected?"CONECTADO — ":"DESCONECTADO — ")+Message+
        (Connected?$"\r\nPostgreSQL REAL | {Cluster} | {Database} | porta física {ServerPort} | schema fugates | {Role}":
        $"\r\nDestino esperado: 18/fugatesacmdev | fugates_dev | porta física 5433 | {EndpointEsperado}")+
        "\r\nÚltima leitura física: "+(LastSuccessUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")??"ainda não realizada")+
        (PilotOnly?". Guardian SIMULADO; somente leitura.":". Natureza por registro; somente leitura.");

    public PhysicalReadConnection Failure(string code)=>this with {Connected=false,Message=code switch{
        "READ_KEY_REJECTED"=>"Chave de leitura rejeitada pela API. Verifique o provider autorizado.",
        "READ_KEY_UNAVAILABLE"=>"Arquivo privado da chave de leitura ausente ou inválido.",
        "READ_KEY_ACCESS_DENIED"=>"A identidade Windows desta sessão não tem acesso à chave. Use o Desktop no usuário TI; não copie a chave nem amplie a ACL.",
        "UNIFIED_DEV_ROUTE_UNAVAILABLE"=>"A API ativa não disponibiliza esta leitura unificada. Confira o perfil/release; isso não comprova ausência de registros.",
        "READ_KEY_ACL_REJECTED"=>"Permissões da chave de leitura não correspondem a TI/SYSTEM.",
        "READ_PROFILE_REQUIRED"=>"Perfil local de leitura não configurado ou inválido.",
        "LOCAL_PORT_UNTRUSTED"=>$"Porta local {LocalReadPort} ocupada por processo sem o binding SSH aprovado. Nenhuma chave enviada.",
        "SSH_BINDING_REQUIRED"=>"Binding SSH de leitura ausente ou inválido.",
        "SSH_PREREQUISITE_MISSING"=>"Cliente SSH, identidade ou registro confiável de host indisponível.",
        "SSH_CONNECT_FAILED"=>"Túnel SSH indisponível: confira autenticação, host conhecido e acesso de rede.",
        "INTEGRATION_NOT_FOUND"=>"IntegrationId não encontrado na API física autorizada.",
        "UNIFIED_DEV_DATABASE_IDENTITY_REJECTED" or "R165AL_PHYSICAL_READBACK_IDENTITY_REJECTED" or "R165AL_SUCCESSFUL_TERMINAL_FACTS_REQUIRED"=>"Identidade ou estado físico retornado não corresponde ao contrato de leitura autorizado.",
        "MM_VM_OBSERVATION_SCOPE_REJECTED"=>"Leitura MM recusada: ambiente ou identidade do snapshot não corresponde ao motor autorizado.",
        "MM_VM_OBSERVATION_DOCUMENT_REJECTED"=>"Leitura MM recusada: snapshot incompatível ou inválido. Conferir a versão do leitor MM; isso não indica indisponibilidade das outras consultas.",
        "MM_VM_OBSERVATION_UNAVAILABLE"=>"Snapshot MM indisponível para leitura. As outras consultas usam fontes independentes.",
        "MM_VM_OBSERVATION_NOT_CONFIGURED"=>"Leitura do motor MM não configurada na API privada.",
        "READBACK_TIMEOUT"=>"Tempo limite na leitura da API privada. Nenhum dado de demonstração substituiu a consulta.",
        _=>"Backend privado indisponível ou resposta inválida. Nenhum serviço remoto foi iniciado; tente Atualizar."}};
}
