# Procedimento da manhã — contrato SAP no FugaTES e cadastro no CloudPrix

Tudo preparado em 29/10/2026 de madrugada. A URL do SAP **não mudou**.

---

## 1. O SAP envia o contrato

Nada a fazer. O contrato chega na URL de sempre, o receptor grava em `fugates_dev:5433` e ele
aparece no **DEV110**, em Integrações → Contratos ACM, com o JSON.

Para conferir pelo banco:

```bash
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w \
  "host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app" \
  -c "select numero_contrato, versao, criado_em from fugates.contrato_sap_toledo_versao order by criado_em desc limit 5"
```

## 2. Ligar o motor para esse contrato

Com o **número** e o **item** do contrato que chegou:

```bash
sudo bash ~/motor-cloudprix.sh ligar <NUMERO_CONTRATO> <ITEM>
```

O script recusa se o contrato ainda não estiver no banco. Para acompanhar:

```bash
sudo journalctl -u fugates-acm-motor-dev -f
```

## 3. Ao terminar

```bash
sudo bash ~/motor-cloudprix.sh desligar
```

---

## Estado verificado no encerramento

| | |
|---|---|
| `fugates-callback-http` (receptor SAP) | **active**, gravando em `fugates_dev:5433` como `fugates_sap_inbound_app` |
| `fugates-acm-motor-dev` (motor CloudPrix) | **failed** — correto: a trava impede subir sem escopo |
| Listeners | `8080`, `18761`, `18762`, `18763`, `5433` |
| Contratos no banco unificado | 7, mais recente `9999000001` (teste) |
| API de leitura 18762 | HTTP 200 |

## O que foi corrigido, e por quê importava

Três defeitos reais, todos encontrados por teste, qualquer um deles quebraria a manhã:

1. **Banco errado.** Existem duas bases chamadas `fugates_dev`, em clusters diferentes. O receptor
   gravava na legada (5432) e o DEV110 lê a unificada (5433). Por isso o contrato 1160 voltou
   `HTTP 201` e nunca apareceu. Corrigido com porta e role no `runtime.env`.
2. **Privilégios incompletos.** A role de escrita foi criada apertada demais para o release que
   está em execução. Faltavam `SELECT` em `integracao` (só 8 colunas estavam liberadas, sem
   `estado`), `SELECT` em `pausa_processamento` e `SELECT, INSERT` em `tentativa_integracao`.
   Delta `008`.
3. **`search_path` ausente.** A role não tinha `search_path` e a connection string do serviço não
   passa `Search Path`. Sem isso nenhuma tabela resolvia. Corrigido com
   `ALTER ROLE fugates_sap_inbound_app SET search_path = fugates, pg_catalog`.

Prova final: o serviço de produção devolveu `201 REENTREGA_IDEMPOTENTE` — conectou, reconheceu o
contrato pelo fingerprint e **não duplicou**.

## Avisos

**O destino é o CloudPrix real.** `Guardian__BaseUrl` é `https://guardian.cloudprix.com.br`, apesar
de o arquivo de credenciais se chamar `guardian-rest-hml`. Ligar o motor cria cadastro de verdade.

**Contrato de teste removido.** O sintético `9999000001` foi apagado do banco em 29/10 (delta `009`,
transacional, com postcheck confirmando que os 6 contratos legítimos ficaram intactos). O banco
está limpo: **zero** integrações elegíveis, então o contrato real do SAP será o único candidato e o
motor parte de estado limpo.

**A trava do motor.** O `ExecStartPre` impede a partida sem contrato no escopo. Sem ela, um
`systemctl start` direto faria o motor despachar **qualquer** integração pendente ao CloudPrix,
inclusive o contrato sintético. Comprovado: `systemctl start` sem escopo resulta em `failed`.

**Reversões disponíveis.**

```bash
sudo bash ~/apontar-recebimento-sap-para-dev-unificado.sh reverter   # volta o receptor para a base legada
sudo bash ~/motor-cloudprix.sh desligar                             # para o motor e limpa o escopo
```

## O que continua manual, e por quê

Ligar o motor exige o número do contrato porque ele só existe quando o SAP o emite, e porque é o
que transforma "automático e cego" em "automático e dirigido". Depois que o primeiro ciclo real
fechar e vocês virem o cadastro no CloudPrix, essa trava pode ser removida com segurança — aí sim o
fluxo roda fim a fim sem intervenção.
