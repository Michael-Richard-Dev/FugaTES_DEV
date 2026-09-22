# INC006 Guardian HTTP SET26 Design

## Objetivo

Preparar adapters HTTP Guardian SET26 para token Bearer, Emissor, Transportadora e Pre-Cadastro sem executar rede real.

## Isolamento

O transporte sera implementado em um novo projeto `FugaTES.Integracao.Guardian`, referenciando somente `FugaTES.Aplicacao`. O projeto `FugaTES.Integracao.Toledo` e todos os contratos INC002-005 permanecem inalterados.

## Configuracao

`GuardianHttpOptions` exige, sem defaults produtivos:

- `BaseUrl` absoluta;
- `Timeout` positivo;
- `TransportadoraEndpoint` relativo;
- `PreCadastroEndpoint` relativo;
- `TokenUserField`;
- `TokenPasswordField`;
- `TokenResponseField`.

As rotas confirmadas sao constantes relativas:

- token: `/api/v2/CreateToken/`;
- emissor: `/api/v0/Guardian/Emissor`.

A troca da BaseUrl futura ocorre somente por configuracao.

## Credenciais e Token

`ICredenciaisGuardianSet26Provider` fornece usuario e senha em runtime. `GuardianTokenClient` monta o JSON usando os nomes de campos configurados, faz POST no endpoint confirmado e extrai o token pela propriedade configurada.

Status nao-success, body nao JSON, propriedade ausente ou token vazio falham fechados. Credenciais, token e header Authorization nunca sao logados.

## Operacoes Guardian

`GuardianSet26HttpClient` implementa ports separados para Emissor, Transportadora e Pre-Cadastro. Cada operacao obtem um token, cria uma requisicao POST JSON com `application/json`, aplica `Authorization: Bearer`, respeita timeout e `CancellationToken` e exige resposta success.

Emissor usa a rota confirmada. Transportadora e Pre-Cadastro exigem endpoints configurados. O payload SET26 usa os modelos do INC006-A e preserva `tipoDocumento` `"0"`/`"1"`, campos `1000/1002/1003`, contrato sem `C/D` e ausencia de `1=Interno`.

O Pre-Cadastro exige body JSON valido contendo protocolo Guardian nao vazio. Emissor e Transportadora aceitam body vazio em status success porque nenhum contrato de resposta foi fornecido.

## Testes

Um `HttpMessageHandler` fake captura requisicoes sem rede. Os testes cobrem token, Bearer, rotas e metodos, payloads SET26, configuracao ausente, status nao-success, token invalido, timeout/cancelamento e ausencia dos campos legados.

## Bloqueios

- A primeira chamada real de Emissor depende de BaseUrl e credenciais runtime.
- A primeira chamada real de Pre-Cadastro depende de BaseUrl, credenciais e rota confirmada.
- Transportadora depende tambem da rota confirmada.
- Callback HTTP inbound, SAP real e migration permanecem fora do escopo.
