# Componentes de terceiros

O executável do TransLocal inclui os componentes abaixo, distribuídos sob as licenças indicadas.

| Componente | Versão | Licença | Projeto |
|---|---|---|---|
| .NET Runtime e Windows Forms | 10.0 | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/winforms |
| NAudio (Core, Wasapi, WinMM, Midi) | 3.3.0 | MIT | https://github.com/naudio/NAudio |
| FlashCap (FlashCap, FlashCap.Core) | 1.12.0 | Apache-2.0 | https://github.com/kekyo/FlashCap |
| Vortice.Windows (Direct3D11, DXGI, DirectX) | 3.8.3 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.Mathematics | 2.1.0 | MIT | https://github.com/amerkoleci/Vortice.Mathematics |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | https://github.com/SharpGenTools/SharpGenTools |
| Nefarius.ViGEm.Client | 1.21.256 | MIT | https://github.com/ViGEm/ViGEm.NET |
| SDL3 (Simple DirectMedia Layer) | 3.5 | zlib | https://github.com/libsdl-org/SDL |
| QRCoder | 1.8.1 | MIT | https://github.com/Shane32/QRCoder |
| System.ServiceProcess.ServiceController (serviço do Windows) | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| .NET para Android (app de celular) | 10.0 | MIT | https://github.com/dotnet/android |
| ppy.SDL3-CS (ligações .NET da SDL3) | 2026.1002.1 | MIT | https://github.com/ppy/SDL3-CS |

O driver **ViGEmBus** (BSD-3-Clause, https://github.com/nefarius/ViGEmBus) não é distribuído com o TransLocal: quando o usuário pede, ele é instalado pelo winget a partir do pacote oficial.

Textos completos das licenças:

- MIT: https://opensource.org/license/mit
- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0
- zlib (SDL): https://www.libsdl.org/license.php — “Copyright (C) 1997-2026 Sam Lantinga”

O instalador é gerado com o [Inno Setup](https://jrsoftware.org/isinfo.php) (licença própria, uso gratuito).
