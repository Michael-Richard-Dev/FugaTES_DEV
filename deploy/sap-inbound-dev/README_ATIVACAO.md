# Receptor SAP DEV — pacote isolado (R158)

`RECEIVER_ACTIVE=NO`. Este pacote **não instala e não ativa nada**. Ele prepara a ativação
controlada do receptor empresarial, que depende de autorização específica da Hera para
**porta, rota, escopo e janela**.

## O que este pacote reaproveita (nada é reescrito)

```
POST /api/v1/integracoes/sap/contratos        ← SapContractInboundEndpoint.MapDurable
  → AceitarContratoSapToledoDuravelCasoDeUso
    → ContratoSapToledoVersaoStorePostgreSql  ← identidade + H4 + versão na MESMA transação
      → fugates_dev : 5433 / schema fugates
```

Nenhum receptor novo, nenhuma persistência paralela, nenhum worker Guardian, nenhum SOAP/REST.

## Separação obrigatória de instâncias

| Instância | Porta | Papel | Tocada por este pacote |
|---|---|---|---|
| API privada de leitura DEV | 18762 (remota) | `--dev-unified-read-only`, GET-only | **Não** |
| Serviço autenticado de comandos | 18763 (remota) | reenvio manual controlado | **Não** |
| **Receptor SAP DEV** | `<<PENDENTE_PORTA_LOOPBACK_APROVADA_HERA>>` | este pacote | sim |

O receptor é uma instância **separada**, com porta própria. As rotas hoje usadas pelo SAP/CPI
não são alteradas por este pacote.

## Por que não basta ligar `ProductiveEnabled`

Ligar apenas essa chave **não** restabelece o fluxo, e isso é proposital:

1. `SapContractInboundProductiveComposition` exige `SecurityMode` ∈ {`LoopbackOnly`,
   `LoopbackBasicHttps`} e credenciais Basic presentes, senão lança
   `SAP_INBOUND_SECURITY_MODE_REQUIRED` / `SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED`.
2. Com `AutomaticProcessingEnabled=true` e **sem escopo ACM**, lança
   **`SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED`** na partida.
3. Com o serviço de pé mas o snapshot fora do escopo, o endpoint devolve **422
   `SAP_INBOUND_SCOPE_NOT_AUTHORIZED`**.

Os três guards permanecem no código, exercitados pela suíte `R158.SapInboundReadiness.Tests`.

## Destino de banco é imposto pelo código, não pela configuração

`NpgsqlDataSourceFactory.Criar` roteia a role `fugates_acm_hml_app` para `CriarAcm`, que **recusa**
qualquer conexão que não seja porta **5433**, database `fugates_dev`, schema `fugates`, host
loopback e `Passfile` explícito (com `PGPASSWORD` ausente), e instala um verificador de
identidade física em cada conexão. Não é possível apontar o receptor para 5432, 5434 ou produção.

## Passos de ativação (exigem autorização da Hera)

1. **Hera aprova**: porta loopback do receptor, rota de ingresso, escopo ACM e janela.
2. Preencher em `appsettings.receiver.dev.json` todos os `<<PENDENTE...>>`:
   - `Urls` com a porta aprovada;
   - `SapInbound:BasicAuth:Username` e a referência de segredo (**nunca** senha em texto claro no
     arquivo — usar o provedor de segredo do ambiente);
   - `MotorIntegracao:AcmInboundWindow`: `Enabled=true`, `WindowId` (GUID formato `N`),
     `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` em UTC com duração **≤ 1 dia**,
     `AllowedSapPartners` e `AllowedSapMaterials` **não vazios**.
3. Rodar `Validar-ReceptorDev.ps1`. Ele **não instala e não conecta**: apenas verifica estrutura,
   destino de banco, guards e ausência de pendências. Qualquer `<<PENDENTE` → `BLOCKED`.
4. Confirmar privilégios do role `fugates_acm_hml_app` no 5433 para `INSERT`/`UPDATE` em
   `fugates.contrato_sap_toledo_identidade`, `fugates.contrato_sap_toledo_versao` e
   `fugates.integracao`. **A evidência disponível comprova apenas leitura.** Sem `GRANT` neste
   pacote: se faltar privilégio, devolver para a Hera.
5. Publicar a instância isolada na VM, preservando os processos 18762 e 18763.
6. Apontar o SAP/CPI para a rota aprovada, através do proxy que fornece
   `X-Forwarded-Proto: https`, mantendo loopback e Basic auth.
7. Comprovação ponta a ponta: somente um **envio SAP real** dentro da janela aberta.
   Depois: `GET /dev/read/contracts?selector=TODOS` deve trazer o contrato com JSON e
   `integrationId`, e a grade do DEV110 deve exibi-lo em até 10 s, sem reinício.

## Rollback

`Rollback-ReceptorDev.ps1` remove a instância encenada e restaura a configuração anterior a
partir do backup que o próprio script cria antes de qualquer escrita. Como nada é instalado por
este pacote, o rollback no estado atual é um **no-op verificável**: ele confirma que nenhuma
instância do receptor foi encenada e que 18762/18763 seguem intactos.

## Pendência conhecida de leitura

O seletor `TODOS` (todos os centros autorizados) foi implementado em `FugaTES.Persistencia`, que
roda **dentro da API privada de leitura na VM**. A API instalada é de 27/10 e, até ser
republicada, tratará `TODOS` como um centro literal e devolverá zero linhas. A tela do DEV110 já
avisa isso explicitamente em vez de afirmar ausência de registros. Republicar a API de leitura é
uma alteração **GET-only**, sem mudança de comportamento além do filtro.
