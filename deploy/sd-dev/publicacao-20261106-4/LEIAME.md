# Publicação do motor SD — 06/11/2026, quarta do dia

**Motor e desktop.** O receptor não precisa: a correção está no `MapeadorTextoNfeSd`, e quem compõe
o texto da NF-e é o motor (`fugates-sd-protocolo`) e a tela. Commit `256ab9a`.

Pacote: sha `dc1394b3...`. O receptor continua em `SD_DONO_20261106_135922Z`.

## O que esta publicação corrige

A remessa **80003934** saiu com `Placa:` **vazio** e com a transportadora escrita dentro de
`Placa Transportadora:` — um campo que espera placa. Dois defeitos, uma raiz: aquele rótulo vizinho
não estava na lista de rótulos conhecidos.

O texto daquela ordem tem, na mesma linha:

```
Mot.:  CPF:  Placa:  Placa Transportadora:
```

**1. A placa não entrava.** O espaço de um campo vai até o próximo rótulo **conhecido**. Sem conhecer
`Placa Transportadora:`, o espaço de `Placa:` se estendia por cima dele e engolia a palavra
"Placa" — parecia preenchido, e a regra 2 (não tocar no que a ordem já trouxe) deixava a placa de
fora, em silêncio.

É a mesma família do defeito de 05/11 com `amostras testemunhas`.

**2. A transportadora ia para o campo errado.** `Transportadora:` casa **dentro** de
`Placa Transportadora:`, então o rótulo curto encontrava lugar no longo. Conhecer o vizinho não
bastava: a comparação passou a recusar casamento parcial, e vence o rótulo **mais específico**.

Sem rótulo próprio no texto, a transportadora vai numa linha ao fim — a regra 3 —, que é melhor do
que o campo errado.

## Como publicar

### Motor

```powershell
cd "C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\deploy\sd-dev\publicacao-20261106-4"; .\Enviar-MotorSd.ps1
```

Tem que imprimir `sha256 ... dc1394b3...`.

```bash
sudo ~/instalar-motor-sd.sh instalar
```

### Desktop

Feche o `FugaTES.exe` antes.

```powershell
cd "C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\deploy\desktop-dev"; .\Publicar-Desktop.ps1
```

O publicador ganhou o marcador `PedacoDeRotuloMaior`. Conferido: ele acusa **ausente** no desktop
publicado hoje mais cedo e **presente** no que esta publicação compila — ou seja, discrimina esta
correção especificamente, e não apenas "tem algo novo".

## Conferir o efeito

Na próxima remessa cujo texto traga `Placa Transportadora:`:

```bash
sudo journalctl -u fugates-sd-protocolo -f
```

No texto gravado, `Placa:` tem de sair **preenchido** com as duas placas (`veículo / carreta`), e
`Placa Transportadora:` tem de continuar como a ordem o trouxe — vazio, se vazio estava. A
transportadora aparece numa linha ao fim, no rótulo dela.

Se `Placa:` sair vazio de novo, ou se `Placa Transportadora:` vier com o código da transportadora,
o que subiu ainda é o código antigo.

## Um ajuste que é na balança, não aqui

A observação daquela pesagem chegou assim:

```
148594,148595,148596,148597 N dos envelopes de seguranca das amostras ⏎ 59187,59188,59189
```

O **rótulo foi digitado dentro da observação**, e por isso o texto saiu com ele duplicado. O código
fez o certo — linha 1 é lacre de carga, inteira como veio; linha 2 é testemunha —, mas a linha 1 não
deveria conter o texto do rótulo, que já existe na nota.

Na balança, a observação deve ter só os números:

```
148594,148595,148596,148597
59187,59188,59189
```
