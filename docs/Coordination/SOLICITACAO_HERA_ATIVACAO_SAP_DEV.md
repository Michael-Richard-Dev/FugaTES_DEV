# Solicitação à HERA V4 — ativação do recebimento SAP no DEV

Motivo: o SAP emitiu o contrato **0000001160** em 28/10/2026 17:59:07 (usuário FMOTTA, log
`00000000000011307836`), com retorno **HTTP 201**, e ele **não apareceu no FugaTES DEV110**.

---

## O que aconteceu — apurado, não suposto

O contrato não chegou ao DEV. Verificação no banco `fugates_dev` (cluster `18/fugatesacmdev`,
porta 5433), que é o único que o DEV110 lê:

| Verificação | Resultado |
|---|---|
| 1160 em `fugates.contrato_sap_toledo_versao` | **ausente** |
| Qualquer rastro de "1160" em `fugates.integracao` | **nenhum** |
| Integrações criadas em 28/10 | **0** |
| Última integração de qualquer contrato | 27/10 12:52 |
| Receptor SAP DEV (porta 18764) | **ausente — nunca instalado** |

Não houve sequer recusa de admissão: se o contrato tivesse batido no receptor DEV e sido barrado
pela janela R57, haveria registro. Não há.

**O HTTP 201 é legítimo, mas veio do receptor HML** — hoje o único endpoint FugaTES exposto ao
SAP (`192.168.17.40:80 → 127.0.0.1:8080`). O emissor SAP/CPI continua apontado para lá porque a
rota DEV nunca foi publicada. É exatamente o estado que o gate anterior registrou:
`SAP_RECEIVER_INSTALLED=NO`, `SAP_DEV_INGRESS=PENDING`, `R57_WINDOW=CLOSED`.

**Nada quebrou.** O contrato foi para onde o SAP estava configurado para enviá-lo.

Não foi feita — nem será, sem autorização específica — nenhuma cópia do contrato do HML para o DEV:
isso seria replay histórico e fabricação de evidência.

---

## O que já está pronto do lado técnico

| Item | Estado |
|---|---|
| Identidade de escrita `fugates_sap_inbound_app` | autentica em `fugates_dev:5433` |
| Guardas de conexão Npgsql | destino divergente recusado antes de conectar |
| Pacote do receptor | `FUGATES_SAP_INBOUND_RECEIVER_DEV.zip`, zero credenciais |
| Porta loopback | **18764**, escolhida por preflight, fora de todo serviço existente |
| Credencial Basic | gerada na VM em arquivo 600, nunca no repositório nem em log |
| Credencial Guardian | dispensada na partida, sem reutilizar segredo exposto |
| Instalação, verificação e rollback | `Instalar-ReceptorDev.ps1`, ensaiado |
| **Ativação em um comando** | `Ativar-ReceptorDev.ps1`, ensaiado de ponta a ponta |
| API de leitura 18762 | promovida para `fugates_dev_read_app`, servindo o DEV110 |

`Ativar-ReceptorDev.ps1` recebe as definições abaixo como parâmetros, valida cada uma, gera a
configuração sem pendências, reempacota, instala com backup e rollback e verifica o resultado.
O template versionado **permanece fail-closed**: a configuração ativada é gerada à parte.

---

## O que precisamos da Hera — quatro itens

### 1. Janela de admissão R57

| Campo | Observação |
|---|---|
| `WindowId` | GUID no formato "N": 32 hexadecimais minúsculos, sem hifens |
| `AuthorizationReference` | referência da autorização; **não é inventada aqui** |
| `NotBeforeUtc` / `UntilUtc` | intervalo de no máximo 1 dia |

### 2. Escopo do contrato de homologação

Centro, parceiro SAP e material do contrato que será usado. Se for o próprio **1160**, basta o SAP
informar esses três valores do payload já emitido. O centro atualmente previsto é `3011`.

A allowlist não será ampliada por conveniência: ela admite exatamente o parceiro e o material
informados.

### 3. Usuário da autenticação Basic

O nome de usuário acordado com o emissor SAP/CPI. O segredo é gerado na VM por nós, em arquivo de
modo 600, e entregue ao emissor pelo canal aprovado — ele nunca transita por chat, repositório ou
log. A conferência é feita por impressão digital SHA-256, sem expor o valor.

### 4. Rota DEV autorizada

Host, porta TLS, nome DNS e certificado, mais a confirmação de que o emissor SAP/CPI será
repontado para essa rota.

O ingress HML **não serve** ao DEV, por três motivos concretos: envia `X-Forwarded-Proto: http`
(o receptor exige `https` e devolveria 403), **remove** o header `Authorization` (Basic → 401) e
aponta para o serviço HML, que não pode ser alterado. A proposta de server block separado está
pronta em `deploy/sap-inbound-dev/nginx-fugates-dev-sap.conf.PROPOSTA` e **não foi publicada**.

---

## O que acontece quando os quatro itens chegarem

Um único comando ativa tudo:

```
.\Ativar-ReceptorDev.ps1 -WindowId <...> -AuthorizationReference <...> `
   -NotBeforeUtc <...> -UntilUtc <...> -Parceiro <...> -Material <...> -UsuarioBasic <...>
```

Ele valida os parâmetros, prepara a credencial, gera a configuração, instala o receptor em
`127.0.0.1:18764` com backup e rollback, e confirma `/health` 200 com as rotas de callback Guardian
e Toledo devolvendo **404** — o receptor aceita contrato SAP e nada mais.

Falta então apenas publicar a rota DEV e repontar o emissor. No envio seguinte, a cadeia completa
fica comprovável: **SAP/CPI → FugaTES → `COMMIT` 5433 → H4 → API 18762 → DEV110**, com Guardian
SOAP/REST desabilitado neste primeiro ingresso e sem reenvio de histórico.

## Restrições preservadas

HML 8080, produção, 18761, 18762 e 18763 intocados; nenhum worker Guardian ativado; janela R57
fechada até a autorização; nenhum POST externo executado; nenhum arquivo do Codex modificado.
