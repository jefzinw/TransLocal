# TransLocal

Veja (e, se quiser, use) a tela de um computador em outro, ligados por cabo — ou exiba um notebook ligado por HDMI a uma placa de captura.

- **Transmitir / Receber** — instale o TransLocal nos dois computadores. Quem recebe fica aguardando na área de notificação, como um recurso do Windows; quem transmite escolhe o receptor e clica em **Transmitir**. A tela recebida abre sozinha, em tela cheia, com som.
- **HDMI (placa de captura)** — ligue o HDMI do notebook a uma placa de captura USB conectada ao PC. Nada precisa ser instalado no notebook.

Funciona nos dois sentidos: notebook → PC ou PC → notebook.

## ⬇ Download

**[Baixar a versão mais recente](https://github.com/jefzinw/TransLocal/releases/latest)** — na página que abrir, em *Assets*:

- **`TransLocal-Setup-x.y.z.exe`** — instalador (recomendado)
- **`TransLocal.exe`** — executável único, roda sem instalar

Instale nos dois computadores. Não é preciso ter o .NET instalado.

## Que cabo usar

| Ligação | Funciona? |
|---|---|
| Cabo de rede (RJ45) direto entre os dois | ✔ Sem configurar nada (o Windows usa endereços 169.254.x.x). Sem porta de rede? Use um adaptador USB-Ethernet. |
| USB-C ↔ USB-C com **USB4/Thunderbolt nos dois** computadores | ✔ O Windows 11 cria sozinho o “USB4 P2P Network Adapter”. Use cabo Thunderbolt/USB4. |
| Cabo “bridge”/“data link” USB 3.0 (chip Prolific PL27A1 ou similar), em modo rede | ✔ |
| Wi-Fi / roteador (mesma rede) | ✔ Com um pouco mais de atraso. |
| HDMI do notebook → placa de captura USB (UVC) → PC | ✔ Aba **HDMI**. |
| Cabo USB-A ↔ USB-A comum | ✘ Não funciona e pode danificar as portas. |
| HDMI ↔ HDMI direto entre notebook e PC | ✘ As duas portas HDMI são de saída. |

## Instalação

**Instalador (recomendado):** execute `TransLocal-Setup-1.0.0.exe` nos dois computadores. Ele:
- instala em *Arquivos de Programas* e cria o atalho no Menu Iniciar;
- libera o TransLocal no Firewall do Windows (necessário: ligações diretas por cabo são tratadas como “rede pública”);
- opcionalmente inicia o TransLocal com o Windows, já pronto para receber;
- aparece em *Configurações › Aplicativos* para desinstalar.

**Executável único (sem instalar):** copie `TransLocal.exe` e execute. Não precisa do .NET instalado. Na aba *Configurações e ajuda*, clique em **Liberar no Firewall do Windows** no computador que vai receber.

> O executável não tem assinatura digital. Na primeira execução o Windows pode mostrar “O Windows protegeu o computador”: clique em **Mais informações › Executar assim mesmo**.

## Como usar

1. Ligue o cabo entre os dois computadores e abra o TransLocal nos dois.
2. No computador que vai **mostrar** a tela (ex.: o PC): aba **Receber nesta tela** — deixe “Aceitar transmissões” ligado.
3. No computador que vai **enviar** a tela (ex.: o notebook): aba **Transmitir esta tela** › **Procurar receptores**, escolha o PC (prefira a linha “cabo…”) e clique em **Transmitir**.
4. A tela abre no PC em tela cheia. Leve o mouse ao topo para ver os botões.

Opções do transmissor: monitor a transmitir, **resolução enviada**, quadros por segundo (até 60), qualidade, som do sistema e **permitir que o outro computador use o mouse, o teclado e os controles** (desligado por padrão).

### Transmissão leve (notebooks e computadores mais simples)

- **Resolução enviada: Automática** (padrão, no transmissor): a imagem é reduzida para o tamanho exato da tela de quem exibe (ex.: 1920×1080 → 1366×768). O computador que exibe não precisa redimensionar nada e decodifica menos pixels. Também é possível fixar um limite (1600×900 … 854×480) ou enviar a resolução original.
- **Pedir transmissão leve** (no computador que exibe, aba *Receber*): pede ao transmissor no máximo 1366×768, 30 quadros/s e mais compressão, e usa um redimensionamento mais rápido na tela.
- Com a tela parada, nada é reenviado: só as regiões que mudam viajam pela rede.

### Controlar o outro computador (mouse, teclado e controles USB/Bluetooth)

Exemplo: o notebook exibe a tela do PC e você quer jogar ou usar o PC pelo notebook.

1. **No PC (que transmite):** aba **Transmitir** › marque *Permitir que o outro computador use o mouse, o teclado e os controles de jogo dele neste computador*. Para controles de jogo, clique em **Instalar suporte a controles** (instala o driver ViGEmBus pelo winget; o Windows pede permissão de administrador). Transmita.
2. **No notebook (que exibe):** com *Começar já controlando…* marcado (padrão), o mouse, o teclado e os controles (USB ou **Bluetooth**) do notebook passam a comandar o PC assim que a tela abre.

- Cada controle do notebook vira um **controle de Xbox 360 virtual** no PC, reconhecido pelos jogos; a vibração volta para o controle do notebook (inclusive DualShock/DualSense).
- Controles reconhecidos (via [SDL3](https://www.libsdl.org/)): Xbox 360/One/Series, PlayStation 3/4/5, Switch Pro e centenas de modelos genéricos, por USB ou Bluetooth. Os botões são posicionais: o botão de baixo (✕ no PlayStation) vira o A.
- **Controle não reconhecido ou com botões trocados?** Aba *Receber* › selecione o controle na lista › **Configurar botões do controle selecionado…** — um assistente pede cada botão, gatilho e analógico, e o ajuste fica salvo para aquele modelo.
- Para parear por Bluetooth: *Configurações do Windows › Bluetooth e dispositivos › Adicionar dispositivo*, com o controle em modo de pareamento (PS4: Share + PS; PS5: Create + PS; Xbox: botão de pareamento; genéricos: veja o manual).
- Os controles só são enviados enquanto a janela de exibição está em primeiro plano. **Ctrl+Alt+Shift+C** solta/retoma o controle.
- Sem a Xbox Game Bar instalada, o Windows mostraria a janela “Obter um aplicativo para abrir este link ‘ms-gamebar’” a cada controle conectado; o TransLocal registra um manipulador silencioso para o seu usuário (desfeito na desinstalação, ou com `TransLocal.exe --restore-gamebar-prompt`).

### Atalhos

| Atalho | Onde | Ação |
|---|---|---|
| Ctrl+Alt+Shift+T | qualquer lugar | inicia/para a transmissão para o último receptor |
| Ctrl+Alt+Shift+H | qualquer lugar | abre/fecha a visualização HDMI |
| F11 / Esc | janela de exibição | alterna / sai da tela cheia |
| Ctrl+Alt+Shift+F | janela de exibição | tela cheia (também com o controle ligado) |
| Ctrl+Alt+Shift+C | janela de exibição | liga/desliga o controle do outro computador |
| Ctrl+Alt+Shift+Q | janela de exibição | fecha |

### Usar como segundo monitor (área estendida)

O TransLocal transmite um monitor existente. Para estender a área de trabalho, instale no computador que transmite um driver de monitor virtual (Indirect Display Driver, por exemplo o projeto *Virtual Display Driver*), ajuste-o em *Configurações › Sistema › Tela* e escolha esse monitor na aba **Transmitir**. Com a placa de captura HDMI isso já acontece: o notebook enxerga um monitor externo (use Win+P).

## Solução de problemas

- **O receptor não aparece na busca:** confira se o TransLocal está aberto no outro computador com “Aceitar transmissões” ligado e se o firewall está liberado (aba *Configurações e ajuda*). Digite o endereço mostrado na aba *Receber* do outro computador.
- **Cabo USB-C não cria rede:** os dois computadores precisam de USB4/Thunderbolt e o cabo precisa ser Thunderbolt/USB4. Veja se aparece “USB4 P2P Network Adapter” em *Gerenciador de Dispositivos › Adaptadores de rede*.
- **HDMI travando em 5 quadros/s:** escolha um formato MJPEG (placas USB 2.0 só chegam a 30/60 quadros/s com compressão). Feche OBS, Câmera ou Teams se a placa estiver em uso.
- **Imagem preta em parte da tela:** vídeos protegidos (DRM) não podem ser capturados pelo Windows.
- **Controle de jogo não funciona:** na barra da janela de exibição (leve o mouse ao topo) aparece o estado dos controles. “Falta o driver no outro PC” → instale o suporte a controles no PC. Na aba *Receber* aparece a lista de controles detectados neste computador; se o seu não aparecer, confira o pareamento Bluetooth; se aparecer como “não reconhecido”, use **Configurar botões**.
- **Imagem lenta ou travando no computador que exibe:** marque *Pedir transmissão leve* nele e, no transmissor, deixe *Resolução enviada: Automática*.
- **Controle não funciona em janelas de administrador:** o Windows bloqueia entrada simulada em programas elevados, a não ser que o TransLocal também seja executado como administrador. A tela de UAC e a tela de bloqueio não podem ser transmitidas.
- **Registros:** aba *Configurações e ajuda › Abrir pasta de registros* (`%LOCALAPPDATA%\TransLocal`).

## Como funciona

- **Captura:** DXGI Desktop Duplication (a mesma API da gravação de tela do Windows, ~4 ms por quadro em 1080p), com GDI como reserva automática. O cursor é desenhado na imagem.
- **Codificação:** a tela é dividida em faixas de 64 px; só os trechos que mudaram são enviados, em JPEG codificado em paralelo (~4 ms para um quadro 1080p inteiro). Controle de fluxo com no máximo 2 quadros em trânsito mantém o atraso baixo mesmo quando a rede é mais lenta.
- **Rede:** TCP 52020 (vídeo, som, controle) e UDP 52021 (descoberta por broadcast em todas as interfaces, inclusive 169.254.x.x).
- **Som:** loopback WASAPI do transmissor → PCM 16 bits → reprodução com descarte de atraso acumulado.
- **Redução de resolução:** média de área (separável, ponto fixo, linhas em paralelo), ~6 ms para 1920×1080 → 1366×768; quando a tela não muda, captura, redução e compressão são puladas.
- **Controles de jogo:** o receptor lê os controles pela SDL3 (USB/Bluetooth, com XInput direto como reserva) a cada 4 ms e envia só as mudanças; o transmissor os reproduz como controles de Xbox 360 virtuais (ViGEmBus) e devolve a vibração.
- **HDMI:** dispositivos UVC via DirectShow (FlashCap), com repasse opcional do áudio da placa.

Bibliotecas: [NAudio](https://github.com/naudio/NAudio) (MIT), [FlashCap](https://github.com/kekyo/FlashCap) (Apache-2.0), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) — detalhes em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Compilar

Requisitos: SDK do .NET 10 e, para o instalador, Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # dist\win-x64\TransLocal.exe + dist\TransLocal-Setup-1.0.0.exe
powershell -ExecutionPolicy Bypass -File build.ps1 -IncludeArm64   # também dist\win-arm64\TransLocal.exe
```

Diagnóstico (resultados em `%LOCALAPPDATA%\TransLocal\TransLocal.log`):

```powershell
TransLocal.exe --selftest <pasta>   # transmissão completa pela interface local; salva a imagem recebida
TransLocal.exe --bench              # mede captura, codificação e decodificação
TransLocal.exe --gamepad-test       # cria um controle virtual e confere pelo XInput (requer ViGEmBus)
```

Estrutura:

```
src/TransLocal/
  Program.cs              entrada, instância única, modos de linha de comando
  Protocol.cs             mensagens TCP e handshake
  Discovery.cs            descoberta UDP e classificação das ligações (cabo/Wi-Fi)
  Streaming/              SenderSession (transmissor) e ReceiverServer (receptor)
  Video/                  captura DXGI/GDI, codificador por faixas, superfície de exibição, HDMI
  Audio/                  som do sistema, reprodução e repasse da placa
  Input/                  controle remoto de mouse e teclado; controles de jogo (XInput → Xbox 360 virtual)
  UI/                     área de notificação, janela principal, janela de exibição
installer/TransLocal.iss  instalador (Inno Setup)
tools/make-icon.ps1       gera assets/TransLocal.ico
tools/publish-github.ps1  publica o código e a Release no GitHub (requer gh auth login)
```
