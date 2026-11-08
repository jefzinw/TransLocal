# TransLocal

Veja (e, se quiser, use) a tela de um computador em outro, ligados por cabo — ou exiba um notebook ligado por HDMI a uma placa de captura.

- **Transmitir / Receber** — instale o TransLocal nos dois computadores. Quem recebe fica aguardando na área de notificação, como um recurso do Windows; quem transmite escolhe o receptor e clica em **Transmitir**. A tela recebida abre sozinha, em tela cheia, com som.
- **HDMI (placa de captura)** — ligue o HDMI do notebook a uma placa de captura USB conectada ao PC. Nada precisa ser instalado no notebook.

Funciona nos dois sentidos: notebook → PC ou PC → notebook.

## ⬇ Download

**[Baixar a versão mais recente](https://github.com/jefzinw/TransLocal/releases/latest)** — na página que abrir, em *Assets*:

- **`TransLocal-Setup-x.y.z.exe`** — instalador (recomendado)
- **`TransLocal.exe`** — executável único, roda sem instalar
- **`TransLocal-Android.apk`** — app de celular que aprova acessos remotos e mostra o código de autorização

Instale nos dois computadores. Não é preciso ter o .NET instalado. **A versão 1.3 só conversa com a 1.3 ou mais nova** (as conexões passaram a ser criptografadas): atualize os dois computadores.

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

**Atualização automática:** ao abrir (e a cada 12 h, se ficar aberto na bandeja), o TransLocal procura uma versão nova nas Releases deste repositório, baixa em segundo plano, confere o SHA-256 publicado pelo GitHub e instala — esperando terminar qualquer transmissão. Na cópia instalada, o Windows pede permissão de administrador uma vez e o app reabre sozinho; no executável único, o arquivo é trocado e reaberto. Pode ser desligada em *Configurações e ajuda › Atualizações*, onde também há “Procurar atualizações agora”. *(Quem está na 1.1.0 ou anterior precisa instalar a 1.2.0 manualmente uma vez.)*

> O executável não tem assinatura digital. Na primeira execução o Windows pode mostrar “O Windows protegeu o computador”: clique em **Mais informações › Executar assim mesmo**.

## Como usar

1. Ligue o cabo entre os dois computadores e abra o TransLocal nos dois.
2. No computador que vai **mostrar** a tela (ex.: o PC): aba **Receber nesta tela** — deixe “Aceitar transmissões” ligado.
3. No computador que vai **enviar** a tela (ex.: o notebook): aba **Transmitir esta tela** › **Procurar receptores**, escolha o PC (prefira a linha “cabo…”) e clique em **Transmitir**.
4. **Na primeira vez**, os dois computadores mostram um **código de 6 dígitos**: confira se é o mesmo nas duas telas e confirme nas duas. Depois de pareados, eles se reconhecem sozinhos.
5. A tela abre no PC em tela cheia. Leve o mouse ao topo para ver os botões.

Opções do transmissor: monitor a transmitir, **resolução enviada**, quadros por segundo (até 60), qualidade, som do sistema e **permitir que o outro computador use o mouse, o teclado e os controles** (desligado por padrão).

### Transmissão leve (notebooks e computadores mais simples)

- **Resolução enviada: Automática** (padrão, no transmissor): a imagem é reduzida para o tamanho exato da tela de quem exibe (ex.: 1920×1080 → 1366×768). O computador que exibe não precisa redimensionar nada e decodifica menos pixels. Também é possível fixar um limite (1600×900 … 854×480) ou enviar a resolução original.
- **Pedir transmissão leve** (no computador que exibe, aba *Receber*): pede ao transmissor no máximo 1366×768 e mais compressão, e usa um redimensionamento mais rápido na tela. Com vídeo H.264 (abaixo), o modo leve aceita 60 quadros/s.
- Com a tela parada, nada é reenviado: só as regiões que mudam viajam pela rede.
- **Vídeo H.264 (a partir da 1.6, nos dois computadores):** a imagem é codificada pela placa de vídeo do PC (ou pelo Windows, se não houver). Usa cerca de **6× menos rede** que os blocos JPEG das versões anteriores (1366×768 a 60 quadros/s em ~5–8 Mbit/s, contra 35–50 Mbit/s) — é o que permite 60 quadros/s num notebook simples e evita sobrecarregar o Wi-Fi e o roteador. Se um dos lados não tiver H.264, a transmissão volta sozinha aos blocos JPEG. A barra de estatísticas mostra qual está em uso.
- **Exibição pela placa de vídeo (1.7):** no computador que exibe, a imagem vai para a tela por uma cadeia de troca DXGI, como nos apps de jogos na nuvem: a conversão de cores e o redimensionamento ficam com a placa de vídeo, e cada quadro aparece assim que é decodificado, sem esperar a fila de mensagens da janela. Medido: ~3 ms por quadro, contra ~7 ms (modo leve) a ~21 ms (normal) no desenho anterior pelo processador. A decodificação usa a placa de vídeo quando o Windows a libera; senão, o processador decodifica e só a imagem pronta vai para a placa. As estatísticas mostram os dois.
- **Fluidez como em jogos na nuvem (1.7):** o PC captura no ritmo do jogo (cada quadro assim que o jogo o apresenta, sem relógio próprio dessincronizado), os quadros-chave periódicos foram eliminados e o tamanho de cada quadro é limitado (sem picos na rede), a taxa de bits se ajusta sozinha quando o Wi-Fi engasga, as threads de vídeo usam a prioridade multimídia do Windows e o Wi-Fi entra no modo de streaming (sem varreduras de redes em segundo plano durante a transmissão). O som fica com ~40 ms de folga, sem acumular atraso.
- **Atraso da imagem:** as estatísticas mostram, nos dois computadores, o tempo da captura no PC até o quadro aparecer na tela de quem exibe (média e pico de cada segundo).
- **Vídeo por UDP com correção de erros (1.8):** como no Xbox Cloud e no Moonlight, o vídeo sai por UDP em pacotes de ~1,2 KB, com paridade Reed-Solomon (10% a 50%, conforme a perda medida) e cifragem AES-GCM por pacote (chave nova a cada transmissão, combinada pelo canal TLS já autenticado). Um pacote perdido no Wi-Fi é reconstruído na hora, sem esperar retransmissão; se um quadro se perder de vez, a imagem segura por um instante e um quadro-chave é pedido, em vez de "pixelar". Controle, som e confirmações continuam pelo canal TCP seguro. Se o UDP for bloqueado no caminho, a transmissão segue pelo TCP sozinha. Medido com perda simulada: 5% → 200/200 quadros inteiros; 15% → 198/200.
- **Resolução (2.1):** com vídeo H.264, o PC nunca envia mais pixels do que a tela de quem exibe — mesmo com “Resolução enviada: Original”. A redução é feita na placa do PC, sem perda de nitidez, e evita sobrecarregar o Wi-Fi e a placa do notebook.
- **Qualidade (1.8):** ~10 Mbit/s para 1366×768 a 60 q/s e ~20 Mbit/s para 1080p60 na qualidade alta; o modo leve não reduz mais a qualidade do H.264 (só a dos blocos JPEG), e a taxa adaptativa nunca desce abaixo da metade da escolhida.
- **Tudo pela placa de vídeo, nos dois lados (1.9):** no PC, a captura fica na placa, o cursor é desenhado nela, o processador de vídeo da placa reduz e converte a imagem e o codificador lê essa textura direto — nada passa pelo processador (antes, só reduzir 1920×1080 para 1366×768 levava ~11 ms por quadro e limitava a transmissão a ~36 q/s). No notebook, o H.264 é decodificado direto pela placa (D3D11VA, como no Moonlight) e vai para a tela sem cópia: ~1,5 ms por quadro, mesmo em placas simples como a Intel UHD 600. Medido: 59 q/s a 60 q/s, atraso da imagem de ~13 ms entre captura e tela (sem contar o Wi-Fi).
- **Perfil High do H.264 (1.9):** quando o notebook decodifica pela placa, o PC usa o perfil High (CABAC + transformada 8×8): imagem mais nítida pela mesma taxa de bits.
- **Qualidade de imagem (2.0):** taxa de bits pensada para jogos em rede de casa (como o Moonlight): 1366×768 a 30 q/s ~9 Mbit/s, a 60 q/s ~15 Mbit/s; 1080p60 até ~35 Mbit/s na qualidade máxima. A redução da imagem no PC e a exibição no notebook usam o modo de alta qualidade do processador de vídeo da placa.
- **Visual novo (2.0):** tema escuro com cartões arredondados, navegação em pílulas, interruptores e destaques em laranja e roxo, no Windows e no app do celular (o código de autorização aparece num anel que esvazia até trocar).

### Controlar o outro computador (mouse, teclado e controles USB/Bluetooth)

Exemplo: o notebook exibe a tela do PC e você quer jogar ou usar o PC pelo notebook.

1. **No PC (que transmite):** aba **Transmitir** › marque *Permitir que o outro computador use o mouse, o teclado e os controles de jogo dele neste computador*. Para controles de jogo, clique em **Instalar suporte a controles** (instala o driver ViGEmBus pelo winget; o Windows pede permissão de administrador). Transmita.
2. **No notebook (que exibe):** com *Começar já controlando…* marcado (padrão), o mouse, o teclado e os controles (USB ou **Bluetooth**) do notebook passam a comandar o PC assim que a tela abre.

- Cada controle do notebook vira um **controle de Xbox 360 virtual** no PC, reconhecido pelos jogos; a vibração volta para o controle do notebook (inclusive DualShock/DualSense).
- Controles reconhecidos (via [SDL3](https://www.libsdl.org/)): Xbox 360/One/Series, PlayStation 3/4/5, Switch Pro e centenas de modelos genéricos, por USB ou Bluetooth. Os botões são posicionais: o botão de baixo (✕ no PlayStation) vira o A.
- **Controle não reconhecido ou com botões trocados?** Aba *Receber* › selecione o controle na lista › **Configurar botões do controle selecionado…** — um assistente pede cada botão, gatilho e analógico, e o ajuste fica salvo para aquele modelo.
- Para parear por Bluetooth: *Configurações do Windows › Bluetooth e dispositivos › Adicionar dispositivo*, com o controle em modo de pareamento (PS4: Share + PS; PS5: Create + PS; Xbox: botão de pareamento; genéricos: veja o manual).
- Os controles só são enviados enquanto a janela de exibição está em primeiro plano. **Ctrl+Alt+Shift+C** solta/retoma o controle.
- **Mouse em jogos:** quando um jogo prende ou esconde o cursor (câmera em 1ª/3ª pessoa), o TransLocal passa sozinho a enviar **movimento relativo** (como um mouse ligado direto no PC) e prende o cursor na janela de exibição. **Ctrl+Alt+Shift+M** força o modo normal se precisar. O teclado é enviado pelo código físico das teclas (o que os jogos leem), e todas as teclas são soltas quando a janela perde o foco.
- **Som:** o som recebido toca na saída padrão do Windows e **acompanha a troca** — ligou um fone Bluetooth ou USB durante a transmissão, o som passa para ele sozinho. Para escolher outra saída, use o botão **Som** na barra da janela de exibição (leve o mouse ao topo) ou **Ctrl+Alt+Shift+A**; a escolha fica salva e, se aquele dispositivo estiver desligado, o som volta ao padrão.
- **Programas de administrador e confirmações do Windows (UAC):** o Windows não deixa um programa comum enviar mouse e teclado para programas abertos como administrador (Gerenciador de Tarefas, jogos com “Executar como administrador”), e a confirmação do UAC aparece numa área de trabalho segura que nenhum programa comum consegue ver nem clicar — a imagem congela no outro computador. No PC, aba **Transmitir**, marque *Controlar programas de administrador e confirmações do Windows (UAC)*: o Windows pede permissão uma vez e o TransLocal instala o **serviço do TransLocal** (como o serviço do Parsec). O serviço roda como SYSTEM e mantém um agente na sua sessão do Windows que aplica mouse/teclado em qualquer janela, inclusive na confirmação do UAC, e captura essa tela para a transmissão. O agente só aceita comandos do próprio TransLocal desta sessão (confere o executável instalado nos dois lados); a parte de rede continua sem privilégios. Só funciona na versão instalada. Desmarcar remove o serviço.
- Sem a Xbox Game Bar instalada, o Windows mostraria a janela “Obter um aplicativo para abrir este link ‘ms-gamebar’” a cada controle conectado; o TransLocal registra um manipulador silencioso para o seu usuário (desfeito na desinstalação, ou com `TransLocal.exe --restore-gamebar-prompt`).

### Atalhos

| Atalho | Onde | Ação |
|---|---|---|
| Ctrl+Alt+Shift+T | qualquer lugar | inicia/para a transmissão para o último receptor |
| Ctrl+Alt+Shift+H | qualquer lugar | abre/fecha a visualização HDMI |
| F11 / Esc | janela de exibição | alterna / sai da tela cheia |
| Ctrl+Alt+Shift+F | janela de exibição | tela cheia (também com o controle ligado) |
| Ctrl+Alt+Shift+C | janela de exibição | liga/desliga o controle do outro computador |
| Ctrl+Alt+Shift+M | janela de exibição | força o mouse normal (desliga o modo relativo de jogos) |
| Ctrl+Alt+Shift+A | janela de exibição | troca a saída de som (padrão do Windows → cada saída ligada) |
| Ctrl+Alt+Shift+D | janela de exibição | mostra/esconde o diagnóstico ao vivo da transmissão |
| Ctrl+Alt+Shift+Q | janela de exibição | fecha |

### Acesso remoto (de fora de casa) com aprovação pelo celular

Exemplo: você está fora com o notebook e quer usar o PC de casa, que ficou ligado com o TransLocal aberto.

1. **Em casa, uma vez só:**
   - pareie o notebook com o PC (basta uma transmissão entre os dois — veja *Como usar*);
   - no PC, aba **Acesso remoto** › marque *Permitir acesso remoto a este computador*;
   - instale o **Tailscale** (gratuito, [tailscale.com](https://tailscale.com/download)) no PC, no notebook e no celular, com a mesma conta — é o que faz os aparelhos se enxergarem pela internet sem abrir portas no roteador;
   - instale o **`TransLocal-Android.apk`** no celular, abra-o na mesma rede do PC, toque em **Parear com um computador** e confirme o mesmo código no celular e no PC. Toque também em *Permitir funcionar em segundo plano*.
2. **Fora de casa:** no notebook, aba **Acesso remoto** › escolha o PC, confira o endereço (o 100.x.x.x do PC no Tailscale) e clique em **Pedir a tela**.
3. O celular recebe a notificação **“Notebook quer acessar o PC — Permitir / Negar”**. Ao permitir, a tela do PC abre no notebook, sem ninguém tocar no PC.
4. Sem o celular conectado? Digite no notebook o **código de autorização** de 6 dígitos que o app mostra (muda a cada 30 s; também funciona com Google/Microsoft Authenticator — QR na aba *Acesso remoto* do PC).

### Segurança

- Cada aparelho tem uma **chave criptográfica própria** (no Windows, guardada pelo sistema e não exportável).
- Toda conexão usa **TLS** (criptografia de imagem, som, teclado e mouse) e **autenticação mútua**: cada lado prova quem é com a sua chave.
- Dois aparelhos só conversam depois de **pareados**, com um código de 6 dígitos confirmado nas duas telas. O código é gerado de forma que um intermediário não consegue fazer as duas telas mostrarem o mesmo número. **Se aparecer um pedido de pareamento que você não esperava, cancele.**
- **O pareamento só acontece na rede local da sua casa.** Quem estiver em outra rede — internet ou Tailscale — não consegue nem pedir o código: de fora, só entram aparelhos que você já pareou em casa. A descoberta automática também só responde à rede local. Um estranho que baixe o TransLocal do GitHub não tem como entrar: não tem a chave de um aparelho pareado, e a chave do seu PC e a lista de aparelhos ficam na sua conta do Windows.
- A regra do **Firewall do Windows** criada pelo TransLocal só aceita conexões da rede local e do Tailscale; a internet aberta é bloqueada pelo próprio Windows, mesmo que alguém encaminhe a porta no roteador. Só um pedido de pareamento por vez é mostrado na tela.
- No celular (Android 12 ou mais novo), **Permitir** um acesso exige desbloquear o aparelho, e os detalhes do pedido ficam ocultos na tela de bloqueio.
- No celular, a chave do aparelho e o segredo do código de autorização ficam cifrados pelo **Android Keystore** (chave no hardware seguro do celular), e a tela do app não aparece em capturas, gravações nem na lista de apps recentes.
- As atualizações automáticas só são instaladas se a impressão digital (SHA-256) do arquivo bater com a publicada no GitHub; o instalador fica travado contra alterações entre a conferência e a execução.
- O TransLocal não tem servidor nem banco de dados na internet: não existe conta online nem tabela de usuários para invadir (por isso conceitos como RLS de banco de dados não se aplicam). Toda a confiança fica nos aparelhos, entre si.
- **Acesso remoto** é desligado por padrão, só aceita aparelhos pareados (não permite parear de fora) e cada acesso precisa ser aprovado (celular, tela do PC ou código de autorização). Códigos não podem ser reutilizados; 5 códigos errados bloqueiam por 10 minutos; endereços com 5 conexões inválidas são bloqueados por 5 minutos.
- O controle do PC (mouse, teclado, controles) continua exigindo *Permitir que o outro computador use…* no PC.
- Aparelhos pareados podem ser removidos em *Configurações e ajuda › Aparelhos pareados*.
- O serviço do TransLocal (programas de administrador e confirmações do UAC) é opcional e desligado por padrão. Ele roda o executável instalado em *Arquivos de Programas* (que programas comuns não conseguem alterar), não escuta a rede e só recebe comandos do TransLocal da mesma sessão do Windows, por um canal local verificado dos dois lados. Ressalva: como no Parsec e em outras ferramentas de acesso remoto com esse recurso, um programa malicioso que já esteja rodando na sua conta poderia tentar usá-lo para aprovar confirmações do UAC; desligue a opção se não precisar. Registros do serviço e do agente: `%ProgramData%\TransLocal`.

### Usar como segundo monitor (área estendida)

O TransLocal transmite um monitor existente. Para estender a área de trabalho, instale no computador que transmite um driver de monitor virtual (Indirect Display Driver, por exemplo o projeto *Virtual Display Driver*), ajuste-o em *Configurações › Sistema › Tela* e escolha esse monitor na aba **Transmitir**. Com a placa de captura HDMI isso já acontece: o notebook enxerga um monitor externo (use Win+P).

## Solução de problemas

- **O receptor não aparece na busca:** confira se o TransLocal está aberto no outro computador com “Aceitar transmissões” ligado e se o firewall está liberado (aba *Configurações e ajuda*). Digite o endereço mostrado na aba *Receber* do outro computador.
- **Cabo USB-C não cria rede:** os dois computadores precisam de USB4/Thunderbolt e o cabo precisa ser Thunderbolt/USB4. Veja se aparece “USB4 P2P Network Adapter” em *Gerenciador de Dispositivos › Adaptadores de rede*.
- **HDMI travando em 5 quadros/s:** escolha um formato MJPEG (placas USB 2.0 só chegam a 30/60 quadros/s com compressão). Feche OBS, Câmera ou Teams se a placa estiver em uso.
- **Imagem preta em parte da tela:** vídeos protegidos (DRM) não podem ser capturados pelo Windows.
- **Controle de jogo não funciona:** na barra da janela de exibição (leve o mouse ao topo) aparece o estado dos controles. “Falta o driver no outro PC” → instale o suporte a controles no PC. Na aba *Receber* aparece a lista de controles detectados neste computador; se o seu não aparecer, confira o pareamento Bluetooth; se aparecer como “não reconhecido”, use **Configurar botões**.
- **Imagem lenta ou travando no computador que exibe:** atualize os dois para a 1.7+ (exibição pela placa de vídeo), marque *Pedir transmissão leve* nele e, no transmissor, deixe *Resolução enviada: Automática*. Nas estatísticas, “atraso da imagem” mostra quanto o quadro demora; se o pico subir muito de vez em quando, a causa costuma ser o Wi-Fi — prefira 5 GHz perto do roteador, ou cabo.
- **Wi-Fi caindo ou internet sem DNS durante a transmissão (precisando reiniciar o roteador):** as versões anteriores enviavam a tela em blocos JPEG, que em jogos chegam a 35–50 Mbit/s — roteadores domésticos sobrecarregados costumam parar de responder o DNS. Atualize os dois computadores para a 1.6+ (vídeo H.264, ~6× menos rede). Se possível, ligue o PC ao roteador por cabo.
- **Perco o controle quando abro o Gerenciador de Tarefas, um jogo não responde ao teclado/mouse, ou a imagem congela ao abrir um jogo:** o programa roda como administrador, ou o Windows está mostrando a confirmação do UAC (o diagnóstico mostra “tela segura do Windows”). Ative *Controlar programas de administrador e confirmações do Windows (UAC)* na aba **Transmitir** do PC (instala o serviço), ou desmarque “Executar como administrador” nas propriedades do jogo (*Compatibilidade*). Ctrl+Alt+Del não pode ser enviado por nenhum programa.
- **Jogo em tela cheia aparece congelado ou preto no notebook:** a partir da 1.4 a captura aceita jogos em 10 bits/HDR. Se mesmo assim o jogo bloquear a captura (tela cheia exclusiva), a janela de exibição avisa; no jogo, troque o modo de tela para **Janela sem bordas** (Borderless).
- **Mouse “preso” para um lado em jogos:** o modo relativo entra quando o jogo esconde/prende o cursor; se o jogo não fizer isso, use **Ctrl+Alt+Shift+M**. Jogos com anti-cheat que bloqueiam entrada simulada podem ignorar o controle remoto.
- **“Não está pareado” ao pedir acesso de fora:** o pareamento precisa ser feito antes, em casa, na mesma rede.
- **Celular não recebe os pedidos:** confira no app se aparece “Conectado a …”; o celular precisa alcançar o PC (mesma rede ou Tailscale) e ter permissão de funcionar em segundo plano. Sem isso, use o código de autorização.
- **Registros:** aba *Configurações e ajuda › Abrir pasta de registros* (`%LOCALAPPDATA%\TransLocal`).
- **Diagnóstico ao vivo:** durante a transmissão, os dois computadores trocam (pela mesma conexão criptografada, uma vez por segundo e em mensagens pequenas) os avisos e erros do registro e um resumo do estado — método de captura, programa em primeiro plano, se ele roda como administrador ou em tela cheia, modo do mouse. **Ctrl+Alt+Shift+D** mostra isso na janela de exibição, e cada sessão fica salva nos dois computadores em `%LOCALAPPDATA%\TransLocal\diagnostico`. Para relatar um problema: *Configurações e ajuda › Relatar um problema no GitHub…* abre a página do GitHub com o resumo da última sessão (sem endereços IP nem o nome do usuário) para você revisar e enviar. Dá para desligar o compartilhamento na mesma aba.

## Como funciona

- **Captura:** DXGI Desktop Duplication (a mesma API da gravação de tela do Windows, ~4 ms por quadro em 1080p), com GDI como reserva automática. O cursor é desenhado na imagem.
- **Codificação:** a tela é dividida em faixas de 64 px; só os trechos que mudaram são enviados, em JPEG codificado em paralelo (~4 ms para um quadro 1080p inteiro). Controle de fluxo com no máximo 2 quadros em trânsito mantém o atraso baixo mesmo quando a rede é mais lenta.
- **Rede:** TCP 52020 com TLS 1.2/1.3 (som, controle, acesso remoto, celular aprovador; vídeo quando o UDP não passa), vídeo por UDP (porta escolhida a cada transmissão, AES-GCM + Reed-Solomon) e UDP 52021 (descoberta por broadcast em todas as interfaces, inclusive 169.254.x.x).
- **Autenticação:** identidade ECDSA P-256 por aparelho; quem aceita a conexão envia um desafio que quem conecta assina junto com as duas chaves e a finalidade; pareamento por comparação de código (compromisso e revelação); código de autorização TOTP (RFC 6238).
- **Captura resiliente:** quando um jogo assume a tela, a captura é recriada com espera crescente (0,2 s → 3 s) e, após falhas seguidas, o dispositivo de vídeo é reiniciado — sem sobrecarregar o PC.
- **Som:** loopback WASAPI do transmissor → PCM 16 bits → reprodução com descarte de atraso acumulado.
- **Vídeo H.264:** Media Foundation (codificador da placa de vídeo, perfil Baseline, sem quadros B, buffer de ~1 quadro, quadro-chave só quando o receptor pede, taxa de bits adaptativa pela medição de atraso de cada quadro). No receptor: decodificador do Windows e exibição por DXGI (modelo flip) + processador de vídeo D3D11 + Direct2D para os textos.
- **Redução de resolução:** média de área (separável, ponto fixo, linhas em paralelo), ~6 ms para 1920×1080 → 1366×768; quando a tela não muda, captura, redução e compressão são puladas.
- **Controles de jogo:** o receptor lê os controles pela SDL3 (USB/Bluetooth, com XInput direto como reserva) a cada 4 ms e envia só as mudanças; o transmissor os reproduz como controles de Xbox 360 virtuais (ViGEmBus) e devolve a vibração.
- **HDMI:** dispositivos UVC via DirectShow (FlashCap), com repasse opcional do áudio da placa.

Bibliotecas: [NAudio](https://github.com/naudio/NAudio) (MIT), [FlashCap](https://github.com/kekyo/FlashCap) (Apache-2.0), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) — detalhes em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Compilar

Requisitos: SDK do .NET 10 e, para o instalador, Inno Setup 6 (`winget install JRSoftware.InnoSetup`). Para o app Android: `dotnet workload install android` e as dependências (`dotnet build android\TransLocal.Android -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=… -p:JavaSdkDirectory=… -p:AcceptAndroidSDKLicenses=True`). O app Android usa os mesmos arquivos de protocolo e segurança do Windows (`Protocol.cs`, `Security\*`, `Streaming\RemoteAccess.cs`).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # dist\win-x64\TransLocal.exe + dist\TransLocal-Setup-1.0.0.exe
powershell -ExecutionPolicy Bypass -File build.ps1 -IncludeArm64   # também dist\win-arm64\TransLocal.exe
```

Diagnóstico (resultados em `%LOCALAPPDATA%\TransLocal\TransLocal.log`):

```powershell
TransLocal.exe --selftest <pasta>   # transmissão completa pela interface local; salva a imagem recebida
TransLocal.exe --bench              # mede captura, codificação e decodificação
TransLocal.exe --gamepad-test       # cria um controle virtual e confere pelo XInput (requer ViGEmBus)
TransLocal.exe --update-test <pasta> # consulta o GitHub, baixa e confere a versão nova, troca o exe numa pasta de teste
```

Estrutura:

```
src/TransLocal/
  Program.cs              entrada, instância única, modos de linha de comando
  Updater.cs              atualização automática pelas Releases do GitHub
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
