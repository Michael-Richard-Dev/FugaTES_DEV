# Protocolo após o contrato, no ACM — 05/11/2026

Serviço `fugates-acm-motor-dev`.

## Não há mudança de código

O fluxo que o operador pediu — "cria primeiro o contrato e logo em seguida cria o protocolo" — já
está construído em `AcmR57Pipeline.Normal`:

```
contrato (SOAP)  →  protocolo (REST, EnviarMinimoAcmAsync)  →  AguardandoCallback
```

E o protocolo inicial nasce de propósito **sem** transportadora, sem documento e sem dados de
pesagem (`CriacaoInicialSemDadosPesagem = true`): o `AcmR57Mapping.Validar` recusa esses campos
nessa criação. É exatamente "logo em seguida", antes de qualquer pesagem existir.

"Já era assim, porém não está funcionando" estava certo nas duas metades. O que recusava era um
portão de **configuração**: `AcmR57NormalPreparation.PrepararProtocolo` exige callback aprovado, e no
ambiente havia `AcmR57__Normal__CallbackApproved=false` e nenhuma das outras chaves de callback.
Trinta e tantos contratos estavam parados aí, 3011 e 3008 juntos — não era problema de centro.

## O que a política exige

```csharp
if(uri.Scheme=="https") return true;
return url==InternalHmlUrl && binding is {Environment:"HML",SourceCidr:"192.168.17.91/32",
    SourceAllowlistVerified:true,PreCadastroReturnRouteBindingVerified:true} && binding.Url==url &&
    !string.IsNullOrWhiteSpace(binding.EvidenceReference);
```

O Guardian devolve por HTTP, então vale só a exceção documentada: uma única URL —
`http://192.168.17.40/api/guardian/set26/callback/recebimento`, a rota do ACM no receptor
compartilhado — e um binding exato.

## Os dois atestados, e por que o script os reconfere

`SourceAllowlistVerified` e `PreCadastroReturnRouteBindingVerified` não são configuração: são
**afirmações** de que alguém conferiu a liberação de origem e a rota de retorno. Escrever `true` nelas
de cabeça seria fabricar atestado — e atestado falso é pior do que bloqueio, porque o bloqueio é
visível e ele não.

Por isso o script reconfere, **no momento da escrita**, e se recusa a seguir se qualquer item não se
sustentar:

| Conferência | Como |
|---|---|
| origem liberada no firewall | regra de aceite para `192.168.17.91` na porta 80, na cadeia `FUGATES_HTTP80` |
| e usada de verdade | o contador da regra tem de ser maior que zero |
| rota existe | `location = /api/guardian/set26/callback/recebimento` no `nginx -T` |
| rota libera a origem | `allow 192.168.17.91` dentro dessa location |
| rota chega ao receptor | `proxy_pass` para a porta 18086 |
| receptor atende | HTTP na rota em `127.0.0.1:18086` |

Conferido em 04/11/2026: a regra do firewall tinha contado **268 pacotes / 72 KB** — tráfego real,
não configuração no papel. É a mesma rota por onde chegaram as pesagens 0020344 e 0020345.

`--evidencia` é obrigatório e **não tem valor padrão**, de propósito: a referência tem de nomear a
conferência que foi feita. Um padrão genérico seria o mesmo atestado fabricado, só escondido.

## Como aplicar

No Windows, nesta pasta:

```powershell
.\Enviar-ProtocoloAcm.ps1
```

Na VM:

```bash
sudo bash ~/habilitar-protocolo-acm.sh conferir
```

```bash
sudo bash ~/habilitar-protocolo-acm.sh instalar --evidencia R173-CALLBACK-CONFERIDO-20261105
```

```bash
sudo bash ~/habilitar-protocolo-acm.sh reverter
```

As chaves vão para `/etc/fugates/motor-dev/protocolo-callback.env`, ligado como **último**
`EnvironmentFile` do drop-in canônico — `EnvironmentFile` acumula e o último vence por chave, então
elas prevalecem sobre o `CallbackApproved=false` declarado antes, sem editar o arquivo de quem o
declarou.

Depois de reiniciar, o script confere o **efeito**: lê `/proc/<pid>/environ` do motor e exige ver
`CallbackApproved=true` no processo em execução. Se não vir, restaura e reverte. É a mesma lição do
dia anterior — conferir o que o sistema resolveu, não o arquivo que se escreveu.

## O que esperar

Os contratos que já têm `guardian_codigo_confirmado` e estão em `R165U_CALLBACK_APPROVAL_REQUIRED`
devem passar a criar protocolo e ir para `AguardandoCallback`, com `guardian_protocolo_id` e
`guardian_numero_ticket` gravados. O script lista todos eles no fim.

Os contratos 1207, 1208 e 1188 **não** vão avançar: eles param antes, em
`R165AG_EXISTING_CONTRACT_RECONCILIATION_REQUIRED`, porque já existem no Guardian por cadastro
manual. Isso é outra decisão, ainda aberta.
