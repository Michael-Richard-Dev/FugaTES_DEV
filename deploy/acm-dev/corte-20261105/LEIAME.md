# Corte do cadastro automático do ACM — 05/11/2026

Serviço `fugates-acm-motor-dev`. Release `R173_CORTE_20261105`.

## O pedido

"Os que não foram criados antes pode, entre aspas, excluir a solicitação; eu quero daqui pra frente,
dos próximos que vierem."

A fila tinha cerca de quarenta contratos travados — uns em reconciliação com o Guardian, uns em
recusa 400, uns em aprovação de callback — e o motor seguia tentando todos, acumulando tentativa e
backoff.

## Por que corte por data, e não lista de números

O mecanismo que já existia era `ExcludedContractNumbers`: enumerar os números. Funcionaria hoje.
Mas o pedido é **temporal** — "daqui pra frente", "os próximos que vierem" — e uma lista de quarenta
números é um retrato de um instante, que precisa de manutenção na próxima vez.

O corte diz a regra uma vez:

```csharp
public bool RecebimentoNoCorte(DateTimeOffset? recebidoEmUtc) =>
    RecebidoDesdeUtc is not { } corte || (recebidoEmUtc is { } quando && quando >= corte);
```

É o mesmo padrão que o ACM já usa em `DynamicEmitterValidationFromUtc` e que o SD usa em
`OrdensVendaSdFiltro.CorteAutomatico`.

Três consequências que valem estar explícitas:

- **Instante fixo, nunca "agora".** Com corte móvel, o contrato que chegou hoje e ainda não foi
  cadastrado sairia do recorte amanhã, sem ninguém decidir isso.
- **Contrato antigo reentregue depois do corte entra.** O recebimento é novo, ainda que o número
  seja antigo. É o que "os próximos que vierem" quer dizer.
- **Recebimento sem data não passa** quando há corte. "Não sei quando chegou" não pode valer como
  "chegou depois do corte": seria deixar entrar justamente o que não se consegue datar.

O corte é um "e" a mais, nunca um atalho: centro, par tipo + pesagem, status `T` e versão 1 continuam
valendo, e identificador sem prova no Guardian continua bloqueando antes do envio.

## Onde o corte age

No **recorte**, não num bloqueio: contrato de antes do corte deixa de ser trabalho do motor, e
não-trabalho não gera recusa. A fila antiga para de ser varrida, em vez de acumular tentativa para
sempre. O mesmo gate é repetido em `AcmR57NormalPreparation.Bloqueio` (código
`R173_ANTERIOR_AO_CORTE_AUTOMATICO`), porque ele também serve caminhos que não passam pelo recorte —
e porque regra de escopo conferida num lugar só é regra que a próxima composição esquece.

## O instante não é escolhido de cabeça

O script lê o recebimento mais recente que existe no banco, adota o momento da execução como corte e
**se recusa a seguir** se o momento não for estritamente maior que esse recebimento. A comparação é
feita pelo PostgreSQL, que entende `timestamptz`, e não por texto. Assim o corte é provadamente
capaz de excluir a fila inteira, em vez de depender de uma constante transcrita de um terminal.

O `conferir` mostra os três números antes de qualquer alteração: quantas versões existem, qual o
recebimento mais recente, e quantas sairão do recorte.

## Um detalhe de formato que mereceu teste

O script escreve o corte como `date -u +%Y-%m-%dT%H:%M:%S.%6NZ`, por exemplo
`2026-11-05T00:30:11.360468Z`. `AcmAutomaticoOptions.EhValida()` exige `Offset == TimeSpan.Zero`;
um texto que ligasse com outro offset invalidaria o recorte **inteiro** e o motor pararia de
cadastrar qualquer contrato — fail-closed, mas uma surpresa péssima, e por uma letra no fim da
string. `AcmCorteAutomaticoTests` liga a string exata pelo binder de configuração e confere o
offset, a validade do recorte e as duas bordas do corte.

## Como aplicar

No Windows, nesta pasta:

```powershell
.\Enviar-CorteAcm.ps1
```

Na VM:

```bash
rm -rf ~/r173 && mkdir ~/r173 && tar -xzf ~/fugates-motor-acm.tgz -C ~/r173
```

```bash
sudo bash ~/publicar-corte-acm.sh conferir
```

```bash
sudo bash ~/publicar-corte-acm.sh instalar
```

```bash
sudo bash ~/publicar-corte-acm.sh reverter
```

Release e configuração num passo só, pela razão registrada em `../centros-20261104/LEIAME.md`:
separar as duas coisas foi o que permitiu, em 04/11, um script anunciar sucesso sobre um arquivo que
o systemd ignorava. O publicador descobre o binding canônico, troca só o caminho do release dentro
dele, escreve o corte num `corte.env` ligado como último `EnvironmentFile`, e confere o **efeito**:
o `ExecStart` que o systemd resolveu, as marcas no binário em execução, e o corte visível em
`/proc/<pid>/environ`.

## O que esperar

A fila antiga deixa de ser tentada. Ela **continua no banco e visível** — nada foi apagado, e
`reverter` a traz de volta ao recorte.

Daqui em diante, contrato que o SAP entregar nos centros 3011 ou 3008 segue o caminho completo:
contrato no Guardian por SOAP e, em seguida, protocolo por REST — este último liberado em
`../protocolo-20261105`.
