# Perfis de iluminação (RESET FERRAMENTAS › Integração)

Pedido do Mateus em 2026-10-25: o efeito configurado na aba Integração se perde ao fechar o app.
Branch local `perfis-iluminacao` (cópia do repositório do GustavoReset; nada vai para o GitHub sem
aprovação do Mateus).

## O que muda para quem usa

1. **Perfis com nome.** No topo do quadro EFEITO:

   ```
   EFEITO
   Perfil  [ Jogo          v ]  [NOVO] [RENOMEAR] [EXCLUIR]
   (Cor fixa) (Respiração) (Arco-íris) ...
   ```

   - O perfil guarda: efeito, cor (R, G, B), brilho, velocidade, "Controle exclusivo" e quais
     dispositivos estão desmarcados.
   - **Salva sozinho**: qualquer mudança nesses controles altera o perfil escolhido e grava em
     `settings.json` na hora (sem botão salvar).
   - Na primeira vez existe um perfil "Padrão" com os valores de hoje (Cor fixa, branco, 100 %, 5,
     exclusivo ligado, todos marcados).
   - NOVO: pede um nome e cria uma cópia do perfil atual, que passa a ser o escolhido.
   - RENOMEAR: pede o novo nome. EXCLUIR: pede confirmação; o último perfil não pode ser excluído.
   - Nomes: sem espaços nas pontas, não vazios, sem repetir (ignorando maiúsculas/minúsculas),
     até 40 caracteres.
   - Ao abrir o app, a aba volta com o último perfil escolhido. Ao trocar de perfil com o controle
     ativo, o efeito novo entra na hora.
   - O perfil não reativa o controle sozinho ao abrir o app (o Mateus não quis isso).

2. **Botão DEIXAR NO OPENRGB** (ao lado de ATIVAR CONTROLE / DEVOLVER AO ORIGINAL).
   - Habilitado só com o controle ativo, efeito Cor fixa ou Apagar e pelo menos um dispositivo
     OpenRGB marcado. Nos outros casos fica desabilitado com dica: "Respiração, Arco-íris e os
     efeitos de sensor precisam do app aberto".
   - Ao clicar: envia mais uma vez as cores do perfil aos dispositivos OpenRGB, manda o OpenRGB
     salvar um perfil dele chamado `ResetExplode` e para o controle **sem devolver** o modo
     original dos dispositivos OpenRGB (as cores ficam). Dispositivos Corsair e Razer são
     devolvidos como hoje (o iCUE e o Synapse reassumem).
   - Depois mostra o guia (abaixo). Fechar o app sem clicar continua devolvendo tudo ao
     original, como hoje.

3. **Guia do OpenRGB ao iniciar.** Depois de gravar, aparece na aba (texto, não janela):
   "Pronto: as cores ficaram no OpenRGB, no perfil ResetExplode. Para elas voltarem quando o PC
   ligar: 1) no OpenRGB, abra Settings › General; 2) marque Start at login e Start minimized,
   e Load profile com o perfil ResetExplode; 3) deixe o servidor SDK ligado (Start server) se
   quiser continuar usando o ResetExplode." (conferir os nomes exatos das opções na versão 0.9 /
   1.0 do OpenRGB antes de escrever o texto final).

## Como funciona por dentro

- `AppSettings`: `List<LightProfile> LightProfiles` e `string? LightProfileActive`.
  `LightProfile { Name, Effect, R, G, B, Brightness, Speed, Exclusive, List<string> Unselected }`
  (`Unselected` guarda `LightDevice.Key` dos desmarcados, que é o que `RebuildLightList` já usa).
- Lógica de perfis (criar cópia, renomear, excluir, validar nome, garantir "Padrão", achar o
  ativo) numa classe sem WPF, `LightProfiles.cs`, para poder testar.
- `MainWindow.Integ.cs`: ao iniciar a aba, aplica o perfil ativo nos controles **antes** de
  ligar os eventos de "salvar"; uma flag evita gravar enquanto o perfil está sendo carregado.
  Os handlers que já existem (`LightFx_Checked`, `LightColor_Changed`) e os novos (checkbox
  exclusivo e checkbox de cada dispositivo) atualizam o perfil e chamam `_settings.Save()`.
  O perfil só é aplicado nos controles quando a aba é aberta (como hoje), já que o controle não
  liga sozinho.
- OpenRGB (`OpenRgbBackend`): pacote `NET_PACKET_ID_REQUEST_SAVE_PROFILE = 151` (protocolo ≥ 2;
  o app já exige 3), cabeçalho com dispositivo 0, carga = nome do perfil em ASCII terminado em
  `\0`. O OpenRGB grava o estado atual de todos os controladores (modo + cores) no perfil.
  Novo método `SaveProfile(string name)` e um `Keep(LightDevice d)`/`Detach` que só esquece o
  dispositivo sem mandar `UpdateMode` (o oposto do `Release`). A montagem dos bytes do pacote
  fica num método estático testável.
- `StopLighting` ganha um caminho "parar sem devolver os OpenRGB".

## Testes

Não há projeto de testes no repositório. Criar `tests/ResetExplode.Tests` (xUnit, net8.0-windows)
e **excluir `tests/**` do `ResetExplode.csproj`** (o SDK incluiria esses .cs no app). Cobrir:

- regras de nome (vazio, espaços, repetido ignorando caixa, > 40);
- NOVO copia todos os campos e vira o ativo; EXCLUIR o ativo escolhe outro; não exclui o último;
- `settings.json` antigo (sem os campos novos) carrega com o perfil "Padrão";
- ida e volta JSON de um perfil com todos os campos;
- bytes do pacote SAVE_PROFILE (cabeçalho `ORGB`, dispositivo 0, tipo 151, tamanho, nome + `\0`).

Teste real (Mateus, na máquina dele com OpenRGB): criar dois perfis, fechar e abrir o app;
DEIXAR NO OPENRGB com Cor fixa, fechar o app e ver as cores ficarem; reiniciar o PC com o OpenRGB
configurado pelo guia.
